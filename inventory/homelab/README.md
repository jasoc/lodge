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

Each run generates a root that contains only that module. Its state is named after the
item's path, for example `/state/proxmox.virtual_machines.docker-tools-node.tfstate`.

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

Lodge provides only two things, both in the server's `.env`:

```sh
DockerExecutor__Mounts__tfstate=/path/to/state/folder   # or volume:<docker volume>
PROTON_PASS_PAT=pst_...::...                            # Proton Pass personal access token
```

- **The token's access.** A new token can see no vault. Grant it the Homelab vault:

  ```sh
  pass-cli personal-access-token access grant --personal-access-token-name lodge --vault-name Homelab --role viewer
  ```

- **What each run fetches from Proton Pass.** Every run logs `pass-cli` (built into the
  images) in with the token, then fetches everything else from `Homelab/environments`:
  the provider credentials, `SSH_PUBLIC_KEY`, `SSH_PRIVATE_KEY_B64` (`base64 -w0 <key>`),
  and every `${VAR}` used in the compose files.
