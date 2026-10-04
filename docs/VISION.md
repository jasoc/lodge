# VISION.md — what Lodge is, what it does, and why

`docs/AGENTS.md` is the technical reference: vocabulary, invariants, file layout. This
document is the one before it — what problem Lodge exists to solve, and why it took the
shape it did. Read this first if you're new; read `AGENTS.md` before you touch code.

## The problem

Any system you operate for real accumulates a pile of things that need a human's
attention sometimes and not other times: provision this, resize that, rotate this
credential, decommission that service, apply this patch, sync that setting. Each one
individually is easy — a script, a button in some admin panel, an API call. The problem
is never any single action. It's that there ends up being one of these per system, each
with its own UI (or no UI — a script someone has to remember exists), its own notion of
"did this already run," its own ad-hoc audit trail (a Slack message, if you're lucky), and
its own access control (or none).

Lodge exists because the actual want was simpler than any of that: **one UI for every
button.** One place to see "here is everything that could be done, here is what's
actually pending, here is who did what and when" — regardless of which system, product,
or homelab box the button belongs to.

## Why a reconciler, not a task list

A flat list of "things you can click" doesn't hold together on its own — it can't tell
you *when* a button should even appear, and it has no memory of what's already been done
versus what's newly true. The model that actually solves this is the one Kubernetes
uses for everything: declare what you *want*, compare it continuously to what you
*have*, and let the difference — the drift — be the thing that produces work.

So Lodge borrows that shape wholesale. A **Kind** is a CRD: the schema, the vocabulary of
signals and rules for one type of governed thing. An **Instance** is one concrete object
of that Kind. Desired state lives in YAML, the same way it would in a Kubernetes
manifest. A reconciliation cycle diffs desired state against confirmed history and turns
every difference into an **Action** — the "button" from the original want, now with a
identity, a status, and a policy: `AUTO` (Lodge just does it), `MANUAL_REQUIRED` (a human
has to click), or `OPTIONAL` (available, never required).

That `MANUAL_REQUIRED` state is also where the name comes from: a lodge is a chamber
where an order convenes to decide, and *to lodge* something — a complaint, an appeal — is
literally what a pending action is doing: waiting on you.

## What it actually does

- Reads declarative state from Git (a local working tree or a real GitHub repo) — the
  single source of truth, never mutated by Lodge itself.
- Diffs it against confirmed history on every reconciliation cycle (a timer, a button, an
  API call, a CI job — all the same coalesced operation underneath).
- Turns every drift into an auditable Action with resolved inputs, a policy, and — for
  `MANUAL_REQUIRED` actions that need more than the inventory already provides — pending
  prompts a human fills in at confirm time.
- Executes confirmed/AUTO actions through a pluggable executor (a container on the local
  docker daemon, or an HTTP call to something else entirely — Octopus Deploy, an internal
  ops API, whatever already knows how to actually perform the operation). Lodge governs; it never
  performs the operation itself.
- Records every state transition as an immutable audit event.
- Gates who can run what by group membership, resolved identically whether the identity
  came from no-auth local admin or a real SSO login.
- Exposes all of the above through one API, with a CLI and a web UI as equal peers of
  it — nothing one can do that the other can't.

## Why it's shaped the way it is

- **One process, one container.** The API, the UI, and the schema migration are one
  deployable unit on purpose — the whole point was fewer moving pieces to operate, not
  more. See `docs/AGENTS.md` for the concrete invariants this implies.
- **Kind/Instance, not Product/Tenant.** The two-level hierarchy is structurally sound
  regardless of what's actually being governed — a homelab NAS is exactly as valid an
  "Instance" as a SaaS customer environment. The vocabulary was deliberately generalized
  away from its SaaS-flavored origin so the same engine serves both without a fork.
- **Auth is two planes, one mechanism.** A human (personal token, via SSO or an implicit
  local-admin) and automation (a scoped service token, for CI/CD) authenticate through
  the exact same bearer-token path. Only how a token gets minted differs by profile —
  see `docs/deployment/homelab.md` and `docs/deployment/company.md` for both ends of
  that range running on the identical codebase.
- **The reconciler is pure.** Given a capability catalog and a current state, it's a
  deterministic function to a set of actions — no I/O, no side effects. Everything
  imperative (the DB, the Git read, the execution) is the shell around that pure
  core, which is what makes "what would this produce" answerable without actually
  running anything.

## Who it's for

The engine doesn't know or care whether it's governing one NAS in a homelab or forty
customer environments across two SaaS products with an SSO-driven support org behind
them — both are just a `kind`/`instance` tree, a capability catalog, and an auth profile.
That range is the point: it's a homelab tool and the thing it was originally built to be
at BaxEnergy, running on the same code, differing only in configuration.
