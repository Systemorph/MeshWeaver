---
Name: The operator's permissions follow the chart
Category: Fix
Description: The first record-driven Reconcile of memex through the fixed operator stopped one step in, refused by the cluster — the operator's ClusterRole had never received a grant that had been in the repository for hours. The grant now travels with the chart on every deploy, and a test refuses a script that names a permission its role does not hold.
Icon: Shield
Order: -20260909
---

The hosting operator runs each instance action as a short-lived Job under its own identity, and
what that identity may do is one file in this repository — the `hosting-operator` ClusterRole.
On 2026-09-09 the first Reconcile of memex through the operator carrying the
namespace fix of 2026-09-08 got past the
line that had stopped every earlier run, rendered its six steps, and stopped at the first:

```
hosting-pv-resize: ERROR: could not read StorageClass azurefile-memex
  (Error from server (Forbidden): storageclasses.storage.k8s.io "azurefile-memex" is forbidden:
   User "system:serviceaccount:memex-ops:hosting-operator" cannot get resource "storageclasses")
```

## What was happening

The rule that grants exactly that read had been on `main` since the volume-capacity step landed.
Nothing applied it: the ClusterRole manifest was one `kubectl apply` in a README, run once from a
laptop when the operator was first set up, and the cluster's copy predated the rule. A second
script — the one that releases a torn-down instance's volumes — had been merged with no grant at
all, so no teardown could have completed either. Both are the same shape as the operator image
that only a laptop could move: an input of the control plane with no lane to carry it.

## What it does now

- The config repository's helm-release lane applies the ClusterRole from this repository at the
  same pin it renders the chart from, on every `adopt` and `deploy`. A grant and the step that
  needs it move together, and the log names every resource as `configured` or `unchanged`.
  A pin with no manifest is a red run, never a skipped step.
- `check-rbac-coverage.sh` in the operator's test suite reads every `kubectl <verb> <resource>`
  the scripts name and refuses one the ClusterRole does not grant — naming the script, the verb,
  the resource and the rule to add. It ran red on the manifest as it was, for both scripts.
- The volume-purge step's grant is added.

What the check cannot see is stated in the script: manifests handed to `kubectl apply`, resources
named at run time, and what helm does with the chart.
