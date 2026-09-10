---
Name: Write Verdict Totality
Category: Architecture
Description: Every cross-hub stream.Update must reach a terminal. The base read is the seam where a write can silently reach none — a source that completes with no value settles nothing, posts no patch, and arms no deadline.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>
---

# Write Verdict Totality

**A writer must always be told what happened.** `GetMeshNodeStream(path).Update(...)` is the only
mutation API, and every caller composes on its terminal: `.Subscribe(onNext, onError)`, a queue that
advances when the write settles, a test that waits for it, a hub whose disposal drains it. A write
that reaches **no** terminal is not a slow write — it is a permanent, silent hang of everything
downstream of it, and nothing is logged, because from the message layer's point of view the request
that started it succeeded long ago.

This page is about the one seam where that used to be possible, and the rule that closes it.

## The three terminations, and the one that had no handler

A cross-hub write (`MeshNodeStreamHandle.UpdateRemote`, reached through
[the one mutation API](../DataAccessPatterns)) is built in two stages:

1. **Read the base** — the node's current state, off this hub's mirror of it.
2. **Diff, post, and wait for the owner's verdict.**

Stage 2 is where every verdict the caller can receive is raised: the no-op short-circuits, the
owner's ack (early or late), each NACK, the delivery failure, and the outer
`VERDICT_TIMEOUT` deadline. All of them live **inside stage 1's `onNext` callback**, because there is
nothing to post until a base has arrived.

Stage 1 subscribed with two of Rx's three terminations:

```csharp
var initialSub = PatchBaseSource(RebaseSource(mirror, …), pendingSelfWrite)
    .Subscribe(
        current => { /* …diff, post, arm every deadline, raise every verdict… */ },
        ex      => { /* …fail the caller… */ });
        // ← no onCompleted
```

So a base read that **completes without a value** settled nothing at all:

- no `OnNext` and no `OnError` on the caller's observer;
- no `PatchDataRequest` posted, so no owner verdict could ever arrive;
- and **not even the outer verdict deadline**, which is armed inside the response wait that a write
  with no base never reaches.

The caller waits for the life of the process.

## Why a mirror ends without a value

This is not a theoretical Rx shape — it is the documented disposal contract. `SynchronizationStream`
does **not** dispose its store; it completes it:

```csharp
// COMPLETE the store — deliberately do NOT dispose it (#1170/#1171). OnCompleted
// detaches every subscriber, and per the Rx grammar a completed subject silently
// ignores any further OnNext/OnError/OnCompleted …
Store.OnCompleted();
```

So a stream that was completed **before it ever carried a value** replays exactly one thing to a new
subscriber: a bare `OnCompleted`. (A stream that did carry one replays value-then-completion, and
`onNext` runs normally — this is only about the empty case.) Three routine paths produce it:

| Path | What happens |
|---|---|
| `Workspace.AcquireRemoteStreamUnchecked` resolves a stream that was **already dead** | It deliberately returns it with an empty lease — "let the caller's subscribe collect the terminal". The terminal is a bare completion. |
| The last **lease** on an evicted mirror is released mid-write | `ReclaimIfUnheld` disposes the stream there and then. |
| The write path's own `Where(change => change.Value is not null)` drops every emission | Filtering introduces a completion the unfiltered source could not produce. |

The first two are load-dependent, and they are why this appeared under **concurrency**: N writers to
one path share one leased mirror, so one of them collecting the bare completion reads as *"N writes
started, N−1 finished"* — with nothing in the log to distinguish it from work still in flight.

## The rule

> **A source that cannot answer must SAY so.**

Every base read passes through one guard, `MeshNodeStreamHandle.RequireBaseState`, which converts an
empty completion into a terminal error:

```csharp
baseRead
    .Select(node => (MeshNode?)node)
    .DefaultIfEmpty(null)
    .SelectMany(node => node is not null
        ? Observable.Return(node)
        : Observable.Throw<MeshNode>(new InvalidOperationException(
            "Update aborted: this hub's mirror ended without ever carrying the node's state, so no "
            + "patch was built and none was posted. The write did NOT land; re-issue it.")));
```

