# Deployment scenario: homelab

The default profile this repo ships for. One host, one container, no identity provider —
you are the only actor, and the audit trail exists so you can answer "wait, why did that
happen?" at 2am, not to enforce permissions on yourself.

## Topology

```
┌─────────────────────────── your server / NAS / mini-PC ───────────────────────────┐
│                                                                                     │
│   docker compose                                                                   │
│   ┌───────────────┐        ┌─────────────────────────────────────────────────┐    │
│   │  postgres      │◄──────│  lodge (server, API + built SPA, self-migrates)  │    │
│   │  16-alpine     │       └─────────────────────────────────────────────────┘    │
│   └───────────────┘                     │                    ▲                    │
│                                          │ reconcile loop     │ bind mounts (ro)   │
│                                          ▼                    │                    │
│                                   playbook containers   inventory/, schemas/       │
│                                   (host docker socket)   (your Git working tree)   │
└─────────────────────────────────────────────────────────────────────────────────────┘
                    ▲
                    │ optional: Tailscale / a reverse proxy with a cert,
                    │ if you want to reach it from outside the LAN
              your browser, `lodge` CLI
```

One container for everything except Postgres (see `docs/AGENTS.md` — Lodge is
deliberately one process). No Kubernetes, no ingress controller, no OIDC provider.

## Auth: `NoAuth`

```bash
# .env
Auth__Mode=NoAuth
```

`POST /api/v1/auth/login` mints a real bearer token unconditionally, bound to an
implicit `local-admin` identity — not a no-op. You still get a genuine audit trail
(every action, every confirm, every reconcile cycle is attributed to `local-admin`);
you just skip the identity-provider round trip, because there's no second person on
this network to distinguish yourself from.

The CLI and the UI both go through this the same way:

```bash
lodge login http://homelab.local:8080
# Logged in to http://homelab.local:8080 as local-admin.
```

The browser does the same thing silently — `PermissionsService`'s route guard calls
`ensureSession()`, which auto-logs in and never shows a login screen, because there's
nothing for you to log in *as*.

## Bring it up

```bash
git clone <your-fork> lodge && cd lodge
cp .env.prod.example .env.prod
# in .env.prod: set Auth__Mode=NoAuth, Git__Provider=Local, the Postgres password, and
# LODGE_BIND=0.0.0.0 if you're not putting a reverse proxy in front
./scripts/prod-test.sh        # builds the server image, then runs docker-compose.prod.yml
```

`docker-compose.prod.yml` is the same file you'd run on the actual box — "test" only
means "you're running it locally before trusting it," not "different from production."
It also defines Keycloak for the SSO profile; with `NoAuth` you don't need it, so on the
box start only the server and its database:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d postgres server
```

(`KEYCLOAK_*` still have to be set to something for the file to parse — any non-empty
value does when Keycloak never starts.) For day-to-day dev instead of a deployment test,
`./scripts/run.sh` is the native loop (see the root README).

For the real deployment, put this behind whatever you already use to reach your homelab
— Tailscale, a Caddy/nginx reverse proxy with a real cert, or nothing at all if it's
LAN-only. Lodge doesn't care; it only ever sees plain HTTP from whatever's in front of
it. If you do put a TLS-terminating proxy in front, nothing in Lodge needs to change —
there's no OIDC redirect URI to keep in sync in this profile.

## Modeling your homelab

The shipped `inventory/homelab/` is an example: one `lab` instance listing VMs, with a
`virtual_machines` capability that creates each VM (AUTO) and destroys a removed one (after
confirmation) via a Terraform playbook. Every `inventory/<kind>/` folder becomes a kind on
the next reconciliation cycle (an optional `kind.yaml` gives it a display name, and
`defaults.inputs` every action of the kind gets — e.g. the one secret all its playbooks need). Model your
own `kind` (the *type* of thing you're governing — e.g. `host` for bare-metal boxes,
`service` for the containers running on them) with one `instance` per concrete thing:

```
inventory/
  host/
    capabilities/
      backups.yaml          # signal: backup.enabled → restic container (AUTO) /
                             #         decommission container (MANUAL_REQUIRED)
    instances/
      nas/
        instance.yaml       # kind_code: host, instance: nas, display_name: "Synology NAS"
        state.yaml          # backup: { enabled: true }
      media-server/
        instance.yaml
        state.yaml
```

Every action names its executor. To call something that already has an API (n8n, Home
Assistant, a CI trigger), use `executor: http`: its `http:` block is the whole request
(`method`, `url`, `headers`, `query`, `body`), with `{{ name }}` replaced by the action's
inputs at run time — secrets included, and masked in the run log.

For tooling Lodge's image doesn't ship (ansible, terraform, ...), use `executor: container`.
The action can run a ready-made `image:` or a playbook folder with a Dockerfile under
`inventory/<kind>/playbooks/` (folders several playbooks share go in `additional_contexts`). Lodge builds that folder on the host's docker daemon
(`docker-compose.prod.yml` mounts `/var/run/docker.sock`) and caches the image by content hash.
The container receives the same `LODGE_PARAM_*` variables, plus `LODGE_PARAMS_JSON`.
Mounting the socket gives root-equivalent access to the host: anyone who can merge to the
inventory can run containers on it. `inventory/homelab/` is a working example: its VMs are
created and destroyed by `playbooks/terraform/`, configured by `playbooks/ansible/`, and
their compose stacks deployed by `playbooks/compose/`.

## Users and `requires` in this profile

None: everyone signs in as the implicit local admin, and admins may run every action, so a
`requires: <group>` (like the shipped homelab's `destroy_vm` → `admins`) only takes effect
once you switch to `Oidc` and your IdP's groups come in.

## Validation errors

An invalid instance or capability file stops what it describes from being reconciled.
The **Reconciliation** page lists the errors the last cycle found, the events of the
recent cycles, and has a button to reconcile right away.

## Secrets

`EnvSecretProvider` (the default `ISecretProvider`) reads whatever an action needs
straight from the container's environment — put them in `.env` alongside everything
else. There's no vault integration in this profile; for a single-operator homelab,
`.env` (never committed, root-owned file permissions) is the vault.

## What you don't get in this profile

- No SSO, no groups, no per-user audit distinction — see `docs/deployment/company.md`
  for that.
- No service tokens scoped to `reconcile`-only automation — `local-admin`'s token can do
  everything, which is fine when the only caller is you.
- `Git__Provider=Local` reads the working tree directly; if you want your homelab's
  desired state to live in a GitHub repo instead (so a phone edit through GitHub's own
  UI triggers reconciliation), switch to `Git__Provider=GitHub` — see `docs/AGENTS.md`'s
  "Plug real providers" section. Everything else about this profile stays the same.
