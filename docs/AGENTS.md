# AGENTS.md — LLM context for the Lodge codebase

This file orients an AI agent (or a new contributor) working in this repository. Read it
before making changes; it encodes the domain vocabulary, the invariants you must not
break, the file layout, and the supported extension points. Unlike some docs in this
space, keep this one honest — if the code and this file disagree, trust the code and fix
this file. For *why* the project exists and is shaped this way — not covered here — see
`docs/VISION.md`.

## What Lodge is

Lodge is a **governance control plane**. Git (or a local working tree) is the single
source of truth (SSOT) for declarative inventory. Lodge reads it, reconciles desired
state against confirmed history, and turns each drift into an auditable **action** that
runs a container or calls an HTTP API. Lodge governs; the executor executes.

The whole thing runs as **one process, one container**: `Lodge.Server` serves the API,
serves the built Angular SPA as static files, runs the reconciliation loop, and migrates
its own Postgres schema on startup. Only Postgres is a separate container.

## Domain vocabulary

- **Kind** — a governed system type with its own capability/signal vocabulary, defined by
  `inventory/{kind}/capabilities/*.yaml`. Borrows the term deliberately from Kubernetes
  CRDs: a Kind is the schema, an Instance is one concrete thing of that Kind. (This was
  called "product" in an earlier, SaaS-flavored version of this idea — renamed because
  Lodge isn't multi-tenant SaaS software.)
- **Instance** — one concrete thing governed under a Kind, e.g. one homelab host, one k8s
  cluster, one customer environment if you're running this for a real multi-instance
  product. Desired state lives at `inventory/{kind}/instances/{instance}/*.yaml`. (Was
  "tenant".)
- **Capability** — a governed module of a Kind, defined by one file
  `inventory/{kind}/capabilities/{capability}.yaml`. It owns the signals it observes and
  the rules that map their values to actions. A capability whose signals have **no rules**
  is a **view**: it only gives a slice of the inventory a name and a card on each instance
  (`collection.*.field` signals sharing a collection render as one table, sorted by item
  key, one column per signal `label`), and is never reconciled.
- **Signal** — inside a capability: a dotted path (`scalar`, `keyed_collection`, or
  `scalar_list`) plus rules that match its current value and emit actions. A keyed
  collection may be nested with one `*` segment (`proxmox.virtual_machines.*.containers`:
  items keyed `vm/container`, a dependency on the parent signal waits for that item's own
  parent, and a recreated parent re-requires its children), and may `exclude:` item fields
  (a VM's `compose` list) from its bodies so editing them isn't a change to the parent. A
  scalar list with `files: <folder>` names files of the inventory (paths or `*`/`**`
  globs): each file is an item whose body is `{file, sha256, content}`, so editing the
  file is a MODIFY and its content travels with the action.
- **Action** — a concrete run produced by a rule. Has a `key`, `label`, `policy`, an
  explicit `executor` with its block, resolved inputs, any pending prompts, and an optional
  `requires: <group>`.
- **Requires** — who may confirm, retry or revoke an action: only members of that user
  group, and admins (the no-auth local admin, or the OIDC `AdminGroup`). Omitted (or
  `requires: nobody`) means anyone. AUTO runs ignore it (it gates humans, not the
  reconciler). It is part of a live row's snapshot: changing it re-queues.
- **Policy** — `AUTO` (runs immediately at reconciliation), `MANUAL_REQUIRED` (waits for a
  human to confirm), or `OPTIONAL` (available but never required). Unspecified defaults to
  `MANUAL_REQUIRED`.
- **Snapshot** — what a live (not yet run) row was emitted with: desired value, executor
  config, `requires`, policy, and inputs (resolved `from`/`const` values, secret
  references, prompts). Any difference from what the catalog emits now supersedes the row
  and queues a fresh one, so nobody confirms something that has changed under them. A
  RUNNING row is never superseded mid-flight.
- **Status** — `BLOCKED`, `QUEUED`, `RUNNING`, `SUCCEEDED`, `FAILED`, `SUPERSEDED`. An
  action whose `depends_on` targets haven't succeeded yet is still emitted, `BLOCKED`, in
  the same cycle as the change that calls for it — so the whole chain a change sets off is
  visible at once (the instance page's Graph tab) — and becomes `QUEUED` (or starts, if
  AUTO) on the first cycle that finds its dependencies satisfied.
- **Input** — an action parameter: `from` (resolved from the match context: `kind`,
  `instance`, `path`, `key`, `item`, `item.<field>`, `value`; for collections `collection`,
  every current item as one JSON object; for nested ones `parent_key`, `parent`,
  `parent.<field>`), `const` (fixed), `secret` (resolved by `ISecretProvider` at run time),
  or `prompt` (supplied by a human at confirm time).
- **Executor** — what runs an action, declared explicitly (`executor: container|http`) and
  dispatched by `CompositeRunbookExecutor`. `container` runs a ready-made `image` or a `build` folder of
  the inventory (`inventory/{kind}/playbooks/...`, plus optional `additional_contexts`
  shared between playbooks), built and cached by content fingerprint; parameters arrive as
  `LODGE_PARAM_*` env vars. The catalog never names a runtime: `ContainerRunbookExecutor`
  keeps approval, caching and run state, and delegates to an `IImageBuilder` +
  `IContainerRunner` pair (Docker today: `DockerImageBuilder`, `DockerContainerRunner`). `http`
  sends one request described by its `http:` block (`method`, `url`, `headers`, `query`,
  `body`, `timeout_seconds`, `expect_status`) with `{{ name }}` parameter references
  substituted at run time; its log shows the request and response with secrets masked.
  The executor config is part of the action's snapshot, so editing it (or a playbook
  folder) re-queues pending actions for fresh confirmation.
- **Kind defaults** — `inventory/{kind}/kind.yaml` may declare `defaults.inputs`, appended
  to every action of the kind (capabilities and instance overrides) that doesn't name that
  input itself; `name: ~` on an action drops a default. Typically the one secret every
  playbook needs, declared once and still listed on each action.
- **Users & groups** — `users`/`user_groups` tables, mirrored from the identity provider
  only: each OIDC login upserts the user and replaces their groups with the token's group
  claim, names 1:1. Lodge never edits them (the Users & groups page is a read-only view).
  Without auth there are no users: everyone is the implicit local admin. A personal
  token's groups are read live from here.
- **Sync cycle** — one reconciliation pass, recorded in `sync_cycles` with its events and
  every inventory/catalog validation error; the Reconciliation page shows the latest ones
  (`GET /api/v1/reconcile/cycles`).
- **past_history** — a tenant-YAML-equivalent, instance-YAML section declaring facts that
  already happened outside Lodge's governance (à la `terraform import`): the identity is
  adopted as a synthetic SUCCEEDED row instead of firing a real run for it.
- **Audit event** — an immutable record of every state transition.

## Invariants (do not break these)

1. **Lodge never writes to the inventory source.** It only reads the local working tree
   or polls GitHub — see `IInventorySource`.
2. **Lodge never executes operations itself** — only through `IRunbookExecutor`.
3. **Every state transition is audited** (`audit_events`).
4. **snake_case JSON everywhere**; `kind_code` on every kind-scoped row and DTO.
5. **Seams are drop-in.** `IInventorySource`, `ICurrentUserAccessor`, `IRunbookExecutor`,
   `ISecretProvider` all have a homelab-appropriate default and are swapped via DI only,
   in `Lodge.Infrastructure/DependencyInjection.cs`.
6. **`requires` is checked on the action row**, against the caller's current groups (admins
   pass), for every human confirm/retry/revoke (`ActionExecutionService`) — never in the
   UI alone.
