# Homelab inventory

The homelab, mirroring the homelab repo's `terraform/`, `docker/` and Ansible profiles.
Everything is in `instances/lab/instance.yaml`:

- `proxmox.images`: cloud images downloaded onto a Proxmox datastore.
- `proxmox.virtual_machines`: the VMs. Each one holds its Proxmox spec, its Ansible
  profile, and `compose`, the list of files from `stacks/` it runs (names or globs).
- `cloudflare.zones.<zone>.records`: the DNS records.

| Capability | Item | Actions (policy) |
|---|---|---|
| `images` | one image | download / update / delete (MANUAL) |
| `virtual_machines` | one VM | create → Ansible profile (MANUAL → AUTO), update (MANUAL), destroy (MANUAL), plan (OPTIONAL) |
| `stacks` | one compose file on one VM | deploy (AUTO, after the VM's profile) / update when the **file** changes (AUTO) / remove (MANUAL) |
| `dns` | one record | create / update / delete (MANUAL) |

## Terraform: one module, one state per item

`playbooks/terraform` is a generic runner. An action names a module from
`playbooks/terraform/modules/` (`proxmox-vm`, `proxmox-image`, `cloudflare-dns-record`)
and passes its inputs, usually the whole inventory item. Fields the module doesn't
declare are dropped.

Each run generates a root that contains only that module. Its state is a workspace of the
Postgres backend (`root.tf`: `backend "pg"`) named after the item's path, for example
`proxmox.virtual_machines.docker-tools-node`, created on first use and deleted on destroy.

- **Apply and destroy are always exact.** They only ever touch that one item, with no
  `-target` needed.
- **Adding a resource type** means writing a module (declare a `# lodge-import:` address
  if it can be adopted) and a capability. `run.sh` doesn't change.
- **Providers** are pinned in `versions.tf` and configured in `providers/`. Their
  credentials come from `# pass:` markers.
- **Adopting an existing resource:** set `import_id` on the item. The module's comment
  shows the id format.
- **`validate` action:** `run.sh validate` generates and validates a root without secrets
  or state.

## Playbook images

`playbooks/terraform`, `playbooks/ansible` and `playbooks/compose` each build one image.
What they all need lives once in `playbooks/_base` (`lodge-lib.sh`, `install-pass-cli.sh`
with the pinned pass-cli version), passed to each build as the `base` additional context.
Their Dockerfiles use it with `COPY --from=base` and `RUN --mount=from=base`.

- A playbook's fingerprint covers its own folder plus `_base`. Editing one playbook
  re-queues only its own actions; editing `_base` re-queues all of them.
- Base images are pinned by digest, so a rebuild on a new host produces the image that was
  approved. Bump a digest with `docker buildx imagetools inspect <image>`.
- Build one by hand: `docker buildx build --build-context base=playbooks/_base playbooks/terraform`.

## Stacks

`stacks/` is one flat folder of compose files; a VM's `compose` entries name them (or glob
them: `pangolin-*`). Each matching file is one item, keyed `<vm>/<file>`. Where the homelab
repo had two different files with the same name on different nodes, the one not on
master-node carries the node as a suffix (`portainer-nomad.yml`, `backrest-backrest.yml`).

- **The file content is part of the item.** Editing the file updates the stack.
- **Removing a stack.** Dropping a file from the list removes it with `compose down`,
  using the last deployed content, so this works even after the file is deleted.
- **Project names.** The project is the file's `name:`, the same one `compose.sh` used, so
  running stacks are updated in place. Every file here sets one (`trilium.yml` says
  `name: home`, the name its old folder gave it); a file without one is named after itself.

## Setup

Lodge provides two things, in the server's `.env`:

```sh
PROTON_PASS_PAT=pst_...::...   # Proton Pass personal access token
# Terraform state: a URL Go's lib/pq understands (not an Npgsql "Host=...;" string),
# double-quoted — the scripts `source` .env, and an unquoted ';' would cut the value.
TF_PG_CONN_STR="postgres://${POSTGRES_USER}:${POSTGRES_PASSWORD}@postgres16-lodge-dev:5432/tfstate?sslmode=disable"
DockerExecutor__Network=lodge_default   # so playbook containers reach that Postgres by name
```

`kind.yaml` hands both to every action as the `pass_pat` and `tf_pg_conn_str` inputs
(`defaults.inputs`), so the capabilities don't repeat them.

- **The Terraform state database.** A `tfstate` database on Lodge's own Postgres server,
  next to Lodge's database: `tf-run.sh` creates it on first use if it's missing (the user
  needs CREATEDB), and the `pg` backend keeps one row per workspace in its
  `terraform_remote_state` schema. The playbook containers reach it on the docker network
  named by `DockerExecutor__Network` (in dev `lodge_default`, host `postgres16-lodge-dev`,
  port 5432 — the container's, not the one published on the host). A password with `@`,
  `:`, `/` or `#` must be URL-encoded in the connection string.

- **The token's access.** A new token can see no vault. Grant it the Homelab vault:

  ```sh
  pass-cli personal-access-token access grant --personal-access-token-name lodge --vault-name Homelab --role viewer
  ```

- **What each run fetches from Proton Pass.** Every run logs `pass-cli` (built into the
  images) in with the token, then fetches everything else from `Homelab/environments`:
  the provider credentials, `SSH_PUBLIC_KEY`, `SSH_PRIVATE_KEY_B64`
  (`base64 -w0 <key>`), and every `${VAR}` used in the compose files.
