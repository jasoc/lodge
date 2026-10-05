# Homelab inventory (example)

An example inventory that models a homelab. **It executes nothing**: the playbooks behind
every action are mocks, so you can confirm any action and watch the whole engine work
(actions, dependencies, policies, run logs, audit) with no credentials, no Proxmox, no
Cloudflare and no ssh.

Everything is in `instances/lab/instance.yaml`:

- `proxmox.images`: cloud images (none declared in the example, the capability is there).
- `proxmox.virtual_machines`: the VMs. Each one holds its Proxmox spec, its Ansible
  profile, and `compose`, the list of files from `stacks/` it runs (names or globs).
- `cloudflare.zones.<zone>.records`: the DNS records.

| Capability | Item | Actions (policy) |
|---|---|---|
| `images` | one image | add: approve → download → verify; update (MANUAL); delete: approve → delete; scan (OPTIONAL, 2 min) |
| `virtual_machines` | one VM | add: approve → create → configure → verify → register; update (MANUAL); destroy: approve → destroy; plan and audit (OPTIONAL, 2 min each) |
| `stacks` | one compose file on one VM | add: deploy → healthcheck, behind the VM's approval; update (MANUAL); remove: approve → remove; scan (OPTIONAL, 2 min) |
| `dns` | one record | add: approve → create → verify; update (MANUAL); delete: approve → delete; watch (OPTIONAL, 2 min) |

## One click, then it runs itself

Creating or destroying something starts with a single MANUAL action, `approve_*`, that has
`executor: none`: it runs nothing and exists only to be confirmed in the UI. Every step after
it is AUTO and `depends_on` the one before, so confirming the approval makes the rest of
the chain run on its own, one step as soon as the previous finishes. Stacks wait on their VM's
`configure_vm`, so a new VM needs one approval for itself and all its stacks.

Editing an existing item (`update_*`, `apply_vm_changes`) is one MANUAL action, not a gated
chain: a `depends_on` is satisfied by any earlier success of its target, whatever the item
looked like then, so after the first edit an approve step would already count as done.

The OPTIONAL actions (`plan_vm`, `audit_vm`, `scan_*`, `watch_record`) wait for nobody, take
two minutes (`sleep_seconds: "120"`) and come back after each run, which makes them handy to
watch a long run in the UI.

## The mock playbooks

`playbooks/terraform`, `playbooks/ansible` and `playbooks/compose` each build one tiny
image (`busybox`) whose entrypoint is `playbooks/_base/mock-playbook.sh`, passed to each
build as the `base` additional context. A run prints the tool, the action and the main
inputs it received (`module`, `unit`, `name`, `host`, `file`, ...), waits 2 seconds (or
`sleep_seconds`, when the action sets it) and exits 0. No secret is read and no network is
needed beyond pulling `busybox`.

The capabilities are written exactly as they would be for the real tools, so this folder
doubles as a template: to make it real, replace each playbook folder with a real image
(Terraform, Ansible, `docker compose` over ssh) and give the kind its credentials in
`kind.yaml` (`defaults.inputs`).

## Stacks

`stacks/` is one flat folder of compose files; a VM's `compose` entries name them (or glob
them: `pangolin-*`). Each matching file is one item, keyed `<vm>/<file>`, and its content is
part of the item: editing the file updates the stack, dropping it from the list removes it.
Here they are only data handed to the mock; nothing is deployed.
