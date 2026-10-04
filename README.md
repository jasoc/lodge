# Lodge

**A blank canvas for operations.** You describe your systems in plain files, the way you
think about them. Lodge keeps reality in line with that description, and asks you before
it does anything that matters.

![An instance's Capabilities view: per-capability action cards, a failed manual action waiting for a retry, and a custom VM sizes view](resources/instance-capabilities.png)

## The idea, in three steps

**1. Write down how things should be.** A folder of YAML files in a Git repository. What
goes in it is up to you: virtual machines, DNS records, customer environments,
certificates. Lodge has no built-in notion of any of them.

```yaml
# inventory/homelab/instances/lab/instance.yaml
proxmox:
  virtual_machines:
    another-node: { cores: 4, memory_mb: 4096, ip: 192.168.178.115/24 }
```

**2. Say what can be done about it.** Next to the description, you define what happens
when something appears, changes or disappears, and who is allowed to make it happen.
The work itself is a container (a Terraform, Ansible or any other script you put in the
same folder) or an HTTP call to a system that already knows how.

```yaml
# inventory/homelab/capabilities/virtual_machines.yaml
signals:
  - path: proxmox.virtual_machines
    kind: keyed_collection
    rules:
      - on: add
        actions:
          - key: apply_vm
            label: "Create VM (Terraform)"
            policy: MANUAL_REQUIRED          # wait for a human
            executor: container
            container: { build: { context: playbooks/terraform }, command: ["apply"] }
      - on: delete
        actions:
          - key: destroy_vm
            label: "Destroy VM (Terraform)"
            policy: MANUAL_REQUIRED
            requires: admins                 # and only these humans
            # ...
```

**3. Let Lodge watch the gap.** Lodge continuously compares what you wrote with the
record of what it has already done. Every difference becomes an **action**: some run on
their own, others wait in the UI for the right person to approve them. Every step is
logged. Change the file, and the actions follow; once an action has succeeded for the
current description, it disappears.

That's the whole model. Your files are the canvas, and Lodge supplies the rules that keep
it honest: Git is the only source of truth, every change goes through an action, every
action has a policy and an owner, and every run is recorded.

### What "drift" means here (and what it doesn't)

Lodge compares your files with **its own history of successful actions**, kept in
Postgres. It does **not** look at the systems themselves. If someone deletes a VM by hand,
or edits a DNS record in the provider's dashboard, Lodge doesn't notice: as far as it
knows, the VM was created and the record applied, so there is nothing to do. It is a
ledger of governed changes, not a continuous check of reality (that is what the
Kubernetes controllers it borrows its shape from do, and Lodge deliberately doesn't).

Closing that gap is a pattern you opt into per capability, not something the engine does.
Write an `OPTIONAL` action that looks at reality and, when it disagrees with what Lodge
believes was done, **invalidates** the matching succeeded action through Lodge's own API
(the same "invalidate" button the UI has). The next cycle then sees that action as never
done and offers it again. The check itself (a ping, an API read, a `terraform plan` that must
be empty) is yours to write, as a playbook next to the capability; Lodge only supplies the
invalidate endpoint and scoped service tokens (`lodge tokens create --scope actions`).

### Things already done: `past_history`

Whatever existed before Lodge was in charge isn't in its history, so it would show up as
work to do. Declare it as already done in the instance YAML, much like `terraform import`:

```yaml
# inventory/homelab/instances/lab/past_history.yaml
past_history:
  - action: "proxmox.virtual_machines[another-node].apply_vm"   # one action of one item
    description: "created by hand in 2024"
  # ...[some-vm].*   every action of that item; "*" (with a description) the whole instance
```

Lodge records those identities as succeeded (without running anything) the first time it
sees them. An entry covers its identity for as long as it stays in the file, so if you
invalidate one of those actions, the entry adopts it again: remove the entry once the
history is real.

If you know Kubernetes, this is the operator pattern without the cluster and without
writing a controller, with a human in the loop. If you know Terraform, it's a `plan` that
never stops running, over anything you can describe, where every line of the plan is a
button with permissions and an audit trail. The web UI and the `lodge` CLI are equal
clients of the same API.

**→ [docs/VISION.md](docs/VISION.md)** goes deeper: why it's built this way, the full
vocabulary, a worked example, and how it compares to tools you may already know.

## Getting started

### Try it in two minutes

You need Docker and nothing else. This runs the server and its Postgres with no login,
reading the example `inventory/` of this checkout:

```bash
git clone https://github.com/jasoc/lodge && cd lodge
docker compose -f docker-compose.quickstart.yml up -d
# open http://localhost:8080 — the example's VMs, stacks and DNS records show up as actions
docker compose -f docker-compose.quickstart.yml down -v     # when you're done
```

Nothing runs until you confirm it, and the example's playbooks need a real Proxmox and
secrets, so just look around: the instance page, the action graph, the audit log. The
stack mounts the docker socket (root-equivalent on that host) so container actions *can*
run; remove that line if you only want to look. It isn't for production: that's
[Run it for real](#run-it-for-real) below. To point it at your own inventory, replace
`inventory/homelab/` with your kind folders.

Check an inventory without a server, for example in CI:

```bash
dotnet run --project cli/src/Lodge.Cli -- validate inventory   # exits 1 on any problem
```

### Prerequisites

The scripts never install anything: they check each tool is present at the pinned
version and stop if it isn't.

| Tool | Version | Pinned in |
|------|---------|-----------|
| Docker Engine + compose plugin | any recent | — |
| .NET SDK | `10.0.100` (or a later `10.0.1xx` feature band) | `global.json` |
| Node.js | `22.23.1`, exactly | `ui/.nvmrc` |
| [process-compose](https://github.com/F1bonacc1/process-compose) | any recent | — |

<details>
<summary>Install commands</summary>

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
the scripts refuse to start until you do. With nvm they switch to the pinned Node on their
own; with any other manager, switch before running them.

</details>

### Run it locally

```bash
./scripts/run.sh
```

Starts the dev Postgres (`docker-compose.yml`), the server and the UI dev server under
process-compose, each with its own log. The server migrates its own schema on first boot.
Open `http://localhost:4200`.

```bash
./scripts/run.sh            # quitting the TUI leaves the stack running; re-run to reattach
./scripts/run.sh --stop     # stop everything (data stays in ./data/postgres)
./scripts/dev-db-up.sh      # just the dev Postgres, e.g. to run the server from an IDE
./scripts/dev-db-down.sh    # remove the Postgres container (--wipe also deletes its data)
./scripts/prod-test.sh      # build the server image and run the production stack locally
```

### Run it for real

One container (API + UI + schema migrations) plus Postgres, and optionally Keycloak for
SSO. Copy `.env.prod.example` to `.env.prod`, fill in every `CHANGEME`, then:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.prod up -d
```

This pulls `ghcr.io/jasoc/lodge` (published by the manual `docker-publish` workflow);
add `--build` to build it from the checkout instead. Two complete setups are written up
in [docs/deployment/homelab.md](docs/deployment/homelab.md) (no auth, one host) and
[docs/deployment/company.md](docs/deployment/company.md) (SSO, groups gating actions,
service tokens for CI). How containers are confined, and how to run them on another
machine with `DOCKER_HOST`, is in [docs/deployment/containers.md](docs/deployment/containers.md).

### Back up Postgres

Git holds your inventory, but **Postgres holds everything Lodge knows it has done**: the
action history, the audit log, the users and tokens. Lose it and Lodge believes nothing was
ever done: every item of your inventory shows up as a new action to run. Back it up like
any database that matters:

```bash
docker compose -f docker-compose.prod.yml exec -T postgres \
  pg_dump -U lodge lodge | gzip > "lodge-$(date +%F).sql.gz"
# restore into an empty database:  gunzip -c lodge-….sql.gz | docker compose … exec -T postgres psql -U lodge lodge
```

If you ever have to start from an empty database over systems that already exist, declare
them as already done with [`past_history`](#things-already-done-past_history) before the
first cycle, or the first cycle will queue their "create" actions. The run logs under
`/repo/data` (the `lodge-data` volume) are nice to keep but nothing depends on them.

## What's in the repo

- `inventory/homelab/`: a working example. Proxmox VMs created and destroyed with
  Terraform and configured with Ansible, compose stacks, DNS records and cloud images,
  all defined in YAML with their playbooks alongside. Replace it with your own: nothing
  in the code knows about it.
- `server/`: the .NET backend (domain and reconciler, infrastructure, the ASP.NET Core
  host serving both the API and the built UI).
- `ui/`: the Angular SPA.
- `cli/`: the `lodge` command-line client.
- `migrations/`: the Postgres schema, applied by the server on every startup.
- `docs/VISION.md`: what Lodge is and why. Start here.
- `docs/AGENTS.md`: the technical reference (vocabulary, invariants, extension points).
  Read it before changing code.
- `docs/THREAT-MODEL.md`: what Lodge trusts and what it protects (the docker socket is
  root-equivalent; `AUTO` runs with no human gate; the inventory is code). Read before deploying.
- `docs/templates/CODEOWNERS.inventory`: a CODEOWNERS template for the repository holding your inventory.
- `docs/deployment/containers.md`: what a container action may do (restrictive defaults,
  resources, timeouts, network profiles) and where it runs (`DOCKER_HOST`).
- `schemas/`: JSON Schemas for editor support, generated by `./scripts/gen-schema.sh`.

## License

See [LICENSE](LICENSE).
