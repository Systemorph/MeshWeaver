---
Name: A hub disposing with its own question unanswered is no longer reported as lost work
Category: Fix
Description: >-
  A transient type probe is created, read once and disposed — by design. When it was torn down with
  its own self-addressed read still parked behind its initialization gates, the teardown reported an
  Error claiming the sender had been answered. Nobody had been answered, and nobody was waiting: the
  sender was the probe itself. 76 such lines per gate shard. The line now follows the fact.
Icon: Checkmark
Order: -20260913
---

A hub that is disposed while a message it accepted is still parked behind an initialization gate
reports that at **Error**, and it should: the message never ran, its sender gets a transient
`ShuttingDown` NACK instead of an answer, and somebody has to find out why the hub went down with
its gates still shut. That contract is in [Teardown Layers](/Doc/Architecture/TeardownLayers).

There is one shape where every clause of that sentence is false, and it was the loudest one in
production. A **transient node probe** — the short-lived hub a NodeType's instance configuration is
applied to so its type registry can be read — is *created, read once and disposed*, in that order,
on purpose ([Transient Node Probes](/Doc/Architecture/TransientNodeProbes)). The configuration it
applies was written for a real per-node hub, where the hub's address IS its mesh path, so a loader
deriving a path from the hub's address and reading it is ordinary, correct code — and on the probe
that derivation collapses onto the probe's own synthetic address. The probe asks itself a question,
the question parks behind the probe's own `DataContextInit` / `MeshNodeInit` gates, and the probe is
disposed before they open.

The teardown then logged:

```
[DISPOSE-DISCARD] Hub $model-probe/f27ab9f9… is disposing with GetDataRequest
(id=beQyWMaX60eN-jRJQeKllw, from $model-probe/f27ab9f9…) still deferred; initialization gates
closed at deferral: [DataContextInit,MeshNodeInit] — the message is NOT processed; the sender is
answered ShuttingDown.
```

**The sender was not answered.** One method below the log call, `NackThroughParent` declines exactly
this delivery — `NACK_DECLINED reason=sender-is-self` — because there is nowhere to post the answer:
the pending-response registry that would resolve it belongs to this hub and is being cancelled in the
same disposal. The reader that would have received it is told by a different and better route
(`CancelCallbacks` errors those subjects with *"Hub … was disposed before the response arrived"*).
So the Error alleged stranded work, named an answer that was never sent, and told whoever read it to
go and find a producer that had done nothing wrong.

It was not rare. Measured on a plugin-gate shard: **76 of these per shard**, every one a probe
discarding its own read.

The classification now follows what happened. When the sender is this hub itself, the discard is
teardown-internal: the line still fires, still names the message, the id, the gates it sat behind and
the run level — at **Debug**, and with a sentence that no longer claims an answer went out. When the
sender is any **other** hub, a real waiter is left holding a transient NACK, and that stays an Error
exactly as before.

Both halves are pinned by tests that were each watched failing: removing the split turns the
self-addressed case red for *stopping to report*, and widening it turns the external-sender case red
for *losing the Error*. A reclassification that quietly stopped reporting would have been a silenced
fault rather than a classification.
