# Threat model

What Lodge protects, from whom, and what it does *not* protect against. Read it before
deploying Lodge anywhere that more than one person (or a CI job) can influence the
inventory. Where a control is configuration rather than code, it says so.

## What Lodge is, security-wise

Lodge is **a way to make a docker daemon run things on behalf of whoever can change a Git
repository**, with an approval step in front of some of them. Everything else follows from
that.

Assets, roughly in order of what an attacker wants:

1. **The docker daemon Lodge talks to**, and through it the host (or remote host) it runs on.
2. **Secrets** Lodge resolves for actions (provider tokens, vault tokens, ssh keys passed as inputs).
3. **The systems the playbooks manage** (Proxmox, DNS, anything an HTTP action calls).
4. **The history** in Postgres: tamper with it and Lodge believes something was or wasn't done.
5. **Lodge's own identities**: personal and service tokens, the OIDC client secret.

## Trust boundaries and the trust you are extending

### The docker socket is root-equivalent

An `executor: container` action needs a docker daemon, normally the host's, through
`/var/run/docker.sock` mounted into the Lodge container (or `DOCKER_HOST`, see
`docs/deployment/containers.md`). **Anyone who can make the daemon run a container can become
root on that host**: mount `/` into a container, `--privileged`, `--pid=host`. Everything Lodge
does to confine action containers (dropped capabilities, read-only rootfs, no host mounts, no
`privileged` key, network *profiles* instead of network names) limits what a *playbook* can do
once it runs; it does not limit a process that has the socket. Concretely:

- **A compromised Lodge server process is root on the docker host.** Run it where that is
  acceptable: a dedicated host or VM for Lodge and its playbooks, not the machine that holds
  everything else.
- **Whoever controls what the server asks docker to run controls the host.** That is the
  inventory (below), and anyone with API access to confirm actions.
- To narrow the blast radius: a **socket proxy** (only the endpoints Lodge uses: create, start,
  logs, wait, kill, rm, image build/inspect, ps) via `DOCKER_HOST=tcp://…`, or a **remote daemon**
  on a throwaway build host via `DOCKER_HOST=ssh://…` so that "root on the docker host" is not
  "root on the Lodge host". Rootless docker/podman helps further.

### The inventory is code

Merging to the branch Lodge tracks decides *which containers run, with which images, which
Dockerfiles are built and which scripts execute*. A playbook folder is arbitrary code; a
capability can name any `image`. So **write access to the inventory repository is equivalent to
the ability to run code on the docker host**, gated only by the action's policy and by humans
reviewing and confirming. Controls:

- **Review the repository like code**: branch protection and CODEOWNERS on playbooks,
  capabilities and `kind.yaml` (`docs/templates/CODEOWNERS.inventory`).
- **Pin what runs.** A built playbook is pinned by content fingerprint: an approved action never
  runs different code (the executor refuses a changed folder). Base images in the examples are
  pinned by digest; do the same for yours.
- **An action's snapshot is what a human approves.** Executor config, inputs, policy and group
  are all part of it; a change re-queues the action for a fresh confirmation.

### `AUTO` actions have no human gate

`policy: AUTO` runs at the next reconciliation cycle with nobody in front of it, and `requires:`
doesn't apply to it (it gates humans, not the reconciler). That is the point, and the risk:

- Anything an AUTO action does, **anyone who can merge to the inventory can trigger** by making
  its rule match.
- An `AUTO` action with a `container.build` runs a Dockerfile unattended; that choice belongs to
  whoever maintains the inventory, which is why the inventory needs review (below) and why
  playbook folders deserve the strictest CODEOWNERS.
- Keep `AUTO` for things that are safe to run repeatedly and unattended, never for what creates,
  destroys or spends. The example's creating/destroying actions are all `MANUAL_REQUIRED`.
- Prompt inputs make an action unable to be AUTO (it would have nobody to ask).

### Secrets

