# Lodge

A governance control plane for your homelab (or anything else you'd rather not click
through by hand). Lodge reads declarative state from a Git-backed inventory, reconciles
it against confirmed history, and turns drift into gated, auditable actions — each a
container run or an API call your infrastructure already knows how to answer. `AUTO`
actions run themselves; `MANUAL_REQUIRED` ones wait for you (or for a member of the group
an action `requires`).

The CLI (`lodge`) and the web UI are peer clients of the same API — nothing you can do in
one, you can't also do in the other.

![An instance's Capabilities view: per-capability action cards, a failed manual action waiting for a retry, and a custom VM sizes view](resources/instance-capabilities.png)

## Prerequisites

The scripts never install anything: they check that each tool is present at the pinned
version and stop with an error if it isn't. Install these once:

| Tool | Version | Pinned in |
|------|---------|-----------|
| Docker Engine + compose plugin | any recent | — |
| .NET SDK | `10.0.100` (or a later `10.0.1xx` feature band) | `global.json` |
| Node.js | `22.23.1`, exactly | `ui/.nvmrc` |
| [process-compose](https://github.com/F1bonacc1/process-compose) | any recent | — |

```bash
# Docker: https://docs.docker.com/engine/install/ — then let your user talk to the daemon
sudo usermod -aG docker "$USER"   # log out and back in afterwards

# .NET SDK, via Microsoft's install script (or your distro's package, if it ships 10.0.1xx)
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version 10.0.100 --install-dir "$HOME/.dotnet"
# add to your shell profile:  export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"

# Node, via nvm (https://github.com/nvm-sh/nvm) — any version manager works, as long as
# `node --version` matches ui/.nvmrc when the scripts run
curl -fsSL https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.1/install.sh | bash
(cd ui && nvm install)            # reads ui/.nvmrc

# process-compose, via its official installer, into ~/.local/bin
sh -c "$(curl --location https://raw.githubusercontent.com/F1bonacc1/process-compose/main/scripts/get-pc.sh)" -- -d -b "$HOME/.local/bin"
```

When you bump a pin (`global.json`, `ui/.nvmrc`), install the new version the same way;
the scripts will refuse to start until you do. With nvm the scripts switch to the pinned
Node on their own; with any other manager, switch to it before running them.

## Quick start

```bash
./scripts/run.sh
```

This runs the stack from `process-compose.yaml` in a TUI: the dev Postgres
(`docker-compose.yml`), the server and the UI dev server, each with its own log. The
server migrates its own schema on first boot. Visit `http://localhost:4200`.

```bash
./scripts/run.sh            # quitting the TUI leaves the stack running; re-run to reattach
./scripts/run.sh --stop     # stop everything, Postgres included (data stays in ./data/postgres)
./scripts/dev-db-up.sh      # just the dev Postgres, e.g. to run the server from an IDE
./scripts/dev-db-down.sh    # remove the Postgres container (--wipe also deletes its data)
./scripts/prod-test.sh      # build the server image and run the production stack locally
```

## Compose files

- `docker-compose.yml` — **development only**: a single Postgres on `localhost`, data in
  `./data/postgres`, configured by `.env` (created from `.env.example` on first run).
- `docker-compose.prod.yml` — **production**: the Lodge server image, its Postgres, and
  Keycloak (with its own Postgres) for SSO, all state in named volumes, configured by
  `.env.prod` (copy `.env.prod.example` and fill in every `CHANGEME`). The server image
  is `ghcr.io/jasoc/lodge`, built from the root `Dockerfile` and published by the manual
  `docker-publish` workflow; `up --build` (or `./scripts/prod-test.sh`) builds it from the
  checkout instead, under the same `LODGE_SERVER_IMAGE` tag.

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d
```

## What's here

- `inventory/homelab/` — the shipped example: one instance (`lab`) with a list of VMs,
  each created (AUTO) and destroyed (after confirmation) by a Terraform playbook that runs
  in a container. Every `inventory/<kind>/` folder is a kind; replace it with your own —
  nothing in the code is specific to this example.
- `server/` — the .NET backend (Core domain + reconciler, Infrastructure, the single
  ASP.NET Core host that serves both the API and the built UI).
- `ui/` — the Angular 21 SPA.
- `cli/` — the `lodge` command-line client.
- `migrations/` — the Postgres schema; the server applies it to itself on every startup.
- `docs/VISION.md` — what Lodge is, what it does, and why it's shaped this way. Read
  this first if you're new.
- `docs/AGENTS.md` — the technical architecture reference: domain vocabulary, invariants,
  extension points. Read this before making non-trivial changes.
- `docs/deployment/` — two worked deployment scenarios:
  [homelab](docs/deployment/homelab.md) (`NoAuth`, one host, one product) and
  [company](docs/deployment/company.md) (`Oidc` SSO, IdP groups gating actions, multiple
  products/tenants, service tokens for CI).

## License

See [LICENSE](LICENSE).
