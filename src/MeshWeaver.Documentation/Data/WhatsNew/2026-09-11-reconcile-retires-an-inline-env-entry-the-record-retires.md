---
Name: Reconcile retires an inline env entry the record retires
Category: Feature
Description: An inline env entry on a portal's Deployment — never rendered by the chart and never removed by a helm upgrade — can now be removed through the API. Mark it retiredBy on the Deployment record and run Reconcile; the operator re-measures that the pod falls back to the same value before it removes the entry from every container.
Icon: Cloud
Order: -20260911
---

# Reconcile retires an inline env entry the record retires

An environment variable set **inline** on a portal's Deployment outranks every other source of
configuration, and nothing in a repository creates or removes one: the chart never renders it, and
a `helm upgrade` leaves it where it is. Until now, the only way to take one off was a `kubectl`
command run with cluster credentials — exactly what operating through the portal is meant to end.
The audit could see these entries; nothing could act on them.

## What changed

`Reconcile` now carries a **retire inline env** step. It reads one field of the Deployment record
that had no effect until now — `inlineEnv[].retiredBy` — and removes exactly the entries that
carry it:

1. Mark the entry on the record: `retiredBy` (the issue that ends it), `shadows` (the source the
   pod should fall back to) and, for a credential, `agreesWithShadowed: true`.
2. File a `Reconcile`. After the record is re-applied, the operator checks that no rollout is in
   progress, confirms that the pod really falls back to the source the record names, and compares
   the two values inside the cluster — reporting only lengths and a verdict, never a value. Only if
   they are **equal** does it remove the entry, from every container that carries it, in one
   guarded change. The run then waits for the rollout and ends in the usual audit.
3. Once the audit no longer lists the entry, drop it from the record.

An entry that cannot safely go is refused by name, and the whole `Reconcile` with it: one that is
the only source of its key (removing it would leave the key empty), a credential whose equality with
its fallback was never recorded, or any value the record says differs from what the pod would fall
back to.

## When you can use it

It needs two things on the control instance: an operator image that includes the new
`hosting-inline-env-retire` command, and the Hosting module version that plans it (1.17). Until both
are there, a `Reconcile` over a record that retires an entry stops at that step without removing
anything. The procedure is in
[Operating from the portal](/Doc/Architecture/OperatingFromThePortal) and
[Deployment env layers](/Doc/Architecture/DeploymentEnvLayers).

Removing an inline credential stops the next reader; it does not undo a disclosure. A credential
that has sat in plaintext on a Deployment still needs rotating afterwards.