7. **An `AUTO` action that carries a prompt is downgraded** to effectively
   `MANUAL_REQUIRED` (it cannot run unattended).
8. **At most one live row per action identity**, enforced by a partial unique index in
   Postgres (`ux_actions_live`) — not just application logic.
9. **The CLI and the UI are peer clients of the same API**, never a wrapper around each
   other. Anything one can do, the other can too.
10. **Auth is two planes, one mechanism.** A personal token (human) and a service token
    (automation) are both just rows in `api_tokens`, validated identically by
    `LodgeBearerAuthenticationHandler`. Only how a token gets minted differs: the `NoAuth`
    profile's `POST /api/v1/auth/login` mints one unconditionally; the `Oidc` profile's
    `/api/v1/auth/oidc/login` → `/oidc/callback` pair does the same from a real SSO
    Authorization Code + PKCE round trip the server drives itself (see
    `docs/deployment/company.md`). A service token additionally carries scopes
    (`ScopeEndpointExtensions.RequireScope`, e.g. `reconcile`, `actions`) — meaningless
    for a personal token, which is instead gated per action by `requires` like any human.

## File layout

```
inventory/{kind}/instances/{instance}/*.yaml   SSOT desired state (read-only to Lodge)
inventory/{kind}/instances/{instance}/overrides.yaml  instance-scoped capability overrides
inventory/{kind}/capabilities/*.yaml           capability definitions (signals → rules → actions)
schemas/common/instance.base.schema.json       shared JSON Schema every kind composes
schemas/kinds/{kind}.instance.schema.json      per-kind schema
inventory/{kind}/kind.yaml                     optional: display name, defaults for every action
inventory/{kind}/playbooks/                    container playbooks for `executor: container` actions
server/src/Lodge.Core/          domain entities, the pure reconciler, capability catalog, seam interfaces
server/src/Lodge.Infrastructure/ EF Core, git inventory sources, reconciliation loop, execution, auth, secrets
server/src/Lodge.Server/        single ASP.NET Core host — minimal-API endpoints + serves the built SPA (wwwroot)
server/tools/validate-schema/   dependency-free console app, CI schema validation gate
server/tests/Lodge.Tests/       xUnit tests
cli/src/Lodge.Cli/              thin HTTP client (`lodge` binary) — a peer of the UI, not a wrapper around it
ui/                             Angular 21 SPA (client-side rendered, no SSR) — built into wwwroot for deployment
migrations/                     Postgres schema; the server migrates itself with this on every startup
scripts/                        dev orchestration — see "Run and test" below
```

