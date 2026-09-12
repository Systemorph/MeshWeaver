---
Name: Two replicas no longer leave together on a node drain
Category: Fix
Description: memex answered 503 for about ninety seconds in the night of 2026-09-09 when the cluster drained a node and both portal pods were on it, with no disruption budget to hold the second one back. The chart now renders the budget for every two-replica deployment, not only autoscaled ones, and asks the scheduler to keep replicas on different nodes.
Icon: Shield
Order: -20260909
---

At 01:05:01Z both memex portal pods logged "Application is shutting down" in the same second.
Nothing had been deployed: the replacements came from the same ReplicaSet, on the same image, and
there was no crash. Read across every namespace, the cluster was draining its nodes one by one —
Loki, its alertmanager, the dashboards and the memex-cloud website pods all stopped within
fifteen seconds of the next memex pod four minutes later. That is a node-pool operation, and a
routine one.

## What was missing

A drain evicts pods through the eviction API, and the eviction API honours a
`PodDisruptionBudget` — if there is one. The chart had one, but only rendered it under KEDA
autoscaling. memex is not autoscaled; its overlay declares two plain replicas, so the budget
never existed there, and the drain took both pods at once. The pod template also said nothing
about where the two replicas should live, so the scheduler had put them on the same node.

## What it does now

- The budget renders whenever the replica floor is two or more — autoscaled or not — and
  throttles voluntary disruption to one pod at a time, as before.
- The pod template declares a preferred anti-affinity on the node name, so two replicas land on
  two nodes when two are schedulable, and still schedule when only one is.
- The chart invariants gate on every pull request now refuses a rendering with a replica floor
  above one and no budget, or no spreading — the shape that was green for weeks.

The lane that gates its own inputs could not have seen this: the check asked whether a budget
that exists has replicas under it, never whether replicas that exist have a budget over them.
