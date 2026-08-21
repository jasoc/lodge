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

That's it. On a machine with nothing installed, this installs the pinned .NET SDK and
Node (via nvm), starts a Postgres container, and opens a tmux session with the server and
the UI dev server each in their own pane. The server migrates its own schema on first
boot. Visit `http://localhost:4200`.

```bash
./scripts/run.sh --stop     # stop the tmux session (the DB container keeps running)
./scripts/dev-db-down.sh    # tear the DB container down too
./scripts/prod-test.sh      # build and run the real single-container image via docker compose
```

## What's here

- `inventory/acme/` — an invented example: one capability (`services`), one instance
  (`demo`). Replace it with your own kind(s) and instance(s); nothing in the code is
  specific to this example.
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
