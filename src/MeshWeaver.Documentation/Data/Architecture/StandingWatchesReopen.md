# A standing watch re-opens after a transient fault, and only after one

Most mesh reads are one-shot. A caller asks, gets an answer or a fault, and the next caller asks
again. The layers below are built for that:

- The query fan-in retries a transient connect fault only **before** a provider's first emission
  (`TransientStorageFaults.RetryTransientConnect`). After it, a re-subscribe would mint a second
  `Initial` into a merge that has already closed its accounting.
- The synced-query cache evicts a chain that faulted (`MeshNodeStreamCache.EvictFaultedQuery`) and
  leaves the retry decision to "the next caller".

A **standing** watch has no next caller. It is one subscription held for the process's life, so it
is the caller. If it does not re-open after a fault, nothing will until the process restarts.

## The defect (MeshWeaver#6183)

The pre-warmer's live NodeType-record census (`DynamicTypePreWarmer.ObserveLiveRecordCensus`, the
`LIVE RECORD CENSUS` sentence of `/health`'s `bake-report`) is such a watch. On memex-cloud, pod
`memex-portal-deployment-65fcf777bc-pt5dc`, at 2026-10-05 23:30:44Z, one
`NpgsqlException: Exception while reading from stream → IOException → SocketException (104)
Connection reset by peer` arrived while the PostgreSQL provider streamed the catalog's rows. The
census ended. `/health` then reported that a record re-keyed after boot was "no longer being watched
for" until the replica restarted.

## The rule

`StandingWatchRecovery.ReopenOnTransientFault` re-opens a standing watch when **both** hold:

1. **The fault is typed as transient** (`StandingWatchRecovery.IsTransient`). That means one of:
   - a database connect or I/O fault (`StorageFaults.IsTransientConnectFault`,
     `InfrastructureFault.IsTransient`);
   - the fan-in's stall terminal (`QueryProviderStalledException`). That is an availability failure,
     never a verdict.

   Anything else ends the watch on its first occurrence, as before: a query or schema error, a
   deserialization defect, a disposed mesh. Re-reading a defect only repeats it on a timer.
2. **The budget is not spent.** The watch gets at most 5 re-opens in a row with no emission between
   them. The backoff is 2 s, 5 s, 15 s, 30 s, then 60 s. An emission proves the watch reads again
   and resets the count. When the budget is spent, the last fault surfaces unchanged. A database
   that stays down is reported after about two minutes, not hidden.

Each re-open calls the factory again. For the census that means asking the synced-query cache
again, and the cache has already evicted the faulted chain. So the re-open gets a fresh upstream,
not a replay of the latched fault. A factory that hands back the same cached observable would defeat
the rule.

The gap is never silent. Before each re-open, the hosted service records the interruption on the
census (`NodeTypeBakeReportRegistry.RecordLiveRecordsFault`), so `/health` degrades and says why. The
next reading clears it (`RecordLiveRecords`). A terminal fault is never followed by a reading, so it
stays on `/health`.

## The build-claim handshake: the same fault in a second shape (MeshWeaver#5716)

The pre-warm sweep starts with a claim handshake to `Admin/Build`. It already retried "no answer in
time" (`BuildProtocolDriver.RetryUnreachableCoordination`, #1635). Once the attempts were spent it
reported `BuildCoordinationUnreachableException`, which sends the decision to the durable-witness
door (#3404).

But "no answer in time" arrives in two shapes, and the handshake knew only one:

| where the bound fired | what the caller receives |
|---|---|
| in this process | `TimeoutException` |
| in the transport (Orleans' 30 s response or placement timeout) | `DeliveryFailureException`, text `Delivery to 'Admin/Build' failed: Response did not arrive on time in 00:00:30 …` |

The router flattens the second shape to text, because the exception object does not survive the
grain boundary. The handshake read it as a hard verdict. It was not retried and not reported as
unreachable, so the witness was never asked and the whole warm-up faulted on one slow exchange. That
happened on memex-cloud, pod `76cf776847-v4txp`, at 2026-09-24 06:51:10Z.

`TransportTimeout.IsNoAnswerInTime` (`MeshWeaver.Messaging`) is now the one definition of both
shapes. The handshake and the view retry (`AreaErrorClassifier`) both use it. It matches only the
transport's own two timeout phrases, never a NACK that carries a verdict. Re-asking is safe: the
claim registration is idempotent per holder, even if the timed-out request did land.

## Tests

- `LiveRecordCensusReopensAfterTransientFaultTest` runs on a real monolith mesh. A test query provider
  answers the catalog query and then dies mid-stream with the incident's exception chain. The fixed
  census re-opens, reads again, and opens a fresh upstream. The negative control runs the raw census
  through the same fault and shows that it terminates. A non-transient fault still ends the watch,
  with no re-open.
- `BuildCoordinationRetryTest` feeds the handshake the router's exact NACK text. It is retried, and
  once exhausted it is reported as unreachable. A routed NACK that carries a verdict is not retried.

## Related

- [The query fan-in's Initial gate is a bound, not a wait](../QueryFanInStallTerminal)
- [Store unreachable is not a refusal](../StoreUnreachableIsNotARefusal)
