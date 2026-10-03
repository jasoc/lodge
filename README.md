# Lodge

A governance control plane for your homelab (or anything else you'd rather not click
through by hand). Lodge reads declarative state from a Git-backed inventory, reconciles
it against confirmed history, and turns drift into gated, auditable actions — each a
runbook invocation your infrastructure already knows how to run. `AUTO` actions run
themselves; `MANUAL_REQUIRED` ones wait for you.

The CLI (`lodge`) and the web UI are peer clients of the same API — nothing you can do in
one, you can't also do in the other.

## Quick start

```bash
./scripts/run.sh
```

That's it. On a machine with nothing installed, this installs the pinned .NET SDK, Node
(via nvm) and [process-compose](https://github.com/F1bonacc1/process-compose), then runs
the stack from `process-compose.yaml` in a TUI: Postgres (the `postgres` service of
`docker-compose.yml`), the server and the UI dev server, each with its own log. The
server migrates its own schema on first boot. Visit `http://localhost:4200`.

```bash
./scripts/run.sh            # quitting the TUI leaves the stack running; re-run to reattach
./scripts/run.sh --stop     # stop everything, Postgres included (data stays in ./data/postgres)
./scripts/dev-db-down.sh    # remove the Postgres container (--wipe also deletes its data)
./scripts/prod-test.sh      # build and run the real single-container image via docker compose
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
  [company](docs/deployment/company.md) (`Oidc` SSO, groups-based RBAC, multiple
  products/tenants, service tokens for CI).

## License

See [LICENSE](LICENSE).
