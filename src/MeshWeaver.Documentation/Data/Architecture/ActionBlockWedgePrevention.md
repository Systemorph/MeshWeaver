# Action-Block Wedge Prevention

Every portal wedge we have diagnosed — the production `_Activity` NotFound storms, the
`DeliveryFailure`/`ShutdownRequest` ping-pong, the composer `UpdateStreamRequest`
`AccessContext`-null cascade — is the **same failure**: a per-partition hub runs a
**single-threaded action block**, and a message whose *failure produces more messages*
saturates that one thread until it can no longer drain. The portal serves HTTP 200 but the
partition is dead.

A wedge is never one bad message. It is **amplification**: rejected post → `DeliveryFailure`
→ resubscribe → rejected post → … on one thread. Remove the amplification and no input —
including a defect we have not seen yet — can saturate the block.

This is prevention by **invariant**, not by chasing each root cause. The three invariants
below are the contract; the **Tests** section is the acceptance criteria. "Solved for good"
means these tests are green and stay green.

## Invariant 1 — Rejection is terminal, never amplifying

A post that the never-null `AccessContext` guard (or any inbound gate) **rejects must produce
O(1) follow-up**: log once, drop. It must NOT emit a `DeliveryFailure` (or any reply) that
triggers a resubscribe/repost that fails again.

- Infrastructure/lifecycle messages (`[CanBeIgnored]`, `[SystemMessage]`, `DeliveryFailure`,
  `ShutdownRequest`, `DisposeRequest`) are exempt from failure-reporting at **every emit
  site** — `MessageService.ReportFailure`, both `HierarchicalRouting` sites, both
  `RoutingServiceBase` sites. (This is the `DeliveryFailure`-storm fix; the invariant is that
  it holds at *all* emit sites, permanently, enforced by a test — not re-checked by hand.)
- Root-aligned, not a band-aid: it removes the *cause* of amplification.

## Invariant 2 — A subscription to a missing target dies after N, it does not retry forever

The phantom `_Activity` storm and the rsalzmann subscribe-wedge were both **unbounded
resubscribe to a node that does not exist**. Bounding must be the **default behavior of the
external/missing-target subscribe primitive**, not a per-call-site patch (`AreaStreamRetry`,
the `JsonSynchronizationStream` bounded retry) that each new caller re-opens.

- The primitive: `.Take(1).Timeout(t).RetryWhen(≤ N on DeliveryFailure/Timeout) → terminal
  OnError`, then **stop**. No caller can spin to infinity even if it forgets to bound.
- Root-aligned: a missing target is a terminal condition, not a retry loop.

## Invariant 3 — No single action block can be driven past its drain rate (the safety net)

Invariants 1–2 remove today's causes. Invariant 3 makes the wedge **structurally impossible**
for any *future* defect: a per-hub **aggregate** backpressure breaker.

- The existing `MessageStormBreaker` trips **per-key** (one path, one stream). Every wedge we
  saw was **many distinct keys** — each phantom path, each failed area is a different key — so
  no single key crossed the threshold while the *aggregate* saturated the thread. **Per-key is
  the gap.**
- The fix: a per-action-block watermark on inbound depth/rate. When one block's queue exceeds
  the watermark, **shed `[CanBeIgnored]`/failure-class messages** (never user-facing or
  lifecycle messages) to keep it draining. The breaker is keyed on the **hub**, aggregated
  across message keys.
- This is the only *new* mechanism; 1–2 are generalizations of fixes already in the tree.

## Tests (the acceptance criteria — "done" = these are green)

All deterministic. Run the saturation tests under `DOTNET_PROCESSOR_COUNT=2` (the CI 2-core
sim that reproduces the real wedge — a fixed sleep/`Task.Delay` is forbidden; assert on the
condition). Wait on the actual signal via `stream.Where(...).FirstAsync().Timeout(...)`.

1. **`Rejection_DoesNotAmplify`** — post a message to a hub with no `AccessContext` and no
   exemption. Assert: exactly one rejection is logged/observed and **zero** `DeliveryFailure`
   re-posts follow (count the `DeliveryFailure` traffic — it must be 0, not "fewer"). Pins
   Invariant 1.

2. **`FailureExemptAtEveryEmitSite`** — reflection/architecture test: enumerate every site that
   constructs a `DeliveryFailure`; assert each is guarded by the `[CanBeIgnored]`/`[SystemMessage]`
   check. Fails the build when a new emit site forgets the guard. (Mirrors the
   `NoStaticCollectionsTest` architecture-guard pattern.) Pins Invariant 1 permanently.

3. **`SubscribeToMissingTarget_Terminates`** — subscribe to a node path that does not exist
   under a live prefix (the rsalzmann shape: `{partition}/_Thread/does-not-exist`). Assert the
   subscription emits terminal `OnError` after ≤ N attempts within a bounded time and **stops**
   (no further `SubscribeRequest`/NotFound after termination). Pins Invariant 2.

4. **`ManyDistinctMissingSubscribes_DoNotWedge`** — the wedge repro. Fire M (e.g. 200) subscribes
   to M *distinct* non-existent paths (defeating any per-key breaker), then post a normal probe
   message to the same hub. Assert the probe is processed within a short timeout — i.e. **the
   action block stayed drainable**. RED until Invariant 3 lands; the canonical wedge guard. Pins
   Invariant 3.

5. **`AggregateBreaker_ShedsOnlySheddable`** — drive a hub past the watermark with
   `[CanBeIgnored]` traffic; assert shed messages are dropped but a concurrently-posted
   **user-facing** message is still delivered (the breaker never sheds user/lifecycle work).
   Pins Invariant 3's safety boundary.

## Status / ownership

Invariants 1–2 exist as point-fixes in the tree (the `DeliveryFailure`-storm fix; the
`AreaStreamRetry` / `JsonSynchronizationStream` bounded retries) — the work is **promoting them
to enforced invariants** via tests 1–3, and adding the **aggregate breaker** (Invariant 3,
tests 4–5). This all lives in the messaging action-block + sync layer (`MessageService`,
`RoutingServiceBase`, `MessageStormBreaker`, `SynchronizationStream`). It must be implemented
by a **single owner** of that layer so the aggregate breaker is designed coherently against the
rejection/bounded-subscribe invariants — two parallel editors of the action block is itself a
source of regression.

The separate root causes that *fed* these wedges (System identity dropped on activity/import
writes; user credential dropped on the composer `stream.Update`; the `Agent`/`Model` public-read
grant) are tracked elsewhere — fixing them removes the *load*; the invariants here remove the
*amplification*. Both are needed.
