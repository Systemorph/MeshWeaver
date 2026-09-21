---
Name: Reading a Routing Saturation Report
Category: Architecture
Description: The [ROUTE] back-pressure line is a gauge over three different facts, and for most of the tickets it produces the causal arrow runs the other way — a silo that has lost its CPU or its heap raises it with nothing stuck. Which number answers which question, the two opposite pool states that printed the same number for a month, and the one reading that separates load from a leaked slot in a single sample.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v3"/><path d="M12 18v3"/><circle cx="12" cy="12" r="4"/><path d="m5 5 2.5 2.5"/><path d="M3 12h3"/><path d="M18 12h3"/><path d="m16.5 7.5 2.5-2.5"/></svg>
---

# Reading a Routing Saturation Report

```
[ROUTE] Routing back-pressure [9d926d64#1 started …]: 64 route dispatches in flight
(reporting threshold 64); ordered channels queued 64 over 12 stream destination(s),
deepest per-channel queue 0, routing pool subscribing 0, waiting for a pool slot 0;
oldest leg in flight 3 ms — stream-routed → cache/… (delivery …).
Latest dispatch target … — the address that happened to cross the threshold, NOT a diagnosis.
```

This line is the single most-ticketed log site in the platform. It is **a gauge, not a bound**:
nothing throttles, queues or refuses at 64. `RoutingGrain.SaturationThreshold` gates a log call and
nothing else, which is why every report in production prints *exactly* 64 — the report latches on the
single increment that crosses the line, so 64 is the only value it can print. That artefact has twice
been read as evidence of a hard cap.

## 🚨 The arrow usually runs the other way

The line's own text says so: *"a CPU-starved silo raises this with nothing stuck."* A routing slot is
held from dispatch until the leg terminates, and a leg cannot terminate — cannot even *start* its own
timeouts — while the silo has no thread to run it on. So the gauge rises whenever the **process**
is in trouble, whatever the cause.

This makes the log site a magnet for tickets about faults that are not routing faults. Read in the
wrong direction it produces a confident, wrong conclusion: *"routing saturation is causing the
outage"*, when routing is the instrument that noticed it.

**Before treating a crossing as a routing defect, rule the process out:** an
`OutOfMemoryException` anywhere on the pod in the same window, a
`LocalSiloHealthMonitor` *".NET Thread Pool execution stalled for N s"*, or an Orleans
`PlacementWorker` timeout are all upstream of this line, not downstream of it. Only
`deepest per-channel queue` speaks about routing structure specifically.

## What each number can and cannot say

| Field | Answers | Cannot answer |
|---|---|---|
| `route dispatches in flight` | nothing on its own — it is pinned to the threshold by the latch | busy vs. blocked vs. leaked. Two opposite situations give the identical count, which is the whole point of `RoutingBackpressureShapeTest` |
| `ordered channels queued` / `stream destination(s)` | breadth. Many channels over few destinations is **load** | whether any channel is stuck |
| `deepest per-channel queue` | ≥ 1 ⇒ frames of **one stream** are stacking behind one another — the only genuinely routing-structural reading | why the head is slow |
| `routing pool subscribing` | how many legs are inside their **subscribe prologue** right now | anything about legs before or after that prologue — see below |
| `waiting for a pool slot` | legs the pool has ACCEPTED and not yet started | which of them will start |
| `oldest leg in flight` | **load vs. leaked slot, in one sample**, and it names the leg | why that leg is old |
| the episode stamp | `activation#episode` — a higher episode on the same activation proves the previous episode **drained** | anything, from a single sample, about an episode that never drained |

## The gauge that could not see the cause it named

For as long as this line has named the ThreadPool wait as its leading hypothesis, it printed the one
pool gauge that is blind to it.

`IoPool` increments `_inFlight` **only after `_gate.WaitAsync` has granted a slot**, and releases it
as soon as `source.Subscribe(observer)` returns. `CurrentInFlight` therefore counts **the subscribe
prologue and nothing else**. A leg that is still waiting for a thread has not reached it; a leg past
its subscribe, awaiting its own I/O, has already left it. Both are invisible.

So two opposite states printed the same number:

| State | legs waiting | `routing pool subscribing` |
|---|---|---|
| every leg parked waiting for a ThreadPool thread — **the named hypothesis** | 64 | ~0 |
| every leg past subscribe, awaiting its own I/O — ordinary breadth | 0 | ~0 |

**All 19 log lines quoted verbatim across the three surviving tickets** — 10, 6 and 3 respectively —
read `0`, `1` or `2` in that field. (19 is what was *read*, not the population: the tickets consolidate
~87 filings, whose individual lines are summarised rather than quoted.) So the question *"is this silo
starved?"* stayed open through three consolidations that each asked for exactly this separation. The
gauge that answers it already existed on the interface, already had an implementation, and already
stated the discriminator in its own XML doc:

> *A depth that stays high while `CurrentInFlight` sits at the cap is a pool whose cap is the
> constraint; a depth that stays high while it does NOT is work that has not reached the gate — a
> thread shortage, not a slot shortage.*
>
> — `IIoPool.CurrentlyWaiting`

**Nothing had to be measured on a pod to learn this. It was two files apart.** The transferable rule:
when a log line names a hypothesis, check that it prints a number that could *falsify* it. A caveat
in prose beside a gauge that cannot see the case the caveat describes reads exactly like evidence
against it.

## The oldest leg: load vs. a leaked slot, in one sample

The line's advice for telling a busy silo from a leaked slot is:

> a later line with a HIGHER episode on this activation means this episode drained … and if neither a
> clear nor a higher episode ever follows, the in-flight count never fell below half the threshold —
> which means a leg never terminated and its slot leaked

