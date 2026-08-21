# Deployment scenario: homelab

The default profile this repo ships for. One host, one container, no identity provider,
no groups — you are the only actor, and the audit trail exists so you can answer "wait,
why did that happen?" at 2am, not to enforce RBAC on yourself.

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
│                                   runbooks/*.sh         inventory/, schemas/       │
│                                   (bind mount)           (your Git working tree)   │
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
cp .env.example .env          # defaults are already homelab-shaped
./scripts/prod-test.sh        # docker compose up --build, the real production image
```

`./scripts/prod-test.sh` is literally `docker compose up`, using the same
`docker-compose.yml` you'd run on the actual box — "test" only means "you're running it
locally before trusting it," not "different from production." For day-to-day dev
instead of a deployment test, `./scripts/run.sh` is the zero-touch native loop (see the
root README).

For the real deployment, put this behind whatever you already use to reach your homelab
— Tailscale, a Caddy/nginx reverse proxy with a real cert, or nothing at all if it's
LAN-only. Lodge doesn't care; it only ever sees plain HTTP from whatever's in front of
it. If you do put a TLS-terminating proxy in front, nothing in Lodge needs to change —
there's no OIDC redirect URI to keep in sync in this profile.

## Modeling your homelab

The shipped `inventory/acme/` is a placeholder — a `services` capability toggling one
instance's `web.enabled` flag. Replace it with your own `kind` (the *type* of thing
you're governing — e.g. `host` for bare-metal boxes, `service` for the containers running
on them) and one `instance` per concrete thing:

```
inventory/
  host/
    capabilities/
      backups.yaml          # signal: backup.enabled → restic runbook (AUTO) /
                             #         decommission runbook (MANUAL_REQUIRED)
    instances/
      nas/
        instance.yaml       # kind_code: host, instance: nas, display_name: "Synology NAS"
        state.yaml          # backup: { enabled: true }
      media-server/
        instance.yaml
        state.yaml
```

Runbooks are plain shell scripts (`runbooks/*.sh`), invoked with `LODGE_INSTANCE_CODE`
and `LODGE_PARAM_*` env vars — see the shipped `runbooks/provision-service.sh` for the
shape. Point a signal's `runbook:` at a script path directly; no alias config needed
unless you want a friendlier name in the capability YAML.

## RBAC in this profile

`inventory/{kind}/runbook-permissions.yaml` is consulted by `FilePermissionResolver`,
but `local-admin` always has `IsAdmin = true` and admins bypass every grant — so an
empty or missing permissions file (the shipped default) is fine here. There's no one to
restrict.

## Secrets

`EnvSecretProvider` (the default `ISecretProvider`) reads whatever a runbook needs
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
