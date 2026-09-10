---
Name: The /api/content 503 — Three Causes, Two Discriminators
Category: Architecture
Description: "A content file that answers 503 after ~10.3 s has burned ReadBudget.Default on a collection-config read. Three things produce that, they need different fixes, and the third one wears the first one's signature unless you read the TARGET clause instead of the reader's."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="M12 11v4"/><path d="M12 18h.01"/></svg>
---

# The `/api/content` 503 — three causes, two discriminators

`/api/content/{node}/{collection}/{file}` answering **503 after ~10.3 s** is not a class of failure.
It is one failure with three possible causes, and the fix for one does nothing for the others. This
page is the elimination: what the 10 s is, what it is *not*, and the fields that decide.

Filed as [#2901](https://github.com/Systemorph/MeshWeaver/issues/2901) — *"a freshly uploaded
content file 503s for minutes"* — and reopened from a different direction as
[#3931](https://github.com/Systemorph/MeshWeaver/issues/3931) — *"every COLD read 503s after
~10.2 s"*, which is Cause C.

## 🚨 The one sentence

> **The 10 s is `ReadBudget.Default` expiring on ONE request — the collection-config
> `GetDataRequest` — and it expires only when no notification reached the ISSUING hub's callback
> in time. Three things do that. The reply could not be DELIVERED there (a lost pod-hub claim,
> Cause A); it was delivered and not DISPATCHED (that hub's action block was saturated, Cause B);
> or the OWNING hub had not finished STARTING, so the request was still parked in its deferred
> queue when the budget lapsed (Cause C). Nothing else on the route produces 10.3 s.**

🚨 **Cause C is the one that reads as Cause A**, because the documented `Queue(buffer=…)`
discriminator snapshots the READER, and under Cause C the reader is idle. It has its own section and
its own discriminator below; read them before applying either of the other cures.

🚨 **The issuing hub used to be `portal/nodeops-{meshId}` — the mesh's node-CRUD EXECUTION hub —
which is what made the second case reachable from an ordinary upload. It is now
`portal/reads-{meshId}`, a hub that registers no handlers — see *"Cause B's cure"* below. The
elimination
below is written against the pre-fix code, because that is what the production occurrences ran, and
because it is still the map of the route.**

## The read path, and where the 10 s lives

| # | Step | Where | Bounded? |
|---|---|---|---|
| 1 | decode + traversal guard | `BlazorHostingExtensions.ResolveContentFile` (Plugins, `:791-806`) | n/a |
| 2 | **resolve the owning node** — `IPathResolver.ResolvePath` | `ContentFileResolver.cs:114-117` | **NO — unbounded** |
| 3 | **read the collection config** — `GetDataRequest(ContentCollectionReference)` to the owner | `ContentFileResolver.cs:183-193` | **`ReadBudget.Default` = 10 s** (`ReadBudget.cs:92`) |
| 4 | mount check (`IsStatic`) | Plugins `:823-824` | n/a |
| 5 | permission gate, tri-state | Plugins `AllowContentRead` `:595-605` / `GatedContentRead` `:631-646` | fold-local |
| 6 | open the collection + read the bytes | `ContentService.GetCollection` → `ContentCollection.GetContent`, on the collection's `IIoPool` | pool-bounded |

Step 3 is the only 10-second bound on the path. `FailIfNoFirstEmission` (`ReadBudget.cs:120-134`)
is an `Observable.Amb` of the request and a 10 s timer; when the timer wins it throws
`HubUnreachableException`, a `TimeoutException` subclass, which
`BlazorHostingExtensions.ContentFailure` maps to **503** (Plugins, `:746-748`). So a measurement of
*~10.2–10.8 s then 503* is `ResolvePath` (a few hundred ms) plus the budget, exactly.

**Step 3 is issued from, and answered onto, a hub that is not the caller's own.** The
`/api/content` endpoint holds the DI-injected `IMessageHub`, which in the mesh's root container *is*
the router, and the router must be neither end of a delivery — so `ContentFileResolver` hops onto a
dedicated off-router hub. **Until this page's fix that hop landed on `portal/nodeops-{meshId}`,
the mesh's one node-operation hub** (`hub.NodeOperationIssuingHub()`); it now lands on
`portal/reads-{meshId}` (`hub.ReadIssuingHub()`). Either way the reply must be dispatched by *that
hub's* single-threaded action block before `hub.Observe` emits (`MessageHub.cs:451` registers
`HandleCallbacks` as a delivery rule, and every delivery — responses included — goes through
`EnqueueTurn`, `MessageService.cs:906`). Which hub it is decides what else can be in that block's
way.

## What is provably NOT the cause

Each of these was a candidate on #2901. Each answers, and answering is not silence.

| Candidate | Why it cannot produce 10.3 s |
|---|---|
| **The config handler is slow / propagating** | `HandleCollectionConfigRequest` (`ContentCollectionsExtensions.cs:300-346`) is a pure in-memory dictionary read followed by `hub.Post`. There is nothing to settle. |
| **The owning node is not content-enabled** | The generic workspace read answers anyway: `GetDataResponseObservable<TReference>` returns `GetDataResponse(null, 0)` when the reduce manager resolves no stream (`DataExtensions.cs:3092-3104`). `ReadCollectionConfigs` then yields null → **404**, in milliseconds. |
| **No handler matched at all** | `MessageHub.FinishDelivery` NACKs any unhandled `IRequest<>` with a typed `DeliveryFailure(NotFound)` (`MessageHub.cs:936-965`). |
| **The permission fold reached no verdict** | `AccessControlPipeline` answers `DeliveryFailure` with `Unavailable` / `ShuttingDown` (`AccessControlPipeline.cs:618-641`) — a fast 503, not a silent one. |
| **A storm breaker latched by the first early read** | The per-key breaker trips at **2000 messages/second on one key** (`MessageStormBreaker.cs:92`) with a 2 s cooldown (`:100`); the aggregate shed only touches `[CanBeIgnored]` traffic (`:505`) and `GetDataRequest` is not (`Messages.cs:318-319`). A browser cannot reach either. |
| **Per-node hub warm-up** | 🚨 **Refuted for #2901's occurrence ONLY — this is Cause C and it is real.** The #2901 measurement rules it out *there*: `AgenticPrimer/content/og.png` answered in 0.9 s while `AgenticPrimer/content/videos/skills.mp4` — the *same* node, the *same* `GetDataRequest` — took 10.3 s in the same window, and an activating hub cannot answer one and drop the other. That argument turns on the two files sharing an owner; it says nothing about a read whose owner has not started at all. See *"Cause C"* below. |
| **A settle window on the uploaded file** | Nothing on the path is keyed to the file. The bytes are read directly from the store; the blob provider has no monitor at all (`AzureBlobStreamProvider.AttachMonitor` returns null), and the file-system monitor drops every non-`.md` event at the watcher callback (`FileSystemStreamProvider.cs:272-277`), so 32 binaries neither ingest nor lengthen collection init. |

### The per-file appearance is a cache key, not a settle window

The one per-file thing on the whole path is `PathResolutionService`'s value cache, whose key is the
**full joined path including the file name** (`PathResolutionService.cs:442-505`). So every distinct
file URL is its own entry and its own storage query on first request, and a warm URL replays
synchronously (`:456-458`) while a cold sibling pays a live cross-schema query. That produces
per-file *latency* differences. It does not produce the 503 — step 3 is identical for both files.

## Cause A — the reply cannot be delivered

The owning hub produces the `GetDataResponse` and the router refuses to carry it, because the
grain directory holds no claim for `portal/nodeops-{meshId}`.

`portal` is a stream-routed address type (`MeshConfiguration.cs:69-70`), so `RoutingGrain` takes the
directed pod-hub route. On a refusal it reaches `AnswerPodHubNotHere`
(`RoutingGrain.cs:610-652`), and `ClientHostedAddressTypes` is **empty in production by explicit
design** (`MeshConfiguration.cs:78-82`) — so there is no stream fallback. The transient
`DeliveryFailure` is posted **to the sender** (`RoutingGrain.cs:650`), i.e. to the *responding* node
hub. The requester is told nothing and can only wait out its budget.

This is [#2938 / #2915](https://github.com/Systemorph/MeshWeaver/issues/2938), root-caused on
[#2901](https://github.com/Systemorph/MeshWeaver/issues/2901) on 2026-09-02 from production logs
(33 occurrences in 24 h, every one on a single pod, onset in the same hour as a silo death). The
cure is [The Pod-Hub Claim Must Be Re-Asserted](../PodHubClaimReassertion), merged as `f37f3fc87`.

**Signature.** The `HubUnreachableException` message carries the reader's own snapshot
(`ReadBudget.cs:234-245` → `MessageHub.GetPendingRequestDiagnostics`, `:2116-2136`):

```text
Reader: Hub portal/nodeops-… RunLevel=Started Queue(buffer=0,deferred=0,exec=0)
        PendingCallbacks=1[…=GetDataRequest@DoublePendulum(10003ms)]
```

**Empty queue, nothing executing.** The reader processed everything delivered to it; the silence is
upstream. Peer pods carry the matching `[ROUTE] Directed delivery to pod hub '…' was refused` line.

> 🚨 `exec=` in the captures on this page is the **pre-#3593 name** of a field that was a hard-coded
> literal `0` — it read the same in every state and carried no information. Today the same position
> prints `drainsInFlight=`, a real count. Nothing on this page depended on it: the discriminator
> below is `buffer` and the `Executing(…)` line. See
> [Reading a Disposal Stall Verdict](/Doc/Architecture/DisposalStallVerdicts).

## Cause B — the reply is delivered and not dispatched

`portal/nodeops-{meshId}` is *also* the mesh's ONE node-CRUD execution hub. Every
`CreateNodeRequest` / `CreateOrUpdateNodeRequest` issued from the router runs there and is
serialised on the same action block (`MeshService.cs:93` issues on it, `:104-108` targets
`NodeOperationTarget`, whose fallback is that same hub, `MeshExtensions.cs:325-326`). The turn loop
runs exactly one turn at a time and does not advance until the current turn's observable completes
(`MessageService.cs:945-1006`).

That this hub stops draining for tens of seconds under a bulk node-CRUD burst is **measured**, from
a different direction, in [Bake Seal — NodeOps Saturation](../BakeSealNodeOpsSaturation): queue
latency bimodal at ≤ 3.2 s / 33–49 s, and one capture of
`Queue(buffer=45,…) Executing(CreateNodeRequest, 24888ms)`.

**A content upload burst is a node-CRUD burst.** With the indexing pipeline registered, each
uploaded file starts its own Activity — and until 2026-09-10 `ContentIndexingObserver.OnUploaded`
fired them **unbounded and fully parallel**, one per file: it subscribed the per-file activity
inline, the instant the seam was raised, while its own sibling walk sequenced the identical work
with `.Concat()`. Per file, on `portal/nodeops-{meshId}`:

| Work | Deliveries on that action block |
|---|---|
| `meshService.CreateNode` for the `_Activity` node (`ContentIndexingActivity.cs:123`) | request + response |
| `MeshDocumentSink.WriteDocument` → `CreateOrUpdateNodeRequest` for `_Documents/{slug}` (`MeshDocumentSink.cs:64-65`) | request + response |

…plus one fresh per-node hub activation for `{owner}/_Activity/{id}`, routed through the mesh
router's own single-threaded block, and ~3 activity-log `stream.Update`s on `cache/{meshId}`.
Thirty-two files is on the order of a hundred deliveries. The burst does not drain when the files
are written — it drains when the slowest indexing leg finishes, and for image posters that leg is a
**vision-model round trip on the `Http` pool, capped at 16** (`ContentIndexingService.cs:215-224` →
`ChatClientImageDescriber.cs:72`; `IoPoolOptions.cs:171`). That is where *minutes* comes from, and
why it heals untouched.

### The fan-out that produced the burst — re-measured, and bounded

🚨 **Re-read on 2026-09-10 at the same coordinates: the mechanism above was still live, verbatim.**
`ContentIndexingObserver.OnUploaded` (Plugins, `ContentIndexingObserver.cs:107-116`) was
`IndexFileActivity(collectionPath, filePath).Subscribe(...)` — one activity per file, subscribed the
moment the seam fired, with no bound of any kind, while `ReindexCollection` sequenced the identical
per-file work with `.Concat()` at `:270`. It is now bounded
([MeshWeaver.Plugins#1616](https://github.com/Systemorph/MeshWeaver.Plugins/pull/1616)): uploads
push onto an instance channel the observer drains with
`.Merge(ContentIndexingObserver.MaxConcurrentUploadIndexing)`, so at most **four** indexing
activities are ever in flight. `OnUploaded` still returns immediately — the surplus is queued by
`Merge`, not by a gate of our own.

🚨 **The asymmetry that decides the bound is not where a reader first looks.** The expensive leg is
the model round trip, and it is *already* bounded — 16, by the shared `Http` pool, as the paragraph
above says. So fan-out past that cap buys **no throughput at all**: the surplus activities exist
only to queue at the Http gate, and every one of them has ALREADY paid its node-CRUD cost in full,
up front, before reaching the model. The burst on `portal/nodeops-{meshId}` was therefore pure cost
with no compensating benefit — which is exactly why bounding the fan-out is not a slowdown.

**Why not sequence, and why not a pool slot.** Both were candidates; each is refused by a
measurement rather than a preference.

| Candidate | Why not |
|---|---|
| **Sequence it — `.Concat()`, the sibling walk's own shape** | `ReindexAll` is ONE activity for a whole collection, so ordering its log costs it nothing. Here each file is its own activity, so a bound of 1 does not reduce the node-CRUD *count* at all — it only paces it, which a bound of 4 already does — while giving up a 4x on work the `Http` pool is happy to run concurrently. |
| **Hold an `IIoPool` slot for the whole activity** (the `Ai` pool's stated "runaway-fan-out STOP" shape) | The right instinct, refused on two counts. The activity's own inner legs take `FileSystem`, `Http` and `Query` slots, so the outer bound must be a pool the inner work never re-enters — a nested acquisition of a pool you already hold is the classic gate deadlock the framework names at `IoPoolOptions.cs:55-59`. A *new* pool name is available to a plugin, but its cap falls to `IoPoolOptions.Default` = `Environment.ProcessorCount`, which is a fallback rather than a reasoned bound, and giving it a real one is a change to `IoPoolOptions` here in core that the plugins repo cannot reach behind its pin. |
| **`.Merge(n)` on an instance channel** (chosen) | The fleet's established bounded fan-out for this exact shape: `.Merge(4)` wherever the per-item work writes mesh nodes (`AiSourcesInstallHook`, `AiContentDiskWriter`, `IssueService.cs:83`, `GitHubWebhookProcessor.cs:154`), `.Merge(8)` where it does not (`GitHubSyncService.cs:214`, `OctokitGitHubRepoClient.cs:223`). `.Concat()` **is** `Merge(1)`, so the sibling walk and the new pump are one operator at two settings - nothing hand-woven, no `SemaphoreSlim`, no pacer, no queue of our own. |

**The control, both directions.** `UploadIndexingFanOutBoundTest` (Plugins,
`MeshWeaver.ContentCollections.Indexing.Graph.Test`) runs on a monolith mesh with a real
file-system collection and no sleeps. Its instrument is the summarizer: `ContentIndexingService`
calls it exactly ONCE per document, from inside that file's activity, so concurrent calls **are**
concurrent activities — and the test's summarizer PARKS, which holds every admitted file inside the
pipeline long enough for the number to be read. Twelve files raised through `RaiseContentUploaded`
in one burst, measured 2026-09-10:

| Observer | Peak concurrent indexing activities | Verdict |
|---|---|---|
| `.Merge(4)` (bounded) | **4** — 4 of 12 admitted while parked, all 12 indexed after release | green |
| `.Merge()` (the pre-fix shape) | **12** — the whole burst at once | red: *"the upload to index fan-out must be bounded at 4: 12 files were uploaded in one burst and 12 indexing activities ran at once"* |

The green run also asserts the *lower* half — the peak must REACH 4, so a future change that
over-serialises the pump is red too — and the positive control that every one of the 12 files is
still indexed, because a bound that indexed nothing would satisfy a ceiling assertion on its own.

**Signature.**

```text
Reader: Hub portal/nodeops-… RunLevel=Started Queue(buffer=N>0,deferred=0,exec=…)
        Executing(<message>, <thousands>ms) PendingCallbacks=…
```

## Cause C — the owning hub had not finished STARTING

Nothing is lost and nothing is saturated. The read is the FIRST thing to address that node since the
process started, so the `GetDataRequest` is what *creates* the owning hub — and until that hub's last
initialization gate opens, `MessageService` parks every arriving delivery in the **deferred queue**
instead of dispatching it (`MessageService.cs:1541-1544`, enqueue `:1687-1692`). The read therefore
does not measure the owner's health. It measures the owner's START-UP, and reports a node that is
present, permitted and about to answer as unreachable.

Filed as [#3931](https://github.com/Systemorph/MeshWeaver/issues/3931).

**The bounds are inverted, and that is the whole defect.** `ReadBudget`'s own remarks state the rule:
*"a bound nested inside another bound must be able to fire FIRST, because it is the only one that
knows WHICH read starved."* On this path every inner bound is LARGER than the budget waiting on it:

| Bound on the work the read triggers | Value | Where |
|---|---|---|
| the reader's budget | **10 s** | `ReadBudget.cs` → `Default` |
| deferred delivery, per message | 30 s | `MessageService.cs:151` → `DeferralTimeout` |
| the hub's own initialization turn | 120 s | `MessageHub.cs:210` → `DefaultInitializationTimeout` |
| `DataContext` initialization | 120 s | `DataContext.cs:144` → `InitializationTimeout` |
| monolith path resolution on the routing path | 30 s | `RoutingServiceBase.cs:306` |
| Orleans first-node resolution | 30 s | `MessageHubGrain.cs:88` → `FirstNodeResolutionTimeout` |
| NodeType slow path (no progress) | 30 s | `NodeTypeEnrichmentHelpers.cs:69` → `SlowPathTimeout` |

So the 10 s ALWAYS fires first, no inner bound can ever deliver its diagnosis to this caller, and
every Cause C occurrence is reported in the outermost, least informative terms. See
[Bounds Must Be Ordered](../BoundsMustBeOrdered).

**Why it is invisible until it is not.** A cold start costs ~0.1 s on a quiet replica — measured
2026-09-10 on memex.meshweaver.cloud, where five previously-untouched owning nodes (`Doc/DataMesh`,
`Doc/Architecture/MessageBasedCommunication`, `…/UserInterface`, `…/AccessControl`,
`…/BusinessRules`) each served their first asset in **0.14–0.20 s**. The budget is 100× that, so
nothing is ever seen. It becomes visible only while the replica is doing something that makes a
start-up cost tens of seconds — a roll, a restart with its boot bake (`PreWarmCompletion`'s own
measurement: ~10 min of sequential rebuilds, across three production portals), or a bulk import into
the partition being read.

### Production capture, 2026-09-10

The `Prod synthetic probe` failed twice, 16 minutes apart, and both runs read the same asset three
times with a 2 s gap:

```text
14:46:43  round 1: platform=503  absent-control=404
14:46:56  round 2: platform=503  absent-control=404
14:47:08  round 3: platform=503  absent-control=404
15:02:28  round 1: platform=503  absent-control=404
15:02:40  round 2: platform=503  absent-control=404
15:02:43  round 3: platform=200  absent-control=404
```

The 15:02 run is the whole shape in six lines: **two consecutive budgets burned, then the same URL
served in 0.2 s about 24 s after the first attempt** — a start-up that completed on its own, inside
every one of its own bounds and outside the reader's. The absent control answered 404 in every round
of both runs, so path resolution (step 2) was never the problem.

What that replica had been doing is in the mesh: `Doc/_Activity/import-bfcebef8e3a0b6ac` —
**"Import Doc (1446 nodes)"**, 14:35:19→14:35:26Z, eleven minutes before the first failure — and a
fleet-wide NodeType bake wave whose `_Activity/compile-state` nodes are stamped 15:04:58→15:05:23Z
across `Reinsurance`, `Edu`, `Planning`, `Store`, `Chess` and two dozen more partitions. By 15:31Z
every cold read measured 0.14–0.20 s again, and the probe has been green since.

### The mechanism, and the two facts that identify it

- The failing unit is the **OWNING NODE**, exactly once. Every later read of *any* file under that
  node is fast, because the hub is now hosted; a different node read afterwards pays its own
  start-up. That is neither Cause A (a per-hub claim, which would fail every read alike until it is
  re-asserted) nor Cause B (a burst, which is per-window rather than per-node).
- The impossible path keeps answering **404 in milliseconds**, because an unmatched reference returns
  at step 2 and never reaches the bounded read (`ContentFileResolver.cs` → `NotFound("No matching
  node found for path")`).

### 🚨 Cause C wears Cause A's signature — the reader clause cannot see it

This is the trap, and it cost two sessions on #3931 before it was named. The discriminator below
reads the **READER's** queue. Under Cause C the reader — `portal/reads-{meshId}`, a hub that
registers no handlers — is completely idle, so it prints Cause A's signature *exactly*. Verbatim,
from `ContentReadPaysTheOwningHubsStartupTest` run against the pre-#3931 code, on a mesh where the
owning hub was in the same process and answered milliseconds later:

```text
Reading content collection config from 'TestData/ColdProbe' gave up after 10s — the owning hub
never answered. … Reader: Hub portal/reads-gNtsgKCSMUOiivUSuHw-ag RunLevel=Started
Queue(buffer=0,deferred=0,drainsInFlight=0)
PendingCallbacks=1[…=GetDataRequest@TestData/ColdProbe(10002ms)]
Target: NO LOCAL HUB at 'TestData/ColdProbe' — it never activated in this process (or it is owned
by another silo and the reply was lost in transit).
```

`Queue(buffer=0,…)`, nothing executing — *"Cause A: the reply never arrived"* by the rule below, on a
run where the reply arrived fine. **Two things in that line were wrong, and both are fixed:**

1. **"the owning hub never answered" is a claim the reader cannot support.** A budget knows one
   thing: that no notification arrived in time. It now says that instead.
2. **`Target: NO LOCAL HUB` was a CONSTANT, not evidence.** `GetHostedHub` searches one hub's own
   collection — it walks neither the hosted tree nor the parent chain — and every per-node hub is
   hosted by the **mesh** hub (`MonolithRoutingService.CreateHub`,
   `MessageHubGrain.CompleteActivation`). The probe asked the READER, which hosts nothing, so it
   answered "NO LOCAL HUB" for every read on this seam whether or not the hub was sitting right
   there. It now asks the mesh hub, and when the answer is a hub that has not reached
   `RunLevel=Started` it says so in words.

### Cause C's discriminator — read the TARGET clause

```text
Target: STILL STARTING — '<owner>' … RunLevel=Starting Queue(…,deferred=N,…)
    ⇒ Cause C: the read was DEFERRED behind the owner's start-up. It is not lost.
Target: Hub <owner> RunLevel=Started Queue(buffer=0,deferred=0,…)
    ⇒ the owner was up: fall through to the reader clause and decide A vs B there.
Target: NO LOCAL HUB at '<owner>'
    ⇒ the owner is not in THIS process. Not decidable from this line — use the black-box table.
```

### The black-box discriminator — no logs required

🚨 This is the durable one, because it needs **no log at all** — everything below is `curl` against
the public route, from anywhere, with no credential and no portal.

> 🚨 **It is NOT durable because the log line is unreachable.** That reading — *"`search
> 'nodeType:Hosting/LogEntry'` returns no content-route entries, so the ingest carries nothing from
> this route"* — was measured on 2026-09-10 and is a **wrong inference from a correct observation**.
> `Hosting/LogEntry` is not a feed: it is the output of one `Logs` `Hosting/InstanceAction`, and
> that portal's whole population is 13 rows from one query about assessment routing. The log line
> IS reachable through the sanctioned API — see *"What the discriminator costs to obtain"* below and
> [Log Entries Are a Query Result, Not a Feed](../LogEntriesAreAQueryResult). Prefer the black-box
> table when you want an answer with no portal access; prefer the line when you want the run level
> and the queue depths, which the black box cannot give you.

| Observation | Cause C | Cause A | Cause B |
|---|---|---|---|
| unit of failure | the OWNING NODE, once | the reader's hub — every read alike | whatever lands inside the burst |
| the same URL, read again | fast, and stays fast | fails identically | fails while the burst lasts |
| a DIFFERENT node, read after the first healed | pays its own start-up and can fail too | already cured once the claim is re-asserted | tracks the burst, not the node |
| the impossible path | 404 in ms | 404 in ms | 404 in ms |
| when | within minutes of a roll, a restart, or a bulk import into that partition | after a silo death | during a bulk node-CRUD burst |
| how it clears | on its own, per node, permanently | needs the pod-hub claim re-asserted | when the burst drains |

### 🚨 What Cause C does NOT have: a cure

Causes A and B each have one. Cause C does not, and picking one is a decision rather than a patch,
because **every exit is either forbidden or changes what the route is**:

1. **Wait for the start-up.** The read budget would have to be ordered outside the target's own
   envelope (≥ 30 s). That is widening `ReadBudget.Default`, which AGENTS.md forbids — *"over budget
   means STUCK, not slow"* — and the measurement above says the read is not stuck, so the honest
   version of this exit is "derive the budget from `MessageService.DeferralTimeout` and pin the
   ordering with a guard". It is still a bound change and it is still a decision.
2. **Answer without the owning hub.** The collection config is deliberately uncached and deliberately
   gated by the owner's `[RequiresPermission(Read)]` — see
   [CQRS and Content Access](../CqrsAndContentAccess). Caching it *is* the short-circuit this page's
   own class remarks refuse.
3. **Keep interactive traffic off a replica that cannot yet serve it.** Readiness, not budget: a
   replica whose boot bake has not settled stays out of the load balancer. This is the only exit that
   removes the user-visible symptom without widening a bound or retrying, and its cost is that a roll
   takes as long as the bake — a fleet-level trade-off, not a code change.

Until one is taken, what #3931 buys is that an occurrence is now **identifiable in one line** instead
of being misfiled as Cause A. The repro is `ContentReadPaysTheOwningHubsStartupTest`
(`test/MeshWeaver.Graph.Test`): one node's reactive initialization is parked on a subject the test
completes, the content read must say what it observed, and the identical read resolves the moment the
gate opens — which is what makes the 503 a false negative rather than a measurement.

## 🚨 The discriminator

🚨 **Read the TARGET clause FIRST, then this one.** The rule below separates A from B and
**cannot see Cause C at all** — it snapshots the READER, and a start-up leaves the reader idle, so
Cause C satisfies the `Queue(buffer=0,…)` test perfectly. Rule out Cause C on the target clause
(*"Cause C's discriminator"* above) before reading a word of this.

A and B are then separated by **one field in a line the framework already prints** — no new
instrumentation, no cluster access beyond the log:

```text
Queue(buffer=0,…) and no Executing(…)   ⇒ Cause A: the reply never arrived — IF the owner was Started.
Queue(buffer>0,…) or Executing(…, Nms)  ⇒ Cause B: the reply arrived and waited.
```

### What the discriminator costs to obtain

🚨 **`Reading content collection config from …` is not a log statement.** Nothing in this repo calls
`Log*` with it. It is the MESSAGE of the `HubUnreachableException` that `ReadBudget.Unreachable`
builds (`ReadBudget.cs`), and it reaches a log only because `BlazorHostingExtensions.ContentFailure`
(Plugins) does `logger.LogWarning(ex, "Content read timed out for {Path}", path)` — the exception
rides along as the warning's detail. Three things follow, and each has misled a reader:

- **It is greppable, and on ONE line — BOTH clauses.** `MessageHub.GetPendingRequestDiagnostics` is
  a single-line snapshot by contract, so `Reader: … Queue(buffer=…) [Executing(…, Nms)]
  PendingCallbacks=…` **and** the `Target: …` clause that decides Cause C both sit on the same
  physical line as the sentence above. *"Read the `Queue(` on the same line"* is therefore literally
  true, and one grep hit carries the whole three-way verdict — which is exactly why the line is
  worth fetching rather than reconstructing. `ContentRoute503DiscriminatorGuard` (core,
  `test/MeshWeaver.Documentation.Test`) fails if a newline ever gets into it.
- **No level or category filter can select it.** It is a continuation line: the `warn:` header and
  the `MeshWeaver.Hosting.Blazor…` category are on the PRECEDING line, which the log store keeps as
  a separate entry.
- 🚨 **The obvious query returns the 503 without the discriminator.** Filtering for `Content read
  timed out for` matches the warning's own message line and stops there. Filter for
  **`Reading content collection config from`** — the line that carries the answer.

**Through the API, not by break-glass.** A `Logs` `Hosting/InstanceAction` on the control instance
takes the LogQL, so this is one node and no cluster credential:

```json
{ "namespace": "Ops/Actions", "nodeType": "Hosting/InstanceAction",
  "content": { "$type": "InstanceActionContent", "deployment": "Deployments/memex-cloud",
    "requestedAction": "Logs", "query": "Reading content collection config from",
    "sinceMinutes": 240, "limit": 200,
    "reason": "Read-only — read the Target clause for Cause C, then the Reader clause for A vs B." } }
```

Read `logQl`, `entryCount` and `truncated` back off the run; the matched lines land as
`Hosting/LogEntry` nodes under `Ops/Logs`. 🚨 **Do NOT instead search the `Hosting/LogEntry` nodes
that are already there** — they are the result of whatever somebody last asked, not a feed, and
reading them as one produced a wrong conclusion on
[#3931](https://github.com/Systemorph/MeshWeaver/issues/3931). See
[Log Entries Are a Query Result, Not a Feed](../LogEntriesAreAQueryResult).

The break-glass form of the same read — `az aks command invoke … curl loki…/query_range`, then
`grep -o "Queue(buffer=[0-9]*"` bucketed over a window — still answers it for a whole day at once,
and is the fallback when the control plane itself cannot act.

**Do not apply Cause A's fix to a Cause B occurrence.** Re-asserting a claim that was never lost
changes nothing, and a single-replica or restarted portal removes the *exposure* to Cause A without
touching Cause B — which is why a 12/12 green probe proves neither. And do not apply either fix to
a Cause C occurrence: neither one touches how long a per-node hub takes to start.

## 🚨 Cause B's cure — the read does not belong on that block at all

The fix is not to make the node-CRUD hub drain faster. It is that **a bounded read with a person
waiting on it must never be issued on the hub that EXECUTES the mesh's node CRUD**, whatever that
CRUD costs. A serial execution hub occupied by a bulk write burst is that hub working as designed;
a ten-second interactive read queued behind it is not.

`MeshExtensions.ReadIssuingHub()` is the seam. It hops a **router**-held caller onto
`portal/reads-{meshId}` — a hub wired exactly as `portal/nodeops-{meshId}` is (the mesh's own type
registry, the mesh hub's permission evaluator, registered with the routing service so replies land
on it cross-silo) with **one deliberate difference: it registers no handlers.** Nothing executes
there, so the only thing its action block ever dispatches is the reply to a read issued on it. Two
call sites moved:

| Read | Was | Now |
|---|---|---|
| `ContentFileResolver.Resolve` — the collection-config `GetDataRequest` (step 3 above) | `NodeOperationIssuingHub()` ⇒ `portal/nodeops-{meshId}` | `ReadIssuingHub()` ⇒ `portal/reads-{meshId}` |
| `MeshNodeStreamExtensions.GetMeshNodeOutcome` — the one-shot node read | same | same |

`NodeOperationIssuingHub()` is unchanged and still correct for a **write**: a target-less
`CreateOrUpdateNodeRequest` posted there executes on the node-CRUD hub, and a write ack that waits
behind the writes queued ahead of it is the ordering that hub exists to impose. The two seams differ
because reads and writes want opposite things from the same block.

### The repro

`ContentReadIsNotQueuedBehindNodeCrudTest` (`test/MeshWeaver.Graph.Test`) — a monolith mesh, no
sleeps, no cluster. An `INodeValidator` parks the create of ONE node (matched by path, so nothing
else in the mesh is slowed), which holds the node-CRUD execution hub's turn exactly as a real write
does; with the block held, `ContentFileResolver.Resolve` must still answer. Reverting the two call
sites to `NodeOperationIssuingHub()` reproduces this page's Cause B **verbatim**, including the
discriminator:

```text
Reading content collection config from 'TestData/ContentProbe' gave up after 10s — the owning hub
never answered. … Reader: Hub portal/nodeops-GMUrd3FU90aD8lxUt_XUlw RunLevel=Started
Queue(buffer=1,deferred=0,exec=0) Executing(CreateNodeResponse, 10004ms)
PendingCallbacks=1[…=GetDataRequest@TestData/ContentProbe(10002ms)]
```

`Queue(buffer=1)` and an `Executing(…, 10004ms)` line — Cause B by the discriminator above, on a
run where the owning hub answered promptly. Note *what* is executing: the create's continuation is
running inside the turn that delivered an intermediate `CreateNodeResponse` to that hub, so the
chain's remaining legs are charged to a node-CRUD delivery turn. That is the mechanism by which a
single create can occupy the block far longer than any one of its own steps.

### What it does NOT fix

**Nothing about how long a node-CRUD turn takes.** [#2543](https://github.com/Systemorph/MeshWeaver/issues/2543)
— the same hub seen from the write side, where a `CreateNodeRequest` turn was captured at 24 888 ms
and queue latency is bimodal at ≤ 3.2 s / 33–49 s — is untouched and stays open. What this change
does take off that block is the **router-issued reads**: the `PendingCallbacks=26[GetDataRequest@Store/Core,
@Store/Install, …]` in #2543's own capture are `GetMeshNode` reads issued from mesh-singleton
services that hold the DI root hub, and every one of them now registers on `portal/reads-{meshId}`
instead. That removes a contributor and, more usefully, removes a confound: pending callbacks left on
`portal/nodeops` after this change were issued by something running **on that hub**, not by a
router-held caller.

## Recorded, not fixed

Two unbounded seams on this exact path. Neither causes the 503; both make an occurrence harder to
read.

1. **`ResolvePath` is unbounded here** (`ContentFileResolver.cs:114-117`) while the routing path
   bounds the identical call at 30 s (`RoutingServiceBase.cs:304-306`). A resolution that never
   emits — a documented shape, pinned by `PathResolutionCachePoisonTest.HungFirstQuery_DoesNotPoisonCache` —
   never even subscribes step 3's budget timer, so the request hangs until the client aborts with
   **no 503 and no log line**. Strictly worse than the failure this page is about.
2. **The issuing hub is re-resolved per request** (`ContentFileResolver.cs`, now `ReadIssuingHub()`)
   rather than cached as `MeshService.cs:93` does. During mesh teardown it returns **the router**
   (`MeshExtensions.MeshReadHub` returns null past `DisposeHostedHubs`), reinstating the
   router-as-both-ends hang the long comment above that call exists to prevent.

A third lived in the plugins repo — the per-file indexing fan-out had **no concurrency bound** while
its own sibling walk did. Re-measured 2026-09-10, found still live at the same coordinates, and
**fixed**: see *"The fan-out that produced the burst"* under Cause B above.

## On answering "not ready" with 409/425

Half of #2901's second ask is already true: the 503 is not generic. It is the deliberate answer for
*"no verdict was reached"*, kept distinct from the 404 a missing or refused file gets — see
`ContentUnavailable` and `PermissionApi` → *"The anonymous gate is tri-state too"*. What stands is
that 503 is also the code for *down*, so a monitor cannot separate the two. Under the A/B analysis that complaint had no
"still settling" case left to serve: both causes are genuine unavailability of a dependency, and both
are correctly 503.

🚨 **Cause C reopens exactly that question, because it IS a ready-later state.** The owning
hub is starting, it will answer, and the answer is usually seconds away — which is what a 503 cannot
express and a monitor cannot separate from *down*. That is one more input to the decision recorded
under *"What Cause C does NOT have: a cure"*, not a change to make on its own: a distinct status is
only worth introducing alongside whichever exit is taken there, because the browser treats an
`<img>` the same way whatever the code is.

## Related

- [Log Entries Are a Query Result, Not a Feed](../LogEntriesAreAQueryResult) — how to fetch the discriminator through the API, and why the log nodes already on a portal are not a feed.
- [Bake Seal — NodeOps Saturation](../BakeSealNodeOpsSaturation) — the same hub, measured from the write side, with the open question of *which rule* holds its block.
- [The Pod-Hub Claim Must Be Re-Asserted](../PodHubClaimReassertion) — Cause A's mechanism and cure.
- [Action-Block Wedge Prevention](../ActionBlockWedgePrevention) — the invariants a single-threaded hub must satisfy.
- [Bounds Must Be Ordered](../BoundsMustBeOrdered) — why an inner bound just under an outer one destroys the outer one's diagnosis.
- [Controlled I/O Pooling](../ControlledIoPooling) — where per-file work belongs instead of on an action block.
- [CQRS and Content Access](../CqrsAndContentAccess) — why the collection-config read is a request/response and deliberately uncached.
