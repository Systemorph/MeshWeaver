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

## The same defect in eight more places

A sweep for watches with no guard of their own found the same shape again: a live query subscribed
once, with an error arm that only logs. Each now re-opens.

| site | what one fault used to cost | how it re-opens now |
|---|---|---|
| `InstanceSecretCatalog` (Graph) | every per-sender key refused until a restart (it fails closed) | `SubscribeHubWatcher`. It stays fail-closed (`Faulted`) from the fault until the re-opened query lists the set again. |
| `UiContributionCatalog` (Graph) | menus frozen at the last known set | `SubscribeHubWatcher`. The next full listing replaces the frozen set. |
| `EventSubscriptionRunner`, both live queries (Graph) | no pending subscription fired, no legacy action migrated | `SubscribeHubWatcher` on each |
| `ImageClosureIngest` inbox (Graph) | no closure record ingested | `SubscribeHubWatcher`. The claimed set keeps a delivery from being drained twice. |
| `PartitionRegistry` (Graph) | the fault became a `null` that the shared `Replay(1)` entry served to every later caller | the faulted entry is evicted. Its current callers get `null`, and the next lookup opens a fresh query. It also folds the change feed now: a delta for another partition used to read as "no definition". |
| `RegistryUpdateReconciler` module-published inbox (PluginCatalog) | broadcasts drained only at the next boot | `SubscribeHubWatcher` |
| `InstanceAutoRegistrationService` consent wait (PluginCatalog) | the fault answered `false`, the waiter filtered it out, and registration never ran until a restart | `StandingWatchRecovery.ReopenOnTransientFault` with no budget, so the wait stays open as long as its waiter does. It re-reads the record on every emission, so a record written incomplete and completed later also registers. |

`SubscribeHubWatcher` re-opens after any fault that does not end the watch for good (its own hub
disposing, poisoned own content, its own node gone). It ends with the hub that owns it. Tests drive
the re-open through its `scheduleReEstablish` seam on a manual clock, never on wall time.

### The guard

`StandingQueryWatchRatchetGuard` (`MeshWeaver.Documentation.Test`) counts `.Subscribe(` statements
whose chain opens a live query (`.Query<`, `GetQuery(`, `ObserveQuery`) without one of these:

- a one-shot read: `Take`, `FirstAsync` and similar;
- a write;
- a re-opening helper;
- a top-level `Catch`;
- a forwarding observer;
- an error arm that releases the chain for the next caller.

It is a ratchet, and the count may only fall. It reads text, not symbols, so it MISSES some watches:
a query subscribed in another statement or by a method's caller, a `GetMeshNodeStream` or
change-feed watch, and a standing chain with a per-item `Take`. A green run means no new watch of
the measured shape, not that every watch is guarded. The two sites it counts at seeding are the
portal's e-mail senders (`OutboundEmailSender`, `InvitationEmailSender`).

## Tests

- `StandingWatchesReopenAfterAFaultTest` (`MeshWeaver.Graph.Test`) and
  `PluginCatalogStandingWatchesReopenTest` (`Memex.Portal.Shared.Test`) cover the sites above with
  one script: the first open throws `QueryProviderStalledException`, the watch survives, the
  re-open runs on the test's clock, and a later emission is processed. Each was proven by putting
  back the unguarded shape, which fails it.
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
