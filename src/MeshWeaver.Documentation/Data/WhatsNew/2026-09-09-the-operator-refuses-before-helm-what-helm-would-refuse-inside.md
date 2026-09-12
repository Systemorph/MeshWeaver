---
Name: The operator refuses before helm what helm would refuse inside
Category: Fix
Description: A record-driven upgrade of memex got through adoption and then failed inside helm because the chart had started rendering a PodDisruptionBudget the operator's role could only read; helm's rollback erred as well. The role now covers every kind the chart renders, a test refuses a chart change that adds one without its grant, and the operator asks the cluster what it may write before helm runs.
Icon: Shield
Order: -20260909
---

On 2026-09-09, the first Reconcile of memex to get past adoption failed inside helm:

```
UPGRADE FAILED: failed to create resource: poddisruptionbudgets.policy is forbidden:
User "system:serviceaccount:memex-ops:hosting-operator" cannot create resource "poddisruptionbudgets"
```

## What was happening

The chart had just started rendering a PodDisruptionBudget for every two-replica instance — the
fix for the morning's node drain that took both memex pods at once. The operator's ClusterRole
knew that kind only from its read-only audit block. Nothing paired the chart change with the
grant, and helm found out mid-upgrade; its automatic rollback then tripped over an ingress whose
service the upgrade had already removed.

## What it does now

- The ClusterRole can write every kind the chart renders — disruption budgets, KEDA scaled
  objects, the portal's own Role and RoleBinding, an external model host's Endpoints, and
  `patch` on the migration Job. ClusterRole and ClusterRoleBinding stay excluded on purpose: a job
  must never widen cluster-scoped RBAC.
- `check-chart-kinds-granted.sh` in the operator's test suite reads every `kind:` the chart
  templates can produce and refuses one the role cannot create, patch and delete. Against the
  role as it was it found sixteen gaps.
- Before `helm upgrade`, the operator asks the cluster (`kubectl auth can-i`) about every kind the
  record renders and refuses with the whole list in one line, so a missing grant is named once
  instead of discovered one failed upgrade at a time. A release helm cannot upgrade —
  `pending-upgrade` after a cut-off run — is refused by name too, with the rollback that resolves
  it left to a person.
- On the Azure side, the bicep module now carries the two grants a Provision needs and never had:
  Managed Identity Contributor on the portal identity (the federated credential per instance) and
  DNS Zone Contributor on the zone. No lane deploys bicep; the README names the command.
