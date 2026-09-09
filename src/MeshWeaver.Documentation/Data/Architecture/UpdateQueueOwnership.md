---
Name: Update Queue Ownership
Category: Architecture
Description: Atomic publication and explicit lifetime ownership keep concurrent node writes on one queue and give every accepted write a terminal result.
---

# Update Queue Ownership

The [MeshNode stream cache](/Doc/Architecture/MeshNodeStreamCache) owns one update queue per
path. That queue serializes writes and hands each successor the preceding write's local state.
Both publication and retirement must preserve that ownership: a caller must never enqueue into
a queue whose subscriber was silently removed by another caller.

## Atomic publication

The queue table uses `ConcurrentDictionary<string, Lazy<UpdateQueueEntry>>`. Competing factories
may construct candidate lazies, but `GetOrAdd` returns the stored winner to every caller. Only
that winner's lazy value starts a subscription. This follows the cache's existing read-entry
publication pattern.

`MemoryCache.GetOrCreate` does not provide that guarantee. Each overlapping cold factory can
return its own candidate and replace an entry another caller has just published. A `Lazy` inside
each candidate prevents duplicate initialization of that candidate, not duplicate queues per path.
Depending on timing, replacement either leaves two live queues or disposes a queue another caller
already holds. Disposing the subscription alone gives that caller no completion or error.

## Ownership lasts through accepted work

An entry tracks both outstanding result subjects and accepted `Concat` slots. Those lifetimes
are distinct: an optimistic result can finish before the queue advances, and advancement can
precede the final write verdict. A conflict retry retains its result pin across the retry delay;
the queue owns the delayed subscription along with its in-flight writes.

The existing idle sweep retires a queue only after ten quiet minutes and only when both counts
are empty. Removal compares the path and the exact stored entry, so a retiring owner cannot
remove a replacement. The preceding write's pending local state belongs to the queue entry too;
a late callback from a retired queue cannot supply or erase its replacement's state.

Shutdown or a pipeline fault stops owned subscriptions and gives every still-pending result an
explicit error, including work buffered in `Concat`. A short state lock protects acceptance and
retirement; dispatch, disposal, and observer notifications happen outside it. The write pipeline,
caller identity capture, owner authorization, and existing retry and advancement bounds retain
their semantics.

## Evidence and attribution

The investigation started with
[`Two_concurrent_logons_run_a_once_action_exactly_once` timing out in CI](https://github.com/Systemorph/MeshWeaver/actions/runs/34340297438/job/102431137649)
at baseline `7540a046917edc0691f2aefec7af2efeab4f56a7`. Its combined wait expired after 30 seconds;
teardown then finished in about 6 milliseconds. The captured Warning-level log does not identify
where completion was lost, so it cannot establish that queue publication caused this occurrence.

One traced local run at that same baseline passed in 762 milliseconds. Nevertheless, it recorded
an update queue eviction with reason `Replaced`, followed by two `START` records for the same
path before either write finished. A passing final assertion therefore did not prove one queue
owned that path.

A deterministic probe reentered the real cache's cold factory through its clock during entry
publication. It failed in 431 milliseconds: the overlapping callers received different subjects,
and the replaced subject had `HasObservers = false`. This proves the publication and subscriber
lifetime defect independently of the original CI timeout. It does not establish that every
concurrent-logon timeout, or unrelated native compiler flake, has the same cause.

Regression coverage must force overlapping cold publication, assert the same live queue for both
callers, and verify that accepted queued work receives a terminal result across retirement and
disposal. Repeating an unchanged test until it passes is not evidence that ownership is correct.