**An error, not a value** — and that direction is the whole point. No base means no diff, which means
no `PatchDataRequest` was ever posted: the write *provably* did not happen. Emitting the unchanged
node instead would report "saved" for a write nobody attempted, which is the fail-open that
[the owner's commit verdict](../CqrsAndContentAccess) exists to prevent.

The guard is invisible to every write that has a base — which is all of them, normally. It only adds
a terminal to the case that had none, so it can turn a hang into an error and nothing else.

### Where it was, and why one branch was not enough

The rule was already written down — beside the **conflict re-attempt** branch of `RebaseSource`,
which filters the mirror for a version newer than the one the owner refused and can therefore end
empty. That branch got a `DefaultIfEmpty` guard and a comment stating the rule in general terms. The
**first-attempt** branch — the ordinary write, every caller write in the mesh — was a bare
`mirror.Take(1)` and got none, because the un-filtered shape *looks* like it cannot end empty.

It can: a disposed mirror ends empty whatever you do to it. The guard now wraps both branches at one
seam, and the two diagnostics stay distinct — "the mirror never carried the node" and "the owner
refused this at version N and the mirror never moved past it" send an operator to different places.

The same two-callback subscribe existed on the sync-stream write path
(`WriteViaSyncStream`, the Overwrite/bypass route) and carries the same guard for the same reason.

### The owner side has the same seam

The owner's ack watcher (`ApplyMeshNodePatchInTurn`, `DataExtensions.cs`) is a `.Take(1)` over the
owner's reduced stream waiting for the emission that carries the write, and it was subscribed with the
same two arms. A reduced stream that completes without emitting — mirror eviction disposing a
`SynchronizationStream` while the hub lives — ended the watcher with no ack posted, and the writer
reported `OwnerUnreachable` for a write the owner may have committed (issue #3033). It is now armed
through `ArmPatchAckWatcher` over the `WhenCompletesEmpty` operator — "run this callback if the
source completes without ever having emitted" — which is `RequireBaseState`'s idea in operator form,
with one twist the owner side needs and the writer side does not: the callback must fire ONLY for a
completion that no emission preceded, because on the happy path `Take(1)` completes while the durable
flush started in `onNext` is still in flight, and a bare completion arm would NACK the write that is
about to succeed. Which verdict each path posts, and why the code is `OwnerDisposing`, is in
[Reading a Write Verdict](../ReadingAWriteVerdict).

## The FOURTH outcome — never emits, never completes

The three terminations above are the ones Rx *delivers*. There is a fourth outcome, and it delivers
nothing at all: **a source that neither emits nor completes**. `WhenCompletesEmpty` cannot see it —
there is no completion to observe — and neither can an `onError` arm. It is settled as silence by
every guard on this page.

It is not hypothetical. A stream's `Store` is a `ReplaySubject`, so one that has never published and
is never disposed hands `Take(1)` nothing, for the life of the process. Two owner-side legs had it,
in different shapes (#3194, #3195):

| Leg | What it had | What was missing |
|---|---|---|
| The generic patch path's initial base read | `Take(1)` + empty-completion arm | **No bound anywhere.** The only other bounded watcher on that path is armed *inside* the `onNext` arm, so a stream that never emits never arms it |
| The cold-activation defer | a 10 s bound | **No completion arm.** A completing primary store passes `Take(1)` and *cancels the Timeout* — a terminal disposes the timer — so neither arm runs |

Both now compose through one seam, `DataExtensions.ArmedOneShotRead`:

```csharp
source                      // already filtered by the caller
    .Take(1)
    .Timeout(bound, scheduler)
    .WhenCompletesEmpty(onEmptyCompletion);
```

Four outcomes, four terminals. And the seam exists rather than three hand-written chains for a
second reason: **the bound sits below the caller's filter.** A `Timeout` placed *above* an operator
that drops emissions is re-armed by every dropped one, so on a busy stream it can never fire — a
bound that is present, reviewed, and unreachable, which is exactly what
[Bounds must be ordered](../BoundsMustBeOrdered) is about. Handing the seam an already-filtered
source makes that order structural instead of remembered.

Expiry is not a lost write. On both legs the merge provably never ran, so the verdict is the
auto-retried `OwnerNotReady` / `OwnerDisposing` rather than the caller's 31 s `OwnerUnreachable` —
see [Reading a Write Verdict](../ReadingAWriteVerdict).

## A verdict also needs a route that is CHECKED

Totality is about producing a verdict. It is not finished until one has been *delivered*, and the
two ack gates produced theirs and then discarded it (#3196):

```csharp
if (Interlocked.Exchange(ref ackPosted, 1) != 0) return;   // gate claimed HERE
…
hub.Post(resp, o => o.ResponseFor(request));               // result DISCARDED
```

Claiming first is right — two racing legs must not both answer. But claiming is *also* what disables
the fallback: `RegisterOwnerDisposingNack`'s `tryClaimAck()` now returns false, so its
`ILatePatchVerdictSink` dispatch — the one route that still reaches an armed waiter with no message
routed at all — is skipped. And the post can be refused: with the owner past `DisposeHostedHubs` and
its parent past it too, `MessageService.PostImplGeneric` stamps `POST_REFUSED_SHUTTING_DOWN` and
returns a `Failed` delivery, its own comment noting that *"This site does NOT answer the sender
itself"*. The verdict was thrown away and the door shut behind it.

**Claim, then VERIFY.** The gate still latches first; the verdict now falls through to the sink when
the transport refuses it. The post stays first here, unlike in the disposal NACK — this runs on the
live path, where the caller's `Observe` callback is armed and the message *is* the designed seam.
Missing both routes is then a checked fact (nobody in this mesh is armed, and the transport is gone)
and is logged rather than swallowed, because the remaining case — a caller in another process — is
one the registry cannot see.

## Totality is not enough — the verdict's CODE decides whether the write survives

A terminal that reaches the writer can still be the wrong terminal, and the difference is not
cosmetic: `MeshNodeErrorCode` splits every NACK into two populations, and its own doc says so.

> `OwnerDisposing` and `OwnerNotReady` "are the only auto-retried NACK codes; every other code stays
> terminal."

So a NACK carrying `Unknown` is not a weaker version of `OwnerDisposing` — it is the opposite
instruction. `MeshNodeStreamExtensions` re-enqueues the two retryable codes against the fresh
activation (capped at `MaxOwnerDisposingReenqueues`, re-diffing on each attempt, so a merge that did
commit becomes a no-op); everything else it hands straight to the caller as a failed write.

### The case: a package root recycles mid-install (#3499)

Measured on 2026-09-06 across three independent runs — core `main-cd` **#7937** (`85ab1dfbe`), core
`main-cd` **#7941** (`b8accc2b2`) and the MeshWeaver.Plugins gate on **#1422** — on two different
packages (`Chess`, `Hosting`), with a run in between that sealed cleanly. All three ended the same
way: `GATE FAILED — install: <package>` with `[install] TimeoutException`, and `main-cd` could not
produce a sealed set for the release.

The subtree hubs of a package root are hosted hubs of that root. When the root recycles — the
NodeType rebind watcher, stale-build convergence, or the installer's own retyped-root recycle, all
of which are legitimate — every hub under it goes down with it. In ten of ten `VERDICT_TIMEOUT`
trails on the gate run, the patches those hubs were answering had **already committed**:

```
HANDLER_ENTER → PATCH_MERGE_DISPATCHED → HANDLER_EXIT Processed
→ PATCH_MERGE_TURN entered → PATCH_MERGE_STAMPED v=2 refused=0 → PATCH_ECHO_SEEN  ⇒ (nothing)
```

The merge landed; only the durability leg was still owed, and it faulted on a lifetime scope that
was closing. That fault reached `ClassifyPatchException` — the ONE funnel every owner-side patch
fault passes through — and fell out of its `_ =>` arm as `Unknown`. The writer therefore reported a
**lost write for a patch that had committed**, and the installer failed the package.

### 🚨 Count the base rate before naming a cause

The obvious story — *"a package root is disposed mid-install under a reconcile"* — is what the
symptom looks like, and it is **falsified by the run that sealed**. Counting the four bake jobs, all
with per-request tracing on (`handler-side fate` present in every one, so no count below is a
missing denominator):

| bake job | verdict | `[QUIESCE-TIMEOUT]` | `reconcile failed … disposed before the response arrived` | `VERDICT_TIMEOUT` |
|---|---|---:|---:|---:|
| core `main-cd` #7937 | FAILED `install: Chess` | 6 | 10 | **5** |
| core `main-cd` **#7938** | **SEALED** | **10** | **12** | **0** |
| core `main-cd` #7941 | FAILED `install: Hosting` | 5 | 8 | **0** |
| Plugins #1422 gate | FAILED `idempotence: Chess` | 3 | 7 | **10** |

**The root being recycled under an in-flight reconcile is at its MAXIMUM in the run that sealed.** It
is normal, it is answered (the requester gets a typed `HubDisposedBeforeResponseException`), and it
is not the cause. What separates a seal from a failure is the third column: a write that reached no
verdict at all.

Which also says the two failures are not one bug. #7937 and the gate run are this page's defect.
**#7941 is not**: it has zero unanswered write verdicts and instead carries two
`JsonSynchronizationStream … resubscribe failed` lines — zero in the sealed control — whose parked
stream leaves the *outer* `CreateOrUpdateNodeRequest` with no terminal at all, one layer above the
verdict watcher. That is its own defect, tracked separately, and no amount of correct classification
here would have sealed that run.

Fixing only the silence makes this *worse-looking, not better*: before the fault was caught at all
(#2543) the writer burned the full 31 s `WriteVerdictBound` and reported `OwnerUnreachable`; after,
it got a prompt `Unknown`. Same failed install, 31 seconds sooner.

### 2026-09-07 — `PATCH_FLUSH_FAULTED_SYNC` has never fired, and that refutes the arm it was built for

#3480 added the stage precisely so the next occurrence would name its exception. It has now had a
window, and the reading is a **negative with a denominator**.

**Population.** Every `Bake + publish NodeType assemblies to portal storage` job in core's
`Continuous Delivery (main)` workflow (`303778518`) from #3480's merge
(`8ebce31e8`, 2026-09-06T21:58:47Z) to 2026-09-07T13:31Z: **25 jobs, 18 success / 7 `GATE FAILED`**.
Every one of the 25 head commits verified `git merge-base --is-ancestor 8ebce31e8` — the code was in
the image, this is not a pin-staleness null.

**Sensitivity, checked before the zero was believed.** The stall shape is still occurring: **8 jobs
carry `OwnerUnreachable`** (32 · 15 · 9 · 6 · 3 · 3 · 1 · 1) and **5 carry `PATCH_ECHO_SEEN` stall
trails** (43 · 19 · 8 · 4 · 4). And the trail cannot silently drop a late stage —
`RequestFate.Add` keeps a HEAD plus a **sliding tail**, so *"the newest stages always survive"*.

| stage | occurrences across all 25 jobs |
|---|---:|
| `PATCH_ECHO_SEEN` | 78 |
| `PATCH_FLUSH_FAULTED_SYNC` | **0** |
| `PATCH_FLUSH_SUBSCRIBED` | **0** |
| `PATCH_FLUSH_BOUND_ARMED` / `_BOUND_FIRED` | **0** / **0** |
| `PATCH_ACK` | **0** |

**So arm 1's stated form is refuted.** The 2026-09-06 reading concluded *"the only code between
`PATCH_ECHO_SEEN` and `PATCH_FLUSH_SUBSCRIBED` is the watcher's own onNext; if either leg throws
synchronously the exception escapes"*. #3480 wraps exactly that code in `try`/`catch (Exception)` and
NACKs — and in five stall-carrying jobs it never fired, while the stalls kept their old shape. A
synchronous throw is not what is happening.

**What survives, and it is one stage away from being named.** `flush(committed)` neither returned nor
threw, i.e. it **blocked**. That call is:

```
GetService<IPostCommitFlush>()          ← DI resolve, on the echo thread
  → StoragePostCommitFlush.Flush(node)
      → GetService<IStorageAdapter>() / <IMeshChangeFeed> / <PostCommitFlushRegistry>
      → flushed.Claim(path, version)
      → storage.WriteAndPublishUpdated(node, …)   ← calls adapter.Write EAGERLY, at build time
```

Four DI resolutions and a registry claim run on the commit-echo thread before any `Subscribe`
happens, and `WriteAndPublishUpdated` is `adapter.Write(node, options).Do(…)` — the adapter chain is
CONSTRUCTED, not deferred, at that point. `PersistenceService.Write` is itself properly
`Observable.Defer`red, so the eager part is the composition and the service resolution, not the IO.

🚨 **The next discriminator is one stage, not another sweep:** a `PATCH_FLUSH_RESOLVED` recorded
between `GetService<IPostCommitFlush>()` returning and `.Flush(...)` being called. A trail ending at
`PATCH_ECHO_SEEN` then means the DI resolve parked; one ending at `PATCH_FLUSH_RESOLVED` means
`Flush`'s own body did. Both are answerable causes; today they are one indistinguishable silence.

🚨 **And the window does not yet include #3603** (the upsert's CREATE leg answering instead of
hanging — merged 2026-09-07T14:20:56Z, after the last bake in this population). The `[STALE-CALLBACK]
… CreateOrUpdateNodeRequest@portal/nodeops` half of #2543 is the shape #3603 addresses; **the first
bake on a commit descended from #3603 is the first that can measure it**, and it must be counted
separately from the `PATCH_ECHO_SEEN` half, which #3603 does not touch.

### 🚨 Arm 1 is closed by ORDER, not by another stage (#3510, arm 4 — CD 8078)

The discriminator above was never needed. CD 8078 (`4fedf4549`, bake job 101955088724) carried
fifteen of these trails against a sealed control on the same core commit (8076), and every one of
them opened on the same line:

```
[PackageInstaller] recycling root Chess now that its in-package NodeType has a loadable build …
```

`PackageInstaller` recycles a package's ROOT partition hub the moment its in-package NodeType has a
loadable build. A patch already committed at its per-node OWNER under that root then flushes durably
through a **routed** write to the partition being recycled — and the routed write has no live target,
so the work parks. Whether it parks in the DI resolve, in `AddressFor`'s
`GetHostedHub` → per-address creation `Lazy` (which blocks a second caller for that address), or in
`PersistenceService.TryWriteFrom`'s deferred prologue is a distinction with no difference: all three
run on the owner's echo thread, and the bound that would have answered was armed **after** them.
That is the defect — not which of the three it was.

So the fix is the ORDER, and it needs no new stage. `ArmPatchAckWatcher` now arms the flush bound as
the first thing the echo arm does — before `flush(committed)` is called, let alone subscribed:

```
PATCH_ECHO_SEEN → PATCH_FLUSH_BOUND_ARMED → (build, subscribe) → PATCH_FLUSH_SUBSCRIBED
```

The timer runs on the flush-bound scheduler (the pool), never on the echo thread, so a parked echo
thread cannot suppress it. From `PATCH_FLUSH_BOUND_ARMED` on, the verdict is owed by the flush **or**
by the bound's scheduler — never by nobody. What the bound says is unchanged and still #3112's rule:
the commit IS the verdict, so it acks `true` and lets the flush run on. `FLUSH_OUTLIVED_BOUND` now
also carries `subscribed=`, which separates the two cases the seven occurrences could not:
`subscribed=True` is a genuinely slow flush (storage behind), `subscribed=False` is this arm.

**Denominator, so the effect is readable on the next seal.** ~120 root recycles per bake gate run;
~30 writes missed the 5 s handoff in ~6 roots in BOTH the failed run and the sealed control, so the
race was always present and the seal reddened only when one stranded write happened to be the
idempotence re-install's own (`Chess/_Policy` on 8078). Those `ADVANCE_WITHOUT_HANDOFF` lines are the
5 s handoff warning and can persist; what must not recur is a `[UpdateQueue] FAILED …
OwnerUnreachable … within 31s` whose trail ends at `PATCH_ECHO_SEEN`.

**What this is not.** No bound moved — not the 5 s handoff, not the 10 s flush bound, not the 31 s
`WriteVerdictBound`; widening any of them changes nothing, because a parked write has no target. No
retry, no watchdog, no drain added to the recycle: the recycle is by design and a package install is
supposed to survive its own root recycling (see below). The only change is that a window in which
nobody owed the verdict no longer exists.


### The rule

> **A disposal fault is the OWNER going away, not a verdict about the write.**

The predicate was already written down, one layer over, in
`HubDisposingException.IsDisposedContainer`:

> *"a hub whose scope has been closed cannot serve anything, so a delivery that faults this way must
> be answered as RETRYABLE (the address reactivates) rather than as a result."*

`ClassifyPatchException` now consults it first, ahead of every type-shaped arm, and answers
`OwnerDisposing` with a message that says what the writer may do. The classification walks the
exception **graph** (`ExceptionChain`), not `InnerException` in a loop, so a teardown sitting at any
index of an aggregate still decides it — a chain walker would classify by arrival order, which is a
race.

Three shapes count, and they are the same fact in three vocabularies:

| shape | where it comes from |
|---|---|
| any `ObjectDisposedException` in the graph | a closed DI scope, a disposed subject, a disposed CTS |
| `HubDisposingException` / `HubDisposedBeforeResponseException` | the two typed teardown exceptions — both **derive** from `ObjectDisposedException`, so they are covered by the line above |
| `DeliveryFailureException` with `ErrorType.ShuttingDown` | the messaging layer's word for it (`MessageService.NackThroughParent`) |

**Why every `ObjectDisposedException`, not just the Autofac-scope shape.** Nothing on this path
disposes anything as a way of *refusing* a patch, so on this path the exception has exactly one
meaning. And the direction of a mistake is asymmetric — the same argument `MeshOperations.IsWriteDenial`
makes for its own default: a false "terminal" loses a write and fails an install, every time; a false
"retryable" costs an idempotent re-diff, capped at two.

### 🚨 The cheap side of that asymmetry is not fully bounded — state it honestly

The re-enqueue this classification feeds is `MeshNodeStreamExtensions`'s
`OwnerDisposing / OwnerNotReady / Conflict` arm, and **#3477** is a measured, open, unreproduced case
where that arm went **dark**: after

```
[UpdateRemote] LATE_NACK_REENQUEUE hub=cache/… target=TestData/late-nack-node attempt=1 code=OwnerDisposing
```

nothing landed and nothing logged for 45 s — no `LATE_ACK`, no second NACK, no error to the caller
(1/330, `LateNackReenqueueTest`, MeshWeaver.Plugins shard 3). Sending more faults down that arm makes
that class **more** likely, and it is silent when it happens.

The two variants are the same mechanism reached two ways: the **direct** arm when the caller's
`Observe` callback is still pending (inside `UpdateResponseWaitBound`, 2 s), and the **late** arm when
the response arrives after that *or* when the owner's own `Post` is refused and `RoutePatchVerdict`
falls through to `ILatePatchVerdictSink`. A closed-lifetime-scope fault takes the **late** half
preferentially — `HostedHubsCollection` closes a hosted hub's scope on `DisposalCompleted`, by which
point `PostImplGeneric` refuses that hub's post — and the late half is exactly where #3477 was
measured.

So the trade is *rarely and recoverably* against *always and fatally*, which is still the right way
round, and it is not "at most two re-diffs". **A first candidate seal after this change should be
read for #3477's signature** — a `LATE_NACK_REENQUEUE` with no terminal after it — because this
change makes it more reachable and nothing yet makes it loud. #3477's own ask is the instrumentation
that would: a `LATE_NACK_REENQUEUED requestId=<new>` stage linking the re-enqueued request's trail to
the original.

### What this does not do

It does not stop the recycle, and it should not: recycling a root whose NodeType changed is the fix
for [a hub bound to the wrong configuration](../NodeTypeCompilation), and a package install that
cannot survive one is the defect. It also widens no bound — not the 2 s quiesce budget, not the 31 s
`WriteVerdictBound`, not the install timeout. The retry it enables already existed and is already
capped; all that changed is that the writer is now told which of the two things happened.

### Sibling defect, still open

A hub that has logged `[QUIESCE-OK]` still **accepts new correlated requests** — the intake gate only
refuses from `DisposeHostedHubs` onward (`MessageService.ScheduleNotify`), so a request taken on in
that window can never be answered. Measured on the same three runs: of nine `[QUIESCE-TIMEOUT]`
trails, six show `RECEIVED runLevel=Quiescing` at the issuing hub, i.e. the callback was registered
*after* teardown began. That is a different bug from this one (here the request was accepted while
the hub was `Started`), it is not fixed by this page's rule, and it is benign in every occurrence
measured so far — the requester gets `HubDisposedBeforeResponseException`, which is a real answer.

## The UPSERT lane is the same rule, one lane over — and totality is now by CONSTRUCTION (#3510, #3674)

`CreateOrUpdateNodeRequest` is a **detached-reply** handler: it returns `Processed()` at once and
owes its `CreateOrUpdateNodeResponse` from chains that outlive the turn. Every one of those chains
is subject to the rule above, and for the same reason — the delivery trail records
`HANDLER_ENTER … → HANDLER_EXIT state=Processed`, so a leg that answers nobody leaves a trail that
looks completed and a caller that waits out its whole budget.

🚨 **"Covered" was asked of the wrong question, and the answer read as more than it was.** The
earlier version of this table judged each leg by *"does it survive the owner being disposed under
it?"* — a real question, pinned by a real test. But the leg that answers "yes" to that can still
answer NOBODY when its source **completes empty**, which is a different termination reached by a
different route. **Every leg now handles all three of Rx's terminations** — the read leg and the
inner create by their own explicit third arm, the other three by composing through
`DetachedReplyOutcome.Of`, which converts value / fault / empty into exactly one emission so the
subscriber cannot fail to answer:

| leg | trail stage | its source's empty completion |
|---|---|---|
| read — `gatedExisting` | `UPSERT_READ …` | posts a refusal naming the empty read (#2454). 🚨 Never `DefaultIfEmpty(null)`: the value arm reads `null` as "the node does not exist" and would turn a non-answer into a CREATE |
| no-op probe — `SkipNoOpIfAuthorized` | `UPSERT_READ existing → no-op probe` | falls through to the write path. `GetEffectivePermissions` terminating without emitting is a documented outcome — half of what `HubPermissionExtensions` classifies as `Undetermined`, *"because it FAULTED, or because it terminated without ever emitting (#2742)"* — and against a two-arm `Subscribe` it posted nothing at all |
| NodeType gate — `ApplyUpdateViaStream` | — | refuses, and says the probe produced no answer rather than "not registered" — a verdict and a non-verdict are not the same answer |
| update — `WriteThroughStream` | `UPSERT_WRITE_THROUGH_STREAM` | posts a refusal naming the unconfirmed write. Disposal is separately covered: `RegisterOwnerDisposingNack` mints `OwnerDisposing`, the writer re-enqueues against the fresh activation, the caller is answered `success=True` (`UpsertAnswersWhenTheOwnerGoesAwayTest`) |
| create — `DispatchInnerCreate` | `UPSERT_READ absent → create` | posts a refusal (`UPSERT_CREATE_COMPLETED_EMPTY`), and an `InnerCreateVerdictBound` bounds the no-answer case |

🚨 **None of this bounds a source that NEVER terminates.** `DetachedReplyOutcome.Of` is about the
three terminations Rx *delivers*; the fourth outcome — never emits, never completes, the section
"The FOURTH outcome" above — reaches no arm at all, and `Of` cannot rescue it, because every
operator it applies **reacts** to a termination and none **manufactures** one. The operator that
manufactures one is `Timeout`, and it is separately present where the source can starve:
`InnerCreateVerdictBound` on the inner create, `NodeOpForwardTimeout` inside the no-op probe.
Compose `Of` **around** that bound and both properties hold — the timeout supplies liveness, `Of`
turns whichever termination arrives into an answer. Totality and liveness are different properties;
this section is about the first.

### #3674: the no-op probe is the one that was actually silent, and it is a RE-INSTALL

[#3674](https://github.com/Systemorph/MeshWeaver/issues/3674) reported a
`CreateOrUpdateNodeRequest` that reached *"a hub but no handler"*, followed by
`ADVANCE_WITHOUT_HANDOFF … the owner never acknowledged this write` and a 31-second
`VERDICT_TIMEOUT` — a write neither applied nor refused. Two of its own readings were later
falsified in its thread: the `@` suffix in a request-fate entry names the hub that **recorded** the
stage, not the delivery's target, so two of the three "not mine" verdicts were correct forwards; and
the 8055-vs-8071 regression framing does not hold, because both commits carry the same code in the
respect that matters. What survives every re-reading is the **shape** — the caller of a sanctioned
lifecycle request received no terminal at all.

The instance of that shape reachable with real machinery is the **no-op probe**. A re-install writes
content identical to what is stored, which is exactly a no-op upsert, which is exactly the branch
that asks `GetEffectivePermissions(path, requestedBy)` whether the caller could have written anyway.
An empty completion there used to run neither arm: no `PostOk`, no write path, no log line.

🚨 **That the reported failures took this branch is a FIT, not a measurement.** CD 7950's
`[FAIL] Chess … idempotence: re-install failed` is an idempotent re-install, and a re-install is a
no-op upsert — but no capture names the probe, because a leg that logs nothing leaves nothing to
name it with. The claim this page makes is the narrow one: the branch was silent on an outcome its
own source is documented to produce, and it is not any more.

**Silence is handed to the write path, never resolved locally.** "No verdict" is not "denied" and
certainly not "granted"; it is not an answer this optimisation may act on, so the optimisation steps
aside and the owner stays the single authority on allow/deny — the same thing the probe's FAULT arm
has always done. Answering "skipped" on an unproven authority would be a success-**without**-write,
which is the one outcome worse than the silence.

`UpsertAnswersWhenThePermissionProbeIsSilentTest` pins it, and the lever is the framework's own
`WithPermissionEvaluator` seam narrowed to a single user id — not a mock: the create, the
access-control pipeline and the cache's own write gate (which probes for the CALLER's identity) all
run unchanged. Measured on this repo: without the fix the caller emits **nothing at all** for the
full 36 s bound; with it, answered in 0.7 s.

🚨 **And the reverted run reproduces #3674's reported artefact, not merely its symptom.** The
capture below is from this repository's own test host with the fix backed out — the same
`[STALE-CALLBACK] … CreateOrUpdateNodeRequest@portal/nodeops-…` line the issue quotes, and the same
interleaved `ROUTED onTarget=False state=Forwarded` entries that were read there as *"forwarded by
every hub, handled by none"*:

```text
[STALE-CALLBACK] client/…: 1 callback(s) pending > 30000ms:
  …=CreateOrUpdateNodeRequest@portal/nodeops-…(35000ms)
  AWAITING → POSTED target=portal/nodeops-… → RECEIVED@client/… → ENQUEUED@client/…
  → RECEIVED@mesh/… → ENQUEUED@mesh/… → ROUTED onTarget=False state=Forwarded@client/…
  → RECEIVED@portal/nodeops-… → ENQUEUED@portal/nodeops-…
  → ROUTED onTarget=False state=Forwarded@mesh/…
  → ROUTED onTarget=True state=Submitted@portal/nodeops-…
  → HANDLER_ENTER@portal/nodeops-…
  → UPSERT_READ existing → no-op probe@portal/nodeops-…
  → HANDLER_EXIT state=Processed@portal/nodeops-…
  ⇒ a handler was entered and no reply, completion or fault has been recorded since
```

**Read the two Forwarded lines against the two extra stages this trail carries.** The forwards are
correct — `@` names the hub that RECORDED the stage, and both of those hubs correctly declined a
delivery targeted at `portal/nodeops`. What names the defect is what follows on the target itself:
`ROUTED onTarget=True`, `HANDLER_ENTER`, the leg's own stage, `HANDLER_EXIT state=Processed` — and
then nothing. A trail that stops at a leg's stage is a leg that answered nobody, and it is
distinguishable from a pump that never turned only by whether `HANDLER_ENTER` is present. That
distinction is the reason each leg records a stage at all.

### The CREATE leg's history, and why closing it did not seal the bakes

🚨 **This gap was real, and it was NOT what failed the CD seals — measured 2026-09-07 on CD 7976.** The
one stale callback in that run's Hosting window took the **UPDATE** leg
(`UPSERT_READ existing → update` → `UPSERT_WRITE_THROUGH_STREAM`) and resolved inside 35 s, and the
eight-minute park that actually ran out the install's 600 s bound had **no pending callback anywhere**
— `[STALE-CALLBACK]`, which reports every callback older than 30 s every 5 s, fired zero times
throughout it. An unanswered `CreateOrUpdateNodeRequest` IS a pending callback, so the install was not
waiting on one. The park is inside `PackageInstaller`'s release wave, one step further on; see
[Bake Seal — NodeOps Saturation](../BakeSealNodeOpsSaturation) → "the park is INSIDE the release
wave". Close the create leg on its own merits, not as a fix for the seal.

Measured on core CD 7950: the create leg's trail ends at `HANDLER_EXIT state=Processed` and nothing
follows, and the run contains **zero `[CreateOrUpdate]` lines of any kind** — so neither terminal arm
ever ran. Silence, not a fault. The install then ran out its ten-minute bound with no name for what
happened.

### A create timeout cannot establish that nothing was written

The inner-create bound guarantees an answer to the upsert caller; it does not establish rollback.
The create handler persists the row before running post-creation handlers and sending its response.
A pending post-creation handler can therefore outlive that bound with the row already present.
The timeout reports an **unknown outcome**, never "NOT applied" or an unconditional safe retry.
`CreateTimeoutOutcomeTest` pins this with a real stored node and a held post-creation handler,
letting the production bound expire without altering its duration.

### 🚨 The recycle is by design, it succeeds, and it is NOT the defect

The disposer is `PackageInstaller.SettleRetypedRoot` — the installer's own ordered recycle of the
root it just retyped from its `Space` placeholder. It fires for every package whose root NodeType is
dynamic, which is **every plugin package** (`retypedRoot` is non-null iff `!rootTypeIsStatic`, and
`Store/Plugin` is dynamic — stated in `PackageInstaller.cs:92` and `PartitionTeardownCoverageGate`).

It is not a race, and it works: zero `root … never answered after its recycle` warnings, and the
same recycle appears 7–12× per bake in every run examined **including the one that SEALED**. A
package install is *supposed* to survive its own root recycling. What it cannot survive is work in
flight across that recycle never being answered.

Everything else was eliminated from code, not from a line's absence: stale-build convergence
(`Modules:AutoRecycleOnStaleBuild` is never set by the tester ⇒ the watcher offers, never disposes),
the overlay self-heal (needs a fault card), `RecycleNode` (one non-test caller — a circuit-only GUI
flow), and MeshWeaver.Plugins (posts no `DisposeRequest` outside tests and its MCP tool). The rebind
watcher is excluded by **ordering**: it fires on the root's *retype*, in the write stage, before the
release waves — while every observed disposal is *after* that package's own adoption.

Because ordering was the only discriminator, `SettleRetypedRoot` now logs its recycle at
Information. It previously logged only its *decline*, so the positive case was attributable to
nobody.

## What this is NOT

**It is not a timeout, a retry, or a watchdog.** No bound moves; nothing is re-attempted; no poller is
introduced. The change is that a source termination which previously reached no handler now reaches
one. If you find yourself widening `LateResponseWatchBound` or adding a re-subscribe to "recover" a
write that never answered, the question to ask first is which of the three terminations your
subscribe does not handle.

**It does not make a no-op write fail.** A lambda that returns the node unchanged, or whose diff is
empty, is a legitimate and *successful* write: it completes the caller with the current node and
writes nothing. That has always been handled, on both the own and the remote path — "nothing changed"
is an outcome, not a reason to withhold a verdict. The gap was narrower and stranger: a write with no
base at all.

## Diagnosing a suspected case

1. **Symptom**: N concurrent writes to one path, N−1 completions, no error anywhere, and a
   settle/drain signal that never fires. The process is otherwise healthy.
2. **Look for the missing pair.** In the `MeshWeaver.Mesh.MeshNodeStreamHandle` channel a write logs
   `[UpdateRemote] BEGIN` and then either `POST-PATCH`, `NO-OP`, or an error. A `BEGIN` with **none of
   the three** after it is a write that never got a base.
3. **Confirm the mirror.** `Workspace` logs `disposed superseded remote stream … no declared holder
   remains` when it reclaims one. A reclaim interleaved with a `BEGIN` for the same path is the
   shape above.
4. Since the guard landed, this surfaces as a normal `OnError` naming the path and saying the write
   did not land — so a caller can re-issue it, and `ActivityWriteTracker` / the per-path queue
   release as they should.

## Related

- [Silent Completion](../SilentCompletion) — the general shape this is instance 3 of: an observable
  that completes without emitting is invisible to `.Timeout`, to `Catch`, and to every `SelectMany`.
- [Data Access Patterns](../DataAccessPatterns) — the one mutation API and its read counterparts.
- [CQRS and Content Access](../CqrsAndContentAccess) — why a write's verdict is the owner's commit,
  never a bound expiring.
- [Asynchronous Calls](../AsynchronousCalls) — `.Subscribe(onNext, onError)` as the house shape, and
  what a cold observable owes its subscriber.
- [Bounds Must Be Ordered](../BoundsMustBeOrdered) — the sibling rule for nested deadlines: an inner
  bound must be able to fire first, because it is the only one that knows what starved.
