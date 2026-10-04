# Containers: what an action's container may do, and where it runs

An `executor: container` action runs one container on a docker daemon. This page is the
reference for how that container is confined, what an action can ask for, and how to
point Lodge at a different daemon. For why the daemon itself is a trust boundary, see
`docs/deployment/` and the compose file's note on the docker socket: **access to a docker
socket is root-equivalent on that host**, and everything below limits what a *container*
can do, not what someone who can merge to the inventory can ask the daemon to run.

## The `container:` block

```yaml
executor: container
container:
  image: "alpine:3.21@sha256:…"          # or: build: { context: playbooks/x }
  command: ["reachable"]
  timeout_seconds: 60                    # killed after this long (default: server, 3600 s)
  resources: { memory: 64m, cpus: 0.5, pids: 32 }
  network: internal                      # a profile the server defines, see below
  security:                              # relaxations of the defaults, see below
    user: image
    read_only_rootfs: false
```

Every field after `command` is optional, and all of them are part of the action's
snapshot: editing one re-queues pending actions for a fresh confirmation, like editing a
playbook does.

| Field | Meaning |
|---|---|
| `timeout_seconds` | 1–86400. A container still running after this is killed (`docker kill`, by its `lodge.run_id` label) and the run fails with "timed out". Omitted: `DockerExecutor__DefaultTimeoutSeconds` (3600; `0` = unlimited). |
| `resources.memory` | docker size (`512m`, `2g`); swap is capped to the same amount. |
| `resources.cpus` | number of CPUs, e.g. `0.5`. |
| `resources.pids` | most processes the container may create. |
| `network` | a **profile name**, not a docker network (see below). |
| `security` | explicit relaxations of the restrictive defaults (below). |

## Restrictive by default

With no `security:` block a container is created with:

| Default | docker flag |
|---|---|
| all Linux capabilities dropped | `--cap-drop=ALL` |
| no privilege gain through setuid/file caps | `--security-opt no-new-privileges` |
| read-only root filesystem | `--read-only` |
| writable scratch space | `--tmpfs /tmp` and `--tmpfs /work` (`mode=1777,nosuid,nodev`, size `DockerExecutor__TmpfsSize`, 256m); the work dir is exported as `LODGE_WORK_DIR` |
| a non-root user | `--user 65534:65534`, unless the image declares its own non-root `USER` (then that one is kept) |

The image's `WORKDIR` is left alone. Anything else a script writes (a `$HOME`, a cache)
needs a path under `/tmp` or `/work`, or a `security.tmpfs` entry.

Every default is undone **explicitly, per action**, in `security:` — so the inventory
shows exactly what an action is allowed beyond them:

| `security` field | Effect |
|---|---|
| `cap_add: [NET_ADMIN]` | adds the named capabilities back (`--cap-add`); the drop-all stays. |
| `no_new_privileges: false` | omits `no-new-privileges`. |
| `read_only_rootfs: false` | omits `--read-only` (and the default tmpfs mounts). |
| `user: image` | keep whatever the image says, root included. `user: "1000:1000"` (or a name) forces one. `auto` is the default. |
| `tmpfs: [/var/cache]` | extra writable tmpfs mounts. |

There is deliberately no `privileged`, no host mount and no host network: nothing of the
host is ever mounted into an action container.

`inventory/homelab` shows the relaxed end: its Terraform/Ansible/compose playbooks run as
root and write to `/root`, so each action says
`security: { user: image, read_only_rootfs: false }` (once, with a YAML anchor per file).

## Network profiles

An inventory never names a docker network — that would let whoever edits it attach a
container to any network on the host. It names a **profile**, and the server maps profiles
to networks:

| Profile | Docker network |
|---|---|
| *(omitted)* / `default` | `DockerExecutor__Network`, else docker's default bridge |
| `none` | no network at all |
| anything else | `DockerExecutor__NetworkProfiles__<name>=<docker network>`; an unconfigured name fails the run before anything starts |

## Run logs

A run's stdout/stderr go to its log, which anyone who can see the action can read. Every
**resolved secret value** (an input declared `secret:`) is masked as `***` there — also its
JSON-escaped form (it travels inside `LODGE_PARAMS_JSON`) and each line of a multi-line
secret. What can't be caught is a secret the container *transforms* (base64, URL-encoding,
a hash): only the exact values Lodge resolved are known. After a server restart the
resolved secrets are gone with the process that held them, so a run that Lodge reattaches
to keeps its exit code but its output is **not** written to the log.

## Restarts and orphans

Containers are labelled `lodge.managed`, `lodge.deployment`, `lodge.action_id` and `lodge.run_id`. A run id is
recorded on the action *before* anything starts, so a crash never runs an action twice; a
restarted server finds a run's container by its `lodge.run_id` label and reads the real
exit code. At startup, every managed container of **this deployment** that does not belong
to a RUNNING action is removed (leftovers of finished or lost runs). A container of a RUNNING
action — on this or another replica — is left alone, and so is any container of another Lodge
deployment sharing the daemon: containers carry a `lodge.deployment` label, by default a hash
of the database the server uses. Replicas that reach the database under different host names
should set the same `DockerExecutor__DeploymentId` explicitly.

## Where the containers run: `DOCKER_HOST`

Lodge calls the `docker` CLI, which talks to whichever daemon the standard environment
points at: the local socket (`/var/run/docker.sock`) by default, or **`DOCKER_HOST` on the
Lodge container itself**. There is no inventory key for it, on purpose: where code runs is
the operator's decision, not something a commit can change.

```bash
# .env.prod — a remote daemon over ssh instead of the local socket
DOCKER_HOST=ssh://deploy@build-host
```

For `ssh://`, three things have to be true inside the Lodge container:

1. **An ssh client.** The runtime image installs `docker.io`, `docker-buildx` and
   `openssh-client` (docker execs `ssh … docker system dial-stdio`; without the client an
   `ssh://` host fails to start).
2. **A key.** Mount a directory with the private key at `/root/.ssh` (the container runs as
   root, `HOME=/root`), e.g. `./docker-ssh:/root/.ssh:ro` — the commented line in
   `docker-compose.prod.yml`.
3. **A `known_hosts` that already lists the host.** There is nobody to answer ssh's
   "trust this host?" prompt, so an unknown host key fails the connection. Generate the
   entry once, out of band, and check the fingerprint yourself:
   `ssh-keyscan -H build-host >> docker-ssh/known_hosts`.

Remove the `/var/run/docker.sock` mount when you do this. The remote host needs a docker
daemon and the `docker` CLI for the ssh user (`docker system dial-stdio`). Image builds
send the playbook folder as the build context over the connection; `DockerExecutor__Network`
and `NetworkProfiles` name networks **on that daemon**. Anything else compatible with
`DOCKER_HOST` works the same way — `tcp://` to a socket proxy that exposes only the
endpoints Lodge needs is the other common choice, and narrows what the daemon will accept.
