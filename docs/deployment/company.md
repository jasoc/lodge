# Deployment scenario: company, multiple products, SSO, groups, RBAC

The profile this project was originally built for: several SaaS products, each with many
customer tenants, an SSO-driven support/ops team with tiered access, and automation
(CI/CD, scheduled jobs) that needs to act without a human in the loop. Everything below
maps onto Lodge's `kind`/`instance` model 1:1 — a `kind` *is* a product, an `instance`
*is* one customer's environment of it.

## Topology

```
                              company IdP (Entra ID / Keycloak / Okta / Auth0)
                                       ▲                        ▲
                          OIDC (server-driven,          groups in the ID token
                          Authorization Code + PKCE)     (support-l1, support-l2, ...)
                                       │                        │
┌──────────────────────────────────────┴────────────────────────┴───────────────────┐
│  company infra (a VM, an ECS/Cloud Run service, a k8s Deployment — one process      │
│  either way, see docs/AGENTS.md)                                                    │
│                                                                                      │
│   ┌──────────────┐        ┌───────────────────────────────────────────────────┐    │
│   │  postgres     │◄──────│  lodge (server, API + built SPA, self-migrates)   │    │
│   │  (managed:     │       └───────────────────────────────────────────────────┘    │
│   │  RDS/Cloud SQL)│                    │                                            │
│   └──────────────┘                     │ reads inventory/ (Git__Provider=GitHub)    │
│                                          ▼                                            │
│                                  your company's inventory repo on GitHub              │
│                                  (orbit/, beacon/ kinds, one dir per tenant)          │
└──────────────────────────────────────────────────────────────────────────────────────┘
        ▲                                          ▲
        │ browser (support engineers, via SSO)     │ lodge CLI + service token
   support-l1 / support-l2 / admins              GitHub Actions: `lodge reconcile`
                                                  after every merge to the inventory repo
```

