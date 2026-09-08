---
Name: A hub teardown no longer reports work it went on to finish
Category: Fix
Description: A disposing hub filed an Error naming turns it had "discarded" — and then drained and answered every one of them milliseconds later. The intake gate that let those turns in one phase too late is now bounded by the phase that can still use them, and the report measures the pump instead of asserting about it. It also finally names the message, which is what made the production occurrence undiagnosable.
Icon: ShieldError
Order: -20260908
---

# A hub teardown no longer reports work it went on to finish

On 2026-09-07 at 21:39:38Z one `sync/` hub on `memex` logged this and opened an incident:

```
[DISPOSE-DISCARD] Hub sync/KptJzzVNdk2qCCf4nMvcSA is disposing with 1 turn(s) still queued and
unprocessed (the pump stops with this call). RunLevel=ShutDown; 0 deferred delivery(ies) already
answered ShuttingDown; last turn executing: ShutdownRequest.
```

It reads as the drain contract broken — [teardown lets accepted work
finish](/Doc/Architecture/TeardownLayers) — and it names no message, so the investigation could get
no further than *"a late post beat the pump by milliseconds"*.

**The pump does not stop with that call and cannot.** `messageService.Dispose()` runs from inside
the hub's own `ShutdownRequest` turn, so the turn loop is one stack frame below it and takes the
next turn the instant that turn returns. Reproduced deterministically: 3 ms after the Error, the
same hub logs `is disposing. Not processing DisposeRequest (id=…)` — it had dequeued the delivery
the Error called unprocessed, and the disposing seam had answered it (a transient `ShuttingDown`
NACK to anything a sender awaits, a silent drop for fire-and-forget traffic nobody awaits). Nothing
was left waiting. **The Error was the defect.**

Three changes, all at the cause:

- **The gate stops creating the state.** The intake gate exempted teardown's own traffic at *every*
  run level, so a `DisposeRequest` arriving after the hub had begun disposing was admitted into a
  window where its handler is a proven no-op — it could do nothing but occupy a turn slot the
  disposal report would then find. The exemption is now bounded by the phase that can still use it:
  `ShutdownRequest` while a phase is left to advance, `DisposeRequest` only before the hub's own
  teardown has begun. A recycle sent to a live hub is unchanged, and has a control arm in the test
  that says so.
- **The report measures.** It stays an Error only where "queued and unprocessed" can be true —
  nothing is draining the loop, so nobody will take those turns. Otherwise it is a Debug line
  saying the pump drains them next.
- **It names the message.** Both forms now print the queued deliveries' type, id and sender.

The finding and the reproduction are in [Teardown
layers](/Doc/Architecture/TeardownLayers) → "What a hub still discards", and the run-level
monotonicity it also secures is in [Teardown verdicts are
causal](/Doc/Architecture/TeardownVerdictsAreCausal).