Key types: `Reconciler` (pure, stateless — everything else is the imperative shell around
it), `CapabilityCatalogLoader`/`CapabilityCatalog` (aggregates + validates capability
YAML), `ReconciliationRunner` (one cycle: sync inventory, diff, apply verdict),
`ReconciliationCoordinator` (single-flight wrapper any caller — timer, UI button, API,
CLI — goes through), `LodgeDbContext`, `CompositeRunbookExecutor`, `MigrationRunner`.
Note: the `Action` entity collides with `System.Action`; files outside its namespace use
`using ActionEntity = Lodge.Core.Domain.Entities.Action;`.

## Extension points

### Add a governed capability
Create `inventory/{kind}/capabilities/{capability}.yaml` with `capability`, `title`,
optional `description`, and `signals`. Each signal has a `path`, a `kind` (`scalar` by
default), and `rules` mapping matched values/triggers to an `actions` list. No code
change is required — `FileCapabilityCatalogProvider` aggregates every capability file for
a kind at reconciliation time, and its cache is invalidated every cycle so edits apply
live without a restart.

### Add an action
Give a rule an action with a `key` and an executor — no server config either way:
- a container: `executor: container` plus a `container:` block with `image:` or
  `build: { context: playbooks/<name> }` (add `additional_contexts: { base: playbooks/_base }`
  to share a folder between playbooks — `COPY --from=base` in their Dockerfiles);
- an API call: `executor: http` plus an `http:` block, e.g.
  `{ method: POST, url: "https://n8n.lan/webhook/{{ instance }}", headers: { Authorization: "Bearer {{ token }}" }, body: { vm: "{{ item }}" } }`
  with `token: { secret: N8N_TOKEN }` and `item: { from: item }` among its inputs.
Restrict it to a group with `requires: <group>`.

### Add a view
A capability with signals and no rules, e.g. `inventory/homelab/capabilities/vm_overview.yaml`
(`proxmox.virtual_machines.*.cores` labelled CPU, `.memory_mb` labelled RAM). A scalar `*`
path is only valid in a view.

### Plug real providers
Replace the default implementation in
`server/src/Lodge.Infrastructure/DependencyInjection.cs`:
- **SSO** → set `Auth:Mode = Oidc` and fill in `Auth:Oidc:*` (`Authority`, `ClientId`,
  `ClientSecret`, `GroupsClaim`, `AdminGroup`) — users and groups are then mirrored from
  the IdP at each login — already implemented against any
  standard OIDC provider via its discovery document, no code change needed. See
  `docs/deployment/company.md` for a worked example. `TokenCurrentUserAccessor` and
  `LodgeBearerAuthenticationHandler` don't change between profiles — the token
  *validation* path is already identity-agnostic.
- **Real vault** → implement `ISecretProvider` (replace `EnvSecretProvider`).
- **Another execution backend** → add an `ExecutorKind`, its config block in
  `CapabilityCatalogLoader`, and an executor behind `CompositeRunbookExecutor`; most
  external systems are already reachable with `executor: http`.
- **GitHub inventory** → set `Git:Provider = GitHub` (already implemented,
  `GitHubInventorySource`); `Local` (the default) reads the working tree directly.

## Deployment scenarios

`docs/deployment/homelab.md` and `docs/deployment/company.md` walk through the two
profiles end to end — topology, `.env`, and what changes (and what deliberately doesn't)
between a single-operator `NoAuth` box and a multi-product `Oidc` deployment with
IdP groups gating actions through `requires` and service tokens for CI.

## Run and test

```bash
./scripts/run.sh            # compose Postgres + native server + SPA under process-compose
./scripts/run.sh --stop     # stop the whole stack (data stays in ./data/postgres)
./scripts/dev-db-up.sh      # just the dev Postgres, e.g. to run the server from an IDE
./scripts/dev-db-down.sh    # remove the dev Postgres container (--wipe: and its data)
./scripts/prod-test.sh      # build the server image, run docker-compose.prod.yml (server + Postgres + Keycloak)

dotnet build server/Lodge.slnx   # expect 0 warnings / 0 errors
dotnet test  server/Lodge.slnx
```

The scripts never install tools: `scripts/lib/` only checks that docker, process-compose,
the .NET SDK satisfying `global.json` and the exact Node in `ui/.nvmrc` are available, and
fails with a pointer to the README's Prerequisites (the only place install steps live)
otherwise. `docker-compose.yml` is the dev Postgres and nothing else;
`docker-compose.prod.yml` is the production stack, configured by `.env.prod`. The only
file any script creates unprompted is a root `.env`, copied from `.env.example` on first
run; every variable in it is either what the official
Postgres image itself expects (`POSTGRES_*`) or the literal ASP.NET Core config key
(`Auth__Mode`, `Git__Provider`, ...) — nothing gets renamed in between.
