# VISION.md — what Lodge is, what it does, and why

`docs/AGENTS.md` is the technical reference: vocabulary, invariants, file layout. This
document comes before it: what problem Lodge exists to solve, the idea it's built on, and
why it took the shape it did. Read this first if you're new; read `AGENTS.md` before you
touch code.

## The problem

Any system you operate for real accumulates a pile of things that need a human's
attention sometimes and not other times: provision this, resize that, rotate this
credential, decommission that service, apply this patch, sync that setting. Each one
individually is easy: a script, a button in some admin panel, an API call. The problem
is never any single action. It's that there ends up being one of these per system, each
with its own UI (or no UI, just a script someone has to remember exists), its own notion
of "did this already run," its own ad-hoc audit trail (a Slack message, if you're lucky),
and its own access control (or none).

The actual want was simpler than any of that: **one UI for every button.** One place to
see "here is everything that could be done, here is what's actually pending, here is who
did what and when," regardless of which system, product, or homelab box the button
belongs to.

## The idea: a canvas with physics

A flat list of buttons doesn't solve this. It can't tell you *when* a button should even
exist, and it has no memory of what's already been done versus what's newly true. And
every tool that ships a fixed model of the world (a VM manager, a CMDB, a network source
of truth) only covers the part of the world its authors imagined.

So Lodge splits the problem in two.

**The canvas is yours.** Lodge has no built-in idea of what a VM, a customer, a DNS
record or a certificate is. You define the vocabulary: what kinds of things exist, what
each one looks like, what can be done to it, and who may do it. All of it is plain YAML
in a folder, with the scripts that do the actual work (Dockerfiles, Terraform modules,
Ansible roles) sitting right next to it. Nothing about your world is compiled into Lodge,
and nothing needs code to add.

**The physics is Lodge's.** On top of whatever you draw, a small, fixed set of rules
applies, the same for every kind of thing:

- Git is the only source of truth, and Lodge never writes to it.
- The world changes only through **actions**, never as a side effect.
- An action exists because the description and reality disagree, and stops existing
  once they agree.
- Every action carries a **policy** (does it run by itself, or wait for a human?) and,
  optionally, the **group** whose members may approve it.
- Lodge never performs the work itself: it hands it to an **executor** (a container, an
  HTTP call) and records the outcome.
- Every transition is an immutable **audit event**.

The canvas is free; the physics is not negotiable. That combination is the point: you
get to model your world however it actually is, and the engine still knows what to do
with it, because it never needs to understand your world, only the gap between what you
said and what has happened.

## Why a reconciler

The model that makes "when should this button exist?" answerable is the one Kubernetes
uses for everything: declare what you *want*, compare it continuously to what you
*have*, and let the difference, the drift, be the thing that produces work.

Lodge borrows that shape wholesale, minus the cluster. A **Kind** is a CRD: the schema
and vocabulary for one type of governed thing. An **Instance** is one concrete object of
that Kind. A reconciliation cycle diffs desired state against confirmed history and turns
every difference into an **Action**: the "button" from the original want, now with an
identity, a status, and a policy: `AUTO` (Lodge just does it), `MANUAL_REQUIRED` (a human
has to confirm), or `OPTIONAL` (available, never required).

The difference from a Kubernetes operator is where the controller lives. In Kubernetes,
each CRD needs someone to write a controller in code. In Lodge the controller is generic:
the rules that map "this changed" to "do this" are declared in the same YAML as the
schema. And the human is part of the loop by design, not an afterthought: a reconciliation
can stop and wait for the right person to say yes.

## The canvas, piece by piece

Everything below lives under `inventory/`, one folder per Kind:

```
inventory/homelab/
  kind.yaml                       the Kind's display name
  capabilities/*.yaml             what can be governed, and how
  instances/lab/instance.yaml     one concrete thing, and how it should be
  playbooks/terraform/            the work itself: a folder with a Dockerfile
  playbooks/ansible/
```

- **Instance:** the desired state of one thing, in whatever structure suits it. In the
  shipped example, `lab` is a homelab with Proxmox VMs, compose stacks, cloud images and
  Cloudflare DNS records. In a company it could be one customer environment.
- **Capability:** one governed concern of a Kind, such as "virtual machines" or "DNS
  records". It names the parts of the instance it watches (its **signals**: a value, a
  keyed collection, a list of files) and the **rules** that say what to do when an item
  is added, modified or deleted.