That is sound, and it **needs two samples** — while the incident filer files per sample. The result
was a string of single-sample tickets that "exclude nothing": a crossing captured with no clear line
and no higher episode inside the window, which is simultaneously the most alarming reading available
and completely inconclusive.

An **age** answers it in one sample:

- every leg young ⇒ load the silo is absorbing;
- one leg minutes old ⇒ a slot that is not coming back — and the label names the leg.

It needs no timer, no poller and no watchdog. `RoutingQuiescence` already tracks every in-flight leg
under a label precisely so that a leg which fails to land can be *named* rather than merely counted;
recording **when** each leg was accepted makes that registry answer "how long" as well as "which".
`RoutingQuiescence.OldestInFlight()` is that reading, and the saturation report carries it.

🚨 **Note what this does and does not do.** It makes the load-vs-leak question *decidable*; it fixes
neither load nor a leak. A report that names a twenty-minute-old leg has told you where to look, and
the defect is still whatever is holding that leg.

## Three verdicts at one log site

One log site, one fingerprint family, three genuinely different faults. Consolidating them on the
title merges the readings and loses the only thing that separates them.

| Verdict | Signature | What it is |
|---|---|---|
| **Head-of-line** | `deepest per-channel queue` ≥ 1 | frames of ONE stream stacking up. Structural, routing's own. Before the channel key was narrowed to `(destination, stream)` this read `deepest per-destination queue` 23–62 and meant something quite different — see [Ordered Route Channels](../OrderedRouteChannels) |
| **Load** | `deepest 0`, many channels over few destinations | dispatch volume, or a silo that has lost the CPU. Usually not a routing defect at all |
| **Alerting policy** | any crossing, reported at `Critical` | the crossing is reported *before* anything can know whether it is a fault |

🚨 **A line that still spells `deepest per-destination queue` came from an older image.** It is the
only way to tell which side of the channel-key change produced a given sample, which matters in any
ticket that spans it.

### 🚨 And do not "raise the threshold to the pool's cap" — that is the same category error

The tempting arithmetic is: the report fires at **64** in-flight routes while the routing `IIoPool` is
capped at **256**, so it is firing at a quarter of capacity and the threshold is simply too low.

**The two numbers count different populations, and that is the whole subject of this page.** A route
slot is held from dispatch until the leg *terminates*. A pool slot is held only for the *subscribe
prologue*. So a leg that is past its subscribe and awaiting a network round trip holds **a route slot
and no pool slot** — it is in the 64 and not in the 256. The counts are not comparable, there is no
ratio between them, and "64 of 256" describes nothing. Reaching for it is the same mistake as reading
`routing pool subscribing 0` as evidence that nothing is waiting.

A threshold conversation is legitimate *after* a post-fix sample shows what the crossings actually are.
It is not legitimate as a way of making them stop.

### Why the alerting verdict is not "the alert is crying wolf"

The standing ask on the alerting verdict was to raise the threshold or to demote a `0`-queued
crossing below `Critical`. Both are refused: the first is a widened bound, the second is a log level
changed for verbosity. Neither is needed, because the premise is wrong — **the alert is not firing on
a healthy state, it is firing on a state it cannot classify.** The fix is to make the line carry its
own verdict, not to make it quieter.

Two facts bear on it, and both are easy to get backwards:

- The crossing report **latches** — the only writer that clears it is the drained report — so a
  crossing at episode *N+1* already **proves** episode *N* drained. "Did it drain?" is not what a
  single sample lacks.
- What a single sample lacks is the **absence** of a later line, and *no event-driven report can
  state an absence*. That is the entire reason the oldest-leg age exists.

The `Critical` level itself stays. So does the drained report's `Warning`: it is deliberately not
`Critical`, because the ticketing path files an incident per `Critical` fingerprint and a `Critical`
"cleared" line would file a ticket for a **recovery**, inverting the signal.

## Reading order for one sample

1. **Rule the process out.** OOM, a `LocalSiloHealthMonitor` stall, or a placement timeout on the
   same pod in the same window ⇒ this line is a symptom; go there.
2. **`oldest leg in flight`.** Minutes ⇒ a slot is leaked, and the label names the leg.
   Milliseconds ⇒ load.
3. **`deepest per-channel queue`.** ≥ 1 ⇒ one stream's frames are stacking; this is routing's own
   structure. `0` ⇒ breadth.
4. **`waiting for a pool slot` against `routing pool subscribing`.** Waiting high while subscribing is
   *not* at the cap ⇒ work that has not reached the gate: a thread shortage. Both low ⇒ the legs are
   past their subscribe and the wait is downstream I/O.
5. **The episode stamp.** A different activation ⇒ the grain was recycled. A higher episode ⇒ the
   previous one drained; the drained report says how long it took.

## Related

- [Ordered Route Channels](../OrderedRouteChannels) — why the ordering FIFO is keyed
  `(destination, stream)`, and the head-of-line verdict this line reports
- [A Timed-Out Delivery Is Still Held by the Callee](../ATimedOutDeliveryIsStillHeldByTheCallee) —
  the retry amplifier that fed this same gauge, and the two shapes it explicitly did not close
- [Controlled I/O Pooling](../ControlledIoPooling) — `IIoPool`, its gauges, and what each counts
- [Action-Block Wedge Prevention](../ActionBlockWedgePrevention) — Invariant 3, the hub-level
  aggregate breaker; this line is its routing-leg analogue
- [Red-Log Watching & Ticketing](../LogWatchTriage) — how a `crit:` line becomes exactly one issue,
  and the consolidation rule for this log site