Secrets are references in the inventory (`secret: NAME`) and are resolved by the server's
`ISecretProvider` only when an action starts; they are never stored resolved or written to the
audit log. They are then **handed to the container as environment variables**, so:

- Any code in a playbook can read them. A playbook you don't trust must not be given a secret.
- Run logs mask exact resolved values (and their JSON-escaped forms, and the lines of
  multi-line ones). A playbook that transforms a secret before printing it (base64, a hash, URL
  encoding) is not masked. After a server restart the values are gone, so the output of a run that
  is reattached to is not logged at all.
- The server holds the means to resolve every secret any action may name. A compromised server
  reads them.
- With `Secrets:Provider=Env`, secrets are the server's own environment variables, visible to
  anyone who can inspect the container or `docker inspect` it. Prefer a vault-backed provider where that matters.

### Identity and approval

- `Auth:Mode=NoAuth` mints an admin token for **anyone who can reach the API**. It is for a single
  operator on a trusted network, behind nothing else; never expose it. Use OIDC otherwise.
- `requires: <group>` is checked on the action row for every human confirm, retry and
  invalidate (admins pass). Group membership comes from the identity provider's token claim at
  login, so it is as trustworthy as the IdP.
- A service token carries scopes (`actions`, `reconcile`); a leaked one with `actions` can confirm
  and invalidate any action it is allowed to, so keep them narrow, short-lived, and out of repos.
- The verification pattern (`inventory/homelab`) deliberately gives a playbook a service token with
  the `actions` scope so that it can invalidate; scope and rotate it accordingly.

### Postgres

The database is the source of "what has been done". Whoever can write to it can make Lodge believe
a destructive action was or wasn't performed, mint tokens, or alter users. Treat it like the secret
store it effectively is: network-isolate it, back it up, restrict who can connect. Lodge never
writes to the Git repository, so the repository's history is the one record an attacker with
database access cannot rewrite.

## What the container confinement does and doesn't do

By default an action container has no capabilities, `no-new-privileges`, a read-only root
filesystem with tmpfs scratch space, a non-root user, nothing of the host mounted, and a timeout;
resource caps and a network profile are available. That is **defence in depth against a
mistaken or lightly malicious playbook**, not a sandbox against a determined one:

- A container shares the host kernel; a kernel or runtime escape is out of scope.
- Every default can be relaxed by the action (`security:`), and an action that relaxes it is
  reviewed through the same inventory process as everything else, so review `security:` blocks.
- A container on a network profile can reach whatever that network reaches, including Lodge's own
  API and Postgres if they share it. Use separate networks.

## Out of scope / known limits

- **Lodge doesn't observe reality.** It compares the inventory with its own history of successful
  actions; a change made outside Lodge is invisible unless you add a verification action.
- **No isolation between kinds or instances** inside one Lodge: it is one trust domain. Use
  separate deployments to separate tenants.
- **Supply chain of playbook dependencies** (apt, pip, provider downloads inside a Dockerfile) is the
  playbook author's to pin and verify; Lodge fingerprints the folder, not what its build downloads.
- **Denial of service** by someone who can edit the inventory or call the API (many actions, long
  builds) is limited only by timeouts and the cycle's single-flight.

## Checklist

- [ ] Lodge runs on a host (or against a remote daemon) where "root on the docker host" is acceptable.
- [ ] The inventory repository has branch protection and CODEOWNERS on playbooks and capabilities.
- [ ] No `AUTO` action creates, destroys or spends; every AUTO action that builds a playbook is deliberate and its folder is reviewed.
- [ ] `Auth:Mode=Oidc` unless it is one operator on a trusted network; service tokens are scoped and rotated.
- [ ] Secrets come from a provider you trust; no playbook you don't trust is given one.
- [ ] Postgres is network-isolated and backed up.
- [ ] CI runs `lodge validate`, the schema drift check and the Trivy scan.
