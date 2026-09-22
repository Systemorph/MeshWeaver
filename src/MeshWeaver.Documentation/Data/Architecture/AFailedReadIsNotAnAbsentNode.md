---
Name: A Failed Read Is Not An Absent Node
Category: Architecture
Description: A store fault caught inside a query provider used to arrive downstream as a complete answer, so a read that failed reported a node that does not exist — and a per-node hub was refused over a working node. The one verdict that closes it, why it reuses SilentProviders rather than adding a second mechanism, and the two sides a test of it has to have.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 5v14a9 3 0 0 0 18 0V5"/><path d="M3 5a9 3 0 0 0 18 0a9 3 0 0 0-18 0"/><path d="M8 13l8 6"/><path d="M16 13l-8 6"/></svg>
---

# A Failed Read Is Not An Absent Node

**A query answer carries two facts, and the platform used to ship only one of them: WHAT was
found, and WHETHER the search that found it ran to completion.** Without the second, a store that
timed out and a store that holds nothing produce the same frame — and everything downstream reads
the first as the second.

## The chain, end to end

```
IStorageAdapter.ReadMany throws (a connect timeout, a dropped connection, a corrupt row)
   ↓  caught in StorageAdapterMeshQueryProvider, logged at Warning, rows dropped
Initial emitted over the SURVIVORS, claiming a complete answer
   ↓  MeshQuery records no silent provider — the provider DID emit an Initial
PathResolutionService's floor refusal cannot fire — it guards on SilentProviders
   ↓  resolution completes with nothing
MessageHubGrain: "No MeshNode resolvable for address '…'"
   ↓  the per-node hub is refused
```

Every step is correct in isolation. The defect is that the first step's *uncertainty* is not
carried by any of the later ones, so the last step states a fact about the NODE from evidence that
was only ever about the STORE.

## Keeping the survivors is right; claiming they are everything is not

The catch is deliberate and stays. One corrupt row must not kill a listing, and a partial read of a
thousand-node subtree is worth more to a caller than a fault. What was wrong was never the
salvage — it was the **silence about the salvage**.

So the fix adds no failure mode and removes no availability: the same rows are served, and the
frame says it is short.

## One verdict, folded into the field that already exists

`QueryResultChange.SnapshotIncomplete` is set by a **provider**, on its **own** frame, when it
answered over reads it could not complete. `MeshQuery` folds it into
`QueryResultChange.SilentProviders` — attaching the provider's name, which the aggregator already
knows because it subscribed the stream.

**That reuse is the point.** `SilentProviders` names a provider that produced *no* Initial; a
provider that produces a *shorter* one is the same lie with a frame attached. Every consumer that
already refuses to read absence off a floor therefore covers this case with no change of its own:

| consumer | what it already does |
|---|---|
| `PathResolutionService.ResolveSegmentsCore` | refuses to resolve, and refuses to CACHE, unless the full requested path matched |
| `MeshNodeStreamCache` | does not cache a floor as the durable answer for the process |
| `SyncedQueryMeshNodes` | publishes the unanswered set to its consumers |

A second mechanism over the same frame would be a second decider, and two deciders over one
question is how they come to disagree.

### Why the flag is provider-side and the name is aggregator-side

A provider naming itself would put the same string in two places. The provider states the FACT; the
aggregator attaches the NAME. The single-provider fast path does the identical fold, because a lone
provider is the shape a Monolith and every test actually run, and a consumer must not have to know
how many providers happened to be registered.

### Why the sink is per subscription

A field on the provider would be process-wide mutable state shared by every concurrent query on
that adapter: one partition's timeout would mark every other caller's snapshot incomplete, and
nothing would ever clear it. The completeness sink is created once per subscription by the method
that emits the Initial, threaded down to the catches, and collected with the run. It is monotone —
a run whose first read faulted and whose second succeeded still produced a short snapshot.

A teardown cancellation is deliberately **not** recorded. That query is not answering at all, it is
stopping; marking it incomplete would label every in-flight snapshot at every mesh teardown.

## What the grain may now say

The determinate arm used to offer a union — *"either the node does not exist or no query provider
claims its partition"* — and **both halves were measured false in the cases that fired**: the
failing paths read back present on the live mesh, and the provider behind the gap had claimed the
partition and answered. A union of two causes, asserted as exhaustive when the real cause was a
third, teaches a reader to pick the nearer one.

With a silent read faulting on its own terminal and an incomplete read refused as a floor, the
remaining arm is genuinely determinate and says so: resolution COMPLETED, every provider answered,
none holds this path.

Three sentences, three causes, no union:

| condition | what the reader is told |
|---|---|
| the read never terminated | *"neither answered nor terminated … the activation source is SILENT"* |
| the read completed but was short | refused as a FLOOR, naming the provider |
| the read completed and matched nothing | *"a determinate absence"* |

## The two sides a test of this must have

A test that drives only the faulting adapter cannot see a rule that CHOOSES among cases. An
implementation that stamped *every* frame incomplete passes it — and refuses every genuinely absent
node in production, which stalls every per-node hub activation in the mesh. That is the worse
defect, and it hides behind a green test.

So `AFaultedReadIsNotAnAbsentNodeTest` carries both, and each is the other's control:

- a store that throws on the read that would have found the node ⇒ the frame is incomplete and
  names its provider, **and the surviving row is still served**;
- a healthy store asked for a path nobody wrote ⇒ the frame is complete, `SilentProviders` is
  empty, and the row that *is* there is served.

Both fixtures count the faults they injected and assert the count, so a refactor that stops taking
the faulting leg fails loudly instead of leaving the test vacuous. The walk leg (`ListChildPaths`)
gets its own case because it is a separate catch: a fix threaded through the exact-path probe and
not the walk reads as complete from outside.

## Related

- [Query Provider Parity](../QueryProviderParity) — the other way two query
  implementations disagree silently.
- [Hub Initialization Failure](../HubInitializationFailure) — what a refused
  activation does to the callers behind it.
- [Reading CI Signals](../ReadingCiSignals) — the same defect class in the other
  instrument: an answer that reads like a pass.
