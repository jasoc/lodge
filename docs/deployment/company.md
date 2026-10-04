# Deployment scenario: company, multiple products, SSO, groups

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
      sso.yaml                              # signal: sso.enabled → configure-sso (http)
      compute.yaml                          # signal: compute.tier → resize (http, requires: orbit-support-l3)
    instances/
      northwind/
        instance.yaml                       # kind_code: orbit, instance: northwind
        state.yaml
      contoso/
        instance.yaml
        state.yaml

  beacon/                                   # product #2 — its own capability vocabulary
    capabilities/
      billing-sync.yaml
    instances/
      fabrikam/
        instance.yaml
        state.yaml
```

Each product keeps its own `capabilities/*.yaml` (its signal/rule vocabulary), and each
action in it says which group may run it with `requires:` — `orbit`'s support tiers and
`beacon`'s don't have to line up.

## Auth: `Oidc`

```bash
# .env.prod (in prod, these come from your real secret manager, not a committed file)
Auth__Mode=Oidc
Auth__Oidc__Authority=https://login.microsoftonline.com/<tenant-id>/v2.0
Auth__Oidc__ClientId=<app registration client id>
Auth__Oidc__ClientSecret=<app registration client secret>
Auth__Oidc__Scopes="openid profile email"
Auth__Oidc__GroupsClaim=groups
Auth__Oidc__AdminGroup=lodge-admins
```

### Running Keycloak alongside Lodge

`docker-compose.prod.yml` ships Keycloak (plus its own Postgres) next to the server, for
when you don't already have an IdP. Set `KEYCLOAK_HOSTNAME` and the passwords in
`.env.prod`, route both hostnames through your TLS reverse proxy, start the stack, then in
the Keycloak admin console:

1. Create a realm (e.g. `lodge`) — `Auth__Oidc__Authority` is
   `${KEYCLOAK_HOSTNAME}/realms/lodge`.
2. Create a confidential OpenID Connect client `lodge` (client authentication on, standard
   flow only) with valid redirect URI `https://<lodge host>/api/v1/auth/oidc/callback`;
   its credentials tab gives `Auth__Oidc__ClientSecret`.
3. Create a client scope `groups` with a *Group Membership* mapper (claim name `groups`,
   "Full group path" off, added to the ID token) and assign it to the client.
4. Create the groups your actions `require` plus `lodge-admins` (`Auth__Oidc__AdminGroup`),
   and put users in them.

The server sends `X-Forwarded-*`-aware callback URLs
(`ASPNETCORE_FORWARDEDHEADERS_ENABLED` is set in the compose file), so the proxy must
forward `X-Forwarded-Proto` and `X-Forwarded-Host`.

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

### Groups → `requires`

Whatever claim your IdP puts group membership in (`GroupsClaim`, default `groups`) is
mirrored into Lodge at every sign-in: the user is upserted and their groups replaced, names
1:1 (Entra ID groups → Lodge groups). The Users & groups page is a read-only view of them.
An action that `requires` a group can then only be confirmed, retried or revoked by its
members — and by `AdminGroup` members, who may run everything:

```yaml
# inventory/orbit/capabilities/compute.yaml (excerpt)
- key: resize
  label: "Resize compute"
  requires: orbit-support-l3
  executor: http
  http:
    method: POST
    url: "https://ops.internal/orbit/{{ instance }}/resize"
    headers: { Authorization: "Bearer {{ ops_token }}" }
    body: { tier: "{{ value }}" }
  inputs:
    ops_token: { secret: ORBIT_OPS_TOKEN }
    instance: { from: instance }
    value: { from: value }
```

No `requires` (or `requires: nobody`) means anyone signed in. The check runs on the server
against the caller's current groups; the Users & groups page also lists every group some
action requires, and warns when nobody is in it.

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
seam (the executors, the GitHub inventory source) needs to change.

## What carries over unchanged from the homelab profile

- Still one container for the server (API + SPA + self-migration); still only Postgres
  gets its own container/managed instance.
- The CLI and the UI are still peer clients of the same API — a support engineer using
  `lodge actions confirm` from a terminal hits the exact same `requires` check the UI's
  confirm button does.
- `IRunbookExecutor` is still the only thing allowed to touch the outside world —
  `executor: http` reaches Octopus Deploy, an internal ops API or anything else that speaks
  HTTP, `executor: docker` runs anything that fits in a container, without touching the
  reconciliation engine at all.