- **Action:** what a rule emits: a label, a policy, an optional `requires: <group>`, an
  executor, and its **inputs**, resolved from the matched item, fixed constants, secrets
  fetched at run time, or **prompts** a human fills in when confirming. Actions can
  depend on each other (configure a VM only after it's been created).
- **Playbook:** a folder with a Dockerfile, built and cached by content. Parameters
  arrive as environment variables. Editing a playbook re-queues the pending actions that
  use it, so nobody approves something that has since changed under them.
- **View:** a capability with signals and no rules. It never produces work; it just gives
  a slice of the inventory a name and a card, such as a table of every VM with its size.
- **`past_history`:** facts that already happened outside Lodge, adopted as done instead
  of executed, much like `terraform import`.

The instance page in the UI shows the same picture: the capabilities as cards with their
pending and finished actions, the raw inventory, and a **Canvas** tab that lays the
inventory out as a map and lights up the capability behind whatever you click.

## A worked example

From the shipped `homelab` inventory, the life of one VM:

1. You add `another-node` under `proxmox.virtual_machines` in `instance.yaml` and push.
2. The next reconciliation cycle sees a new item in a signal of the `virtual_machines`
   capability. Its `on: add` rule emits three actions:
   - **Create VM (Terraform)**, `MANUAL_REQUIRED`: it waits for you.
   - **Configure VM (Ansible profile)**, `AUTO`, depending on the first: it will run by
     itself, but only after the VM exists.
   - **Plan VM (Terraform, read-only)**, `OPTIONAL`: there whenever you want to look.
3. You confirm the creation. Lodge builds `playbooks/terraform` if its content changed,
   runs it with the VM's fields as parameters and the secret it needs, and keeps the
   run's log, one click away in the UI.
4. When it succeeds, the configuration runs on its own. If it fails, it stays on the card
   with its log and a Retry button.
5. Later you raise `memory_mb`. That's a modification, so **Apply VM changes** appears
   and waits for you.
6. One day you delete the VM from the file. **Destroy VM** appears, and only a member of
   `admins` can confirm it.

At every step, the audit log records who did what, when, with which inputs, and how it
ended. None of the words "VM", "Terraform" or "Proxmox" appear anywhere in Lodge's code.

## What it actually does

- Reads declarative state from Git (a local working tree or a real GitHub repo), the
  single source of truth, never mutated by Lodge itself.
- Diffs it against confirmed history on every reconciliation cycle (a timer, a button, an
  API call, a CI job: all the same coalesced operation underneath).
- Turns every drift into an auditable Action with resolved inputs, a policy, and, for
  `MANUAL_REQUIRED` actions that need more than the inventory provides, prompts a human
  fills in at confirm time.
- Executes confirmed and `AUTO` actions through a pluggable executor: a container on the
  local docker daemon, or an HTTP call to something else entirely (Octopus Deploy, an
  internal ops API, whatever already knows how to perform the operation). Lodge governs;
  it never performs the operation itself.
- Records every state transition as an immutable audit event.
- Gates who can run what by group membership, resolved identically whether the identity
  came from no-auth local admin or a real SSO login.
- Exposes all of the above through one API, with a CLI and a web UI as equal peers of
  it: nothing one can do that the other can't.

## Why it's shaped the way it is

- **One process, one container.** The API, the UI, and the schema migration are one
  deployable unit on purpose. The whole point was fewer moving pieces to operate, not
  more. See `docs/AGENTS.md` for the concrete invariants this implies.
- **Kind/Instance, not Product/Tenant.** The two-level hierarchy is structurally sound
  regardless of what's actually being governed: a homelab NAS is exactly as valid an
  Instance as a SaaS customer environment. The vocabulary was deliberately generalized
  away from its SaaS-flavored origin so the same engine serves both without a fork.
- **Auth is two planes, one mechanism.** A human (personal token, via SSO or an implicit
  local admin) and automation (a scoped service token, for CI/CD) authenticate through
  the exact same bearer-token path. Only how a token gets minted differs by profile;
  see `docs/deployment/homelab.md` and `docs/deployment/company.md` for both ends of
  that range running on the identical codebase.
- **The reconciler is pure.** Given a capability catalog and a current state, it's a
  deterministic function to a set of actions: no I/O, no side effects. Everything
  imperative (the DB, the Git read, the execution) is the shell around that pure core,
  which is what makes "what would this produce" answerable without running anything.

## What Lodge is not

- **Not an executor.** It doesn't know how to create a VM or call your API in any
  meaningful way; your playbooks and services do. Lodge decides *when*, *who* and *with
  what*, and remembers *what happened*.
- **Not a CMDB or a portal with a fixed model.** It has no opinion on what your world
  contains. If you can describe it as YAML and act on it with a container or an HTTP
  call, it fits.
- **Not a CI system.** Pipelines run when code changes; Lodge acts when the description
  of the world and the world disagree, and keeps the history of both.

## Neighbours

Lodge overlaps with several kinds of tools, and is none of them:

- **Developer portals** (Port, Backstage) let you model entities and attach self-service
  actions with approvals. Their actions are buttons you choose to press; in Lodge, the
  buttons appear because of drift. And the model lives in Git, not in a vendor's
  database.
- **Platform orchestrators** (Kratix, Crossplane, and generic control planes like kcp)
  share the reconciliation model, but need Kubernetes and controllers written in code,
  and don't treat human approval as a first-class step.
- **Runbook automation** (Rundeck, Kestra, StackStorm, AWX) runs jobs with permissions
  and logs, but has no model of what the world should look like, so it can't tell you
  which job needs running.
- **Sources of truth** (NetBox, Nautobot) model infrastructure with approvals and jobs,
  but within a fixed domain, extended through code.

## Who it's for

The engine doesn't know or care whether it's governing one NAS in a homelab or forty
customer environments across two SaaS products with an SSO-driven support org behind
them. Both are just a Kind/Instance tree, a capability catalog, and an auth profile.
That range is the point: it's a homelab tool and the thing it was originally built to be
at BaxEnergy, running on the same code, differing only in configuration.

## The name

A lodge is a chamber where an order convenes to decide, and *to lodge* something, a
complaint or an appeal, is literally what a `MANUAL_REQUIRED` action is doing: waiting
on you.
