---
Name: A degradation that recovered is not a defect
Category: Fix
Description: The CI gate that catches unreadable node content reddened a shard for the ordinary boot race — a NodeType read a moment before its own compile registered the type. It now reports the verdict rather than the event: at teardown the platform re-asks the content-type registry about everything that degraded, and only what is still unresolvable is a hit. The same correction makes /health stop calling a replica Degraded over a race it already won.
Icon: Filter
Order: -20260909
---

A node whose content carries a `$type` the reading hub cannot resolve does not fail. The value
degrades to a raw `JsonElement`, and everything downstream reads it as absent — an `is MyType` check
misses, a view renders empty, a reactive wait never completes. That silence is why there is a CI
gate for it at all.

The gate was catching the wrong population. It keyed on the record the stream cache writes **at the
instant of a read**, and at that instant nothing can know whether the type registers a moment later.
That is the normal state during a portal boot: a NodeType's runtime compile lands after the first
readers have already been served, and re-typing those live readers when the registration arrives is
a fixed, working behaviour. The seam one layer down said as much in its own message — *"content
stays an untyped JsonElement **for now** … (a) the compile has not registered it **yet, which is
transient** … or (b) no declaration will ever claim this discriminator"* — and reddened nothing. One
event, two descriptions, opposite consequences.

Measured: two of the three cases in the test that covers this race degrade **and recover inside a
single test** — the recovery is the assertion — and both produced a gate-reddening record.

## What it does now

Nothing new was invented. The platform already kept a per-node-type record of what a replica could
not type, and the content-type registry already answers *"is this resolvable"* as a pure lookup. So
the verdict is computed by **asking again**:

- at the end of the stream cache's life, every recorded degradation is re-checked against the
  registry — by the node's own NodeType and by the stored `$type`, the same two routes recovery
  takes;
- what has since registered is dropped: it was the boot race, and it healed;
- what is still unresolvable is reported once, naming the node type and the discriminator, and
  **that** is what the gate keys on.

A boot that reads a runtime-compiled NodeType before its compile lands therefore produces no gate
hit. A discriminator no declaration will ever claim produces exactly one. The per-read records stay
in the logs and stay useful — once the verdict names a type, they are how you find which reads
degraded on it.

## `/health` was wrong the same way

`ContentTypeHealthCheck` reported everything that had ever degraded, so a replica stayed `Degraded`
after a boot race until some later read of the same type happened to clear the entry — a probe
answering about the past. It re-asks now, so a race that healed disappears from `/health`
immediately and only a type that genuinely never arrived keeps the replica degraded.

## Pinned in both directions

Reduce the re-ask to the raw snapshot and the recovered case is reported again — the transient test
fails. Remove the teardown report and the unrecoverable case has nothing to find — the final test
fails on *"the teardown must REPORT what never resolved"*. The gate's own control arm additionally
pins that the script keys on the verdict type, that the emitter constructs it, that the report is
computed from the re-asked set, and that both read seams still emit the per-read record the verdict
is derived from.

See [Reading CI Signals](@/Doc/Architecture/ReadingCiSignals) → *TRANSIENT from FINAL*.