Still one Lodge process (no SSR, no separate migration step, no split UI deployment —
the single-container shape from `docs/AGENTS.md` doesn't change for this profile). What
changes from the homelab scenario is: `Auth:Mode=Oidc` instead of `NoAuth`,
`Git:Provider=GitHub` instead of `Local` (your inventory now lives in a real company
repo, not the box's local disk), and more than one `kind`.

## Modeling two products, several tenants each

```
inventory/
  orbit/                                    # product #1
    capabilities/
      sso.yaml                              # signal: sso.enabled → configure-sso runbook
      compute.yaml                          # signal: compute.tier → resize runbook
    instances/
      northwind/
        instance.yaml                       # kind_code: orbit, instance: northwind
        state.yaml
      contoso/
        instance.yaml
        state.yaml
    runbook-permissions.yaml                # who may run orbit's runbooks

  beacon/                                   # product #2 — its own capability vocabulary
    capabilities/
      billing-sync.yaml
    instances/
      fabrikam/
        instance.yaml
        state.yaml
    runbook-permissions.yaml
```

Each product keeps its own `capabilities/*.yaml` (its signal/rule vocabulary) and its
own `runbook-permissions.yaml` (its RBAC map) — `orbit`'s support tiers and `beacon`'s
don't have to line up, because `FilePermissionResolver` scopes every grant per kind.

## Auth: `Oidc`

```bash
# .env (in prod, these come from your real secret manager, not a committed file)
Auth__Mode=Oidc
Auth__Oidc__Authority=https://login.microsoftonline.com/<tenant-id>/v2.0
Auth__Oidc__ClientId=<app registration client id>
Auth__Oidc__ClientSecret=<app registration client secret>
Auth__Oidc__Scopes="openid profile email"
Auth__Oidc__GroupsClaim=groups
Auth__Oidc__AdminGroup=lodge-admins
```

`Authority` works against any standard OIDC provider — Entra ID, Keycloak, Auth0, Okta —
because Lodge discovers everything else (`authorization_endpoint`, `token_endpoint`,
JWKS) from `{Authority}/.well-known/openid-configuration`. Nothing in the server is
Entra-specific.

**The server drives the whole OIDC dance itself** — the SPA and the CLI never talk to
the IdP directly (see `docs/AGENTS.md` invariant 10):

- A browser hitting a guarded route with no session lands on `/auth/login`, whose
  "Sign in with SSO" button navigates (full page load, not XHR) to
  `GET /api/v1/auth/oidc/login?ui_redirect=<spa-origin>/auth/callback`.
- `lodge login https://lodge.internal.example.com` does the same thing headlessly: it
  spins up a local loopback listener, opens your browser at
  `.../auth/oidc/login?cli_redirect=http://127.0.0.1:<port>/callback`, and captures the
  minted token when the server redirects back to it — the same shape as `gh auth login`.
- Either way, Lodge exchanges the authorization code (PKCE, server-side — the client
  secret never reaches the browser or the CLI), validates the ID token against the IdP's
  JWKS, and mints a Lodge-owned personal token from the resulting claims. Downstream of
  that token, authentication is identical to the `NoAuth` profile — same bearer-token
  handler, same `AuthenticatedUser` shape.

### Groups → RBAC

Whatever claim your IdP puts group membership in (`GroupsClaim`, default `groups`) lands
on `AuthenticatedUser.Groups`. `FilePermissionResolver` checks those against
`inventory/{kind}/runbook-permissions.yaml`:

```yaml
# inventory/orbit/runbook-permissions.yaml
permissions:
  - runbook: orbit-ops/configure-sso
    allowed_groups:
      - orbit-support-l2
      - orbit-support-l3
  - runbook: orbit-ops/resize-compute
    allowed_groups:
      - orbit-support-l3
```

Fail-closed: a runbook with no entry here is denied to every non-admin, no matter their
groups. `AdminGroup` (one group name, e.g. `lodge-admins`) grants full access across
every product — reserve it for people who should bypass every per-product map, not for
"senior support."

## Service tokens: CI/CD and scheduled automation

A merge to the inventory repo shouldn't wait for a human to click "reconcile." Mint a
scoped, non-human token once:

```bash
lodge login https://lodge.internal.example.com   # an admin does this once, interactively
lodge tokens create --display-name ci-reconcile --scope reconcile
# Created service token <id> (scopes: reconcile)
# This raw token is shown once — store it now, it cannot be retrieved again:
# lodge_svc_...
```

Store the raw value as a GitHub Actions secret (`LODGE_SERVICE_TOKEN`), then:

```yaml
# .github/workflows/reconcile.yml, in the inventory repo
on:
  push:
    branches: [main]
jobs:
  reconcile:
    runs-on: ubuntu-latest
    steps:
      - run: |
          lodge login https://lodge.internal.example.com --token "$LODGE_SERVICE_TOKEN"
          lodge reconcile
        env:
          LODGE_SERVICE_TOKEN: ${{ secrets.LODGE_SERVICE_TOKEN }}
```

A service token with only the `reconcile` scope can call `POST /api/v1/reconcile` and
nothing else — `ScopeEndpointExtensions.RequireScope` on the actions-confirm/invalidate
endpoints returns `403` for it, even though the CI job is otherwise fully authenticated.
Personal tokens (humans) are never scope-limited this way — a human is already governed
by `IsAdmin` + their groups; scopes exist specifically to keep an unattended credential
narrow. List and revoke tokens the same way:

```bash
lodge tokens list
lodge tokens revoke <id>
```

Both `lodge tokens create/list/revoke` require an admin identity — a support engineer's
own SSO-minted token can't create new service tokens, only `lodge-admins` can.

## Inventory source: GitHub, not the local disk

```bash
Git__Provider=GitHub
Git__GitHub__Owner=your-org
Git__GitHub__Repo=lodge-inventory
Git__GitHub__Branch=main
Git__GitHub__TokenSecretRef=GITHUB_INVENTORY_TOKEN   # resolved via ISecretProvider
```

`GitHubInventorySource` polls the branch head SHA and reads `inventory/{kind}/instances/`
via the Contents API — the reconciliation loop doesn't know or care whether it's reading
a local working tree or GitHub; same `IInventorySource` seam either way. A read-only,
repo-scoped GitHub token (fine-grained PAT or a GitHub App installation token) is enough;
Lodge only ever reads (invariant #1 in `docs/AGENTS.md` — it never writes to the
inventory source).

## Secrets: swap in your real vault

`EnvSecretProvider` — reading plain env vars — is the OSS default, not what you'd run at
this scale. `ISecretProvider` is a two-method seam
(`server/src/Lodge.Core/Abstractions/ISecretProvider.cs`); replace it in
`Lodge.Infrastructure/DependencyInjection.cs` with an implementation backed by whatever
you already run — Azure Key Vault, AWS Secrets Manager, Vault itself. Nothing above this
seam (runbook executors, the GitHub inventory source) needs to change.

## What carries over unchanged from the homelab profile

- Still one container for the server (API + SPA + self-migration); still only Postgres
  gets its own container/managed instance.
- The CLI and the UI are still peer clients of the same API — a support engineer using
  `lodge actions confirm` from a terminal hits the exact same RBAC check the UI's confirm
  button does.
- `IRunbookExecutor` is still the only thing allowed to touch the outside world — swap
  `ShellCommandRunbookExecutor` for `WebhookRunbookExecutor` (or both, dispatched by
  `CompositeRunbookExecutor`) to point runbooks at Octopus Deploy, an internal ops API, or
  anything else that speaks HTTP, without touching the reconciliation engine at all.
