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
invokes a runbook (a shell script or a webhook). Lodge governs; the runbook executes.

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
  the rules that map their values to actions.
- **Signal** — inside a capability: a dotted path (`scalar`, `keyed_collection`, or
  `scalar_list`) plus rules that match its current value and emit actions.
- **Action** — a concrete runbook invocation produced by a rule. Has a `runbook`, `label`,
  `policy`, resolved inputs, and any pending prompts.
- **Policy** — `AUTO` (runs immediately at reconciliation), `MANUAL_REQUIRED` (waits for a
  human to confirm), or `OPTIONAL` (available but never required). Unspecified defaults to
  `MANUAL_REQUIRED`.
- **Status** — `QUEUED`, `RUNNING`, `SUCCEEDED`, `FAILED`, `SUPERSEDED`.
- **Input** — a runbook parameter: `from` (resolved from the match context: `kind`,
  `instance`, `path`, `key`, `item`, `value`), `const` (fixed), or `prompt` (supplied by a
  human at confirm time).
- **Runbook** — an execution detail, not part of an action's identity: a shell command
  (`ShellCommandRunbookExecutor`) or an HTTP webhook (`WebhookRunbookExecutor`), dispatched
  by `CompositeRunbookExecutor` based on which one's alias map recognizes the `runbook`
  value. An unrecognized `runbook` value is executed by the shell executor as a literal
  command — so a capability can point straight at a script path with zero extra config.
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
5. **Seams are drop-in.** `IInventorySource`, `ICurrentUserAccessor`, `IPermissionResolver`,
   `IRunbookExecutor`, `ISecretProvider` all have a homelab-appropriate default and are
   swapped via DI only, in `Lodge.Infrastructure/DependencyInjection.cs`.
6. **RBAC is fail-closed** for non-admins: a runbook with no explicit grant is denied.
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
    for a personal token, which is instead gated by `IsAdmin` + RBAC like any human.

## File layout

```
inventory/{kind}/instances/{instance}/*.yaml   SSOT desired state (read-only to Lodge)
inventory/{kind}/instances/{instance}/overrides.yaml  instance-scoped capability overrides
inventory/{kind}/capabilities/*.yaml           capability definitions (signals → rules → actions)
inventory/{kind}/runbook-permissions.yaml      runbook → allowed groups
schemas/common/instance.base.schema.json       shared JSON Schema every kind composes
schemas/kinds/{kind}.instance.schema.json      per-kind schema
runbooks/                                      example shell scripts the shipped `acme` kind points at
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

### Add a runbook
Reference it in a capability's `actions[].runbook`. For a local script: either add an
alias in `ShellExecutor:Runbooks` config, or just use the script path directly as the
`runbook` value — the shell executor runs an unrecognized `runbook` string as a literal
command. For an external webhook: add an alias in `WebhookExecutor:Runbooks` pointing at
the target URL. Grant access by adding an entry to
`inventory/{kind}/runbook-permissions.yaml` mapping the `runbook` to `allowed_groups`;
missing entries are denied for non-admins.

### Plug real providers
Replace the default implementation in
`server/src/Lodge.Infrastructure/DependencyInjection.cs`:
- **SSO** → set `Auth:Mode = Oidc` and fill in `Auth:Oidc:*` (`Authority`, `ClientId`,
  `ClientSecret`, `GroupsClaim`, `AdminGroup`) — already implemented against any
  standard OIDC provider via its discovery document, no code change needed. See
  `docs/deployment/company.md` for a worked example. `TokenCurrentUserAccessor` and
  `LodgeBearerAuthenticationHandler` don't change between profiles — the token
  *validation* path is already identity-agnostic.
- **Real vault** → implement `ISecretProvider` (replace `EnvSecretProvider`).
- **Real runbook backends** → `IRunbookExecutor` already supports shell and webhook;
  `OctopusRunbookExecutor` is an unimplemented scaffold for anyone who wants to wire
  Octopus Deploy specifically.
- **GitHub inventory** → set `Git:Provider = GitHub` (already implemented,
  `GitHubInventorySource`); `Local` (the default) reads the working tree directly.

## Deployment scenarios

`docs/deployment/homelab.md` and `docs/deployment/company.md` walk through the two
profiles end to end — topology, `.env`, and what changes (and what deliberately doesn't)
between a single-operator `NoAuth` box and a multi-product `Oidc` deployment with
groups-based RBAC and service tokens for CI.

## Run and test

```bash
./scripts/run.sh            # zero-touch: Postgres in Docker, server + SPA in a tmux session
./scripts/run.sh --stop     # stop the tmux session (DB container keeps running)
./scripts/dev-db-down.sh    # tear down the dev Postgres container
./scripts/prod-test.sh      # build and run the single production container via docker compose

dotnet build server/Lodge.slnx   # expect 0 warnings / 0 errors
dotnet test  server/Lodge.slnx
```

`./scripts/run.sh` also installs nvm/Node and the pinned .NET SDK if they're missing —
see `scripts/lib/`. The only file any script creates unprompted is a root `.env`, copied
from `.env.example` on first run; every variable in it is either what the official
Postgres image itself expects (`POSTGRES_*`) or the literal ASP.NET Core config key
(`Auth__Mode`, `Git__Provider`, ...) — nothing gets renamed in between.
