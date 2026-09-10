---
Name: The Phantom Base After Owner Disposal
Category: Architecture
Description: The owner echoes a merge to every mirror BEFORE it flushes it, so a teardown fault on the flush leg produces a "never applied" NACK whose re-attempt finds its own write already in the mirror — diffs to nothing, posts nothing, and reports success for a write that reached no store. Only the owner can tell that phantom from a merge that survived.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2a7 7 0 0 0-7 7v11l3-2 2 2 2-2 2 2 3-2V9a7 7 0 0 0-5-6.7"/><path d="M9 10h.01"/><path d="M15 10h.01"/></svg>
---

# The Phantom Base After Owner Disposal

**A mirror is not evidence that a write is durable.** This page is about the one place where the
write path treated it as if it were, and lost writes in silence for it — issue #3477.

It is the fourth member of a family this write path has closed one member at a time: #2661 (a bound
expiring is not a commit), #3001 (a base read that ends empty must not complete), #2543 (a bound
placed where it can never fire), #3550 / #3603 (a source that never terminates is not one that
completes). Each was a **fail-open**: a write reported as saved that was not. This is the last one
that decided from local state.

## The two facts that meet

### 1. On the owner, the echo precedes the durable flush

`MeshWeaver.Data/DataExtensions.cs` answers a `PatchDataRequest` in this order:

```
PATCH_MERGE_STAMPED v=N refused=0     ← the merge committed IN MEMORY
PATCH_ECHO_SEEN                       ← the change went out to every mirror
IPostCommitFlush.Flush(committed)     ← persist, THEN ack
```

That order is deliberate — readers see a commit as soon as it commits — and it means there is a real
window in which **every mirror of the node carries a value that no store holds**.

### 2. A teardown fault anywhere in that sequence is answered `OwnerDisposing`

`ClassifyPatchException` maps every owner-side teardown fault — a closed lifetime scope, a disposed
subject, a disposed CTS, `HubDisposingException` — to `MeshNodeErrorCode.OwnerDisposing`, which by
the enum's own contract means *"the patch never applied; safe to re-apply"*. That classification is
right, and #3499 deliberately widened it: the alternative (`Unknown`) is TERMINAL, and a false
terminal loses the write **every time**, whereas a false retryable was believed to cost only an
idempotent re-diff.

But the faults that widening routes here are, by construction, the ones raised **after**
`PATCH_ECHO_SEEN` — on the durability leg. So the population taking the `OwnerDisposing` re-enqueue
arm is precisely the population whose merge is already in the mirrors while storage is untouched.

## What the re-attempt then did

`MeshNodeStreamHandle.UpdateRemote`'s re-enqueue passes `refusedBaseVersion = 0` for
`OwnerDisposing` / `OwnerNotReady`, which reduces `RebaseSource` to `mirror.Take(1)`. Its comment
stated the intent exactly:

> the re-attempt re-runs the caller's `update` FUNCTION against whatever **the fresh activation
> serves** — pre-merge state, so the patch is recomputed and lands; or, if the commit did survive, an
> identical value, so the re-diff is an empty no-op. Both are the outcome the caller asked for.

The mirror is not the fresh activation. The re-attempt read **this hub's mirror**, found its own
value already there (the echo), computed an **empty** merge patch, posted **nothing**, and completed
the caller as a **success carrying the marker it had never persisted**.

Every log line on that path is `LogDebug`. Nothing is warned. Nothing is posted. The caller is told
the write is saved. Storage never changes.

## Why "the commit did survive" cannot be read off the mirror

The comment's disjunction has three cases, not two:

| what happened on the owner | mirror shows the write | storage holds it | empty re-diff is |
|---|---|---|---|
| merge never ran | no | no | — (a real diff is computed; the write lands) |
| merge committed **and flushed**, ack lost to teardown | yes | **yes** | **correct** — the write is durable |
| merge committed, **flush died with the hub** | yes | **no** | **a lost write reported as success** |

Rows 2 and 3 are indistinguishable from the mirror: both look like *"my value is already there"*.
**Only the owner can tell them apart**, because only the owner reads the store.

## The rule

> When the owner has just stated a write **never applied**, a mirror base that already **carries**
> that write is self-contradictory. It is not a no-op — it is an echo of state the owner does not
> have. Re-read the base from the owner, and diff against that.

`MeshNodeStreamHandle.ReattemptBaseSource` is that rule, at one seam:

- `ownerSaidNeverApplied` is set only by the `OwnerDisposing` / `OwnerNotReady` re-enqueues — the two
  codes whose contract is that no merge the owner kept ever happened. **`Conflict` is excluded**: it
  is the owner asserting it is provably PAST our base, so its mirror rebase (#1910) stays.
- The mirror base is used unchanged unless the caller's own lambda is a **no-op against it**. That is
  the ambiguity, and it is the only case that pays anything.
- In that case one authoritative `GetMeshNode` read is issued — routed to the OWNER, and its paced
  re-probe stands aside for a still-shutting-down activation, so what answers is the fresh one.
  Owner has the value ⇒ the no-op is real and the caller's success is earned. Owner does not ⇒ the
  diff is non-empty and the write lands.
- If that read cannot produce a base, the re-attempt **raises** — the `RequireBaseState` doctrine. It
  never resolves back onto the phantom it was called in to reject.

A first attempt, a `Conflict` re-attempt, and any "never applied" re-attempt whose mirror yields a
real diff are untouched: no extra read, no extra bound, no new failure mode.

## How this was established rather than guessed

The only sighting is `LateNackReenqueueTest.LateOwnerDisposingNack_AfterOptimisticEmit_ReenqueuesAndLands`,
1 of 330 on MeshWeaver.Plugins shard 3 (run `34059060596`). Its whole transcript after the NACK is:

```
[20:56:25.860] [Warning] … LATE_NACK_REENQUEUE … attempt=1 code=OwnerDisposing
[20:57:10.860] === TEST FAILED: The operation has timed out.
```

45.000 s of nothing, and the failing wait is the **durable-storage** poll, not the caller's verdict.

The cause follows by **elimination over the re-attempt's own terminals**, every one of which is
bounded below the 45 s the test waited:

| terminal | what it logs | inside 45 s? | storage |
|---|---|---|---|
| base-state wait expires (30 s) | `LATE_NACK_REENQUEUE failed` — Warning | yes | unchanged |
| base read ends empty | same Warning, at once | yes | unchanged |
| second NACK | `LATE_NACK_REENQUEUE` / `OWNER_NACK_REENQUEUE` — Warning | yes | unchanged |
| non-retryable NACK | `LATE_NACK_TERMINAL` — Warning | yes | unchanged |
| denial / delivery failure | `LATE_OWNER_DENIED` / `LATE_DELIVERY_FAILURE` — Warning | yes | unchanged |
| silence after the post | `VERDICT_TIMEOUT` (31 s) — Warning | yes | unchanged |
| early or late ACK | Debug | — | **written** |
| **empty diff — no post at all** | **Debug** | — | **unchanged** |

Exactly one terminal is both silent and leaves storage unchanged, and it is the one that requires the
mirror to already carry the marker. That is the reading; the code that produces it is above.

### 🚨 A correction to that elimination: the bounds compose ADDITIVELY

The table above reads every terminal's deadline from a **common origin** — the NACK — and concludes
that all of them fall inside the 45 s the test waited. They do not. The re-attempt's bounds are
**sequential**, and the same page of `LatePatchResponseRegistry` says so about its own window: *"The
owner-side paths were enumerated as ALTERNATIVES, taking their maximum; in
`ApplyMeshNodePatchInTurn` they compose ADDITIVELY."* The re-attempt is the same shape:

```
BaseStateWaitBound (30 s, from SUBSCRIBE)  →  post  →  WriteVerdictBound (31 s, from the POST)
                                              └─ plus GetMeshNode's 10 s when the base is a phantom
```

So `VERDICT_TIMEOUT` is due at *(base-read latency) + 31 s*, not at 31 s. A re-attempt whose base
arrives after 14 s produces its first Warning at 45 s or later — **outside the window the test was
watching**. The region from T+30 to T+61 is therefore silent BY DESIGN, and the failing observation
sat inside it.

That does not make the phantom reading wrong; it makes the sighting **under-determined**. It is
consistent with two things, and this page's change closes the first while the observation contract
described below closes the second:

1. the re-attempt settled silently against a phantom base (this page), or
2. the re-attempt had simply not reached any terminal yet, and the test stopped watching first.

## The residual: the write path has TWO no-write exits, and #3633 guarded ONE

`UpdateRemote` decides not to post a patch in two separate places:

| # | gate | where | logs |
|---|---|---|---|
| 1 | `IsRecordNoOp(current, updated)` | before anything is serialised | `NO-OP … contentType=…` (Debug; Warning only for a raw `JsonElement`) |
| 2 | `ComputeMergePatchDiff(...).Count == 0` | after serialisation | `NO-OP … diff empty after serialisation` (Debug) |

Both post nothing, both complete the caller as a **success**, both are Debug. They are one decision
wearing two coats — and `ReattemptBaseSource`'s phantom test was originally written at its call site
as **gate 1 alone**. A phantom base that fails record equality but serialises identically therefore
walked straight past the guard and out through gate 2, into precisely the silent loss the guard
exists to refuse.

**That pair is not contrived — it is the cache hub's ordinary shape.** Gate 2 exists because "a
rebuilt-but-identical content slips past the record-Equals check above", and `MeshNode` says which
cases those are in two places of its own:

- `MeshNode.ContentEquals` compares two `JsonElement`s structurally, but a **MIXED** pair — one
  `JsonElement`, one typed — is `false` by construction, and says so: *"no `JsonSerializerOptions` is
  available here to bridge representations"*.
- `MeshNode.SerializedEquals` enumerates the same witnesses: *"a rebuilt-but-identical typed content,
  a re-parsed `JsonElement` …, or a content record holding collections (compared by reference) all
  read as 'changed' while the persisted JSON is byte-identical"*.

And a mixed pair is what this comparison gets **whenever the content type resolves**. `UpdateQueued`
wraps the caller's lambda as `update(EnsureTypedContent(node, …))`, so the lambda's **output** carries
typed content while `current` — the raw mirror emission it is compared against — still carries the
`JsonElement`. Gate 1 therefore cannot fire, and gate 2 decides, which is precisely where the phantom
guard was not looking.

When the type does **not** resolve, `EnsureTypedContent` degrades back to a `JsonElement`, both sides
stay untyped, and `JsonElement.DeepEquals` settles it at gate 1. So gate 2 is the resolved-type case
— the ordinary one — not literally every write.

The fix is to ask the write path's own question once:

```csharp
internal static bool PostsNothing(MeshNode current, MeshNode updated, JsonSerializerOptions o)
    => IsRecordNoOp(current, updated)
       || ComputeMergePatchDiff(ToJsonObject(current, o), ToJsonObject(updated, o)).Count == 0;
```

`UpdateRemote` passes `PostsNothing` as `baseAlreadyCarriesTheWrite`, and spells both of its own
gates through the same helpers, so the guard and the decision cannot drift apart again. The extra
serialisation is paid **only where the guard runs at all** — a re-attempt whose owner said the write
never applied — never on a first attempt, a `Conflict` re-attempt, or a write that yields a real
diff.

## The second half: observing a write through its own contract

The sighting that opened #3477 could not name its own cause, and that was a property of the
**instrument**, not of the race. Three defects, all in the test:

1. **It waited on a proxy before the contract.** The ground truth was `storage.Read(path)` reaching
   the marker; the caller's terminal — the only wait with a bound and a diagnosis of its own — was
   checked *afterwards*. So when `UpdateRemote` was about to report `OwnerUnreachable` with the
   `corr=` request trail, the test had already failed with `System.TimeoutException : The operation
   has timed out.` and thrown the explanation away. This is #2819 restated: *anything waiting on a
   write must bound itself STRICTLY ABOVE the framework's bound, so the framework's terminal wins and
   names the cause.*
2. **The decisive bound was a hand-written `45.Seconds()`** — not CI-scaled while every other wait in
   the same test was `TestTimeouts.Convergence`, and below what one re-enqueue may legitimately cost
   before the framework gives up (see the additive arithmetic above).
3. **`[Fact(Timeout = 90_000)]` was BELOW the inner waits on a runner.** `TestTimeouts.Convergence`
   is 108 s at the CI factor, so on CI xunit killed the test before any inner wait could report.
   That is not hypothetical: core merge-queue run `34332482683` (2026-09-09, shard 4) failed
   `LateNackReenqueueCorrelationTest` as a bare *"Test execution timed out after 90000
   milliseconds"* — no assertion, no named wait — and #3477 has been unable to attribute that
   sighting since. `TestTimeouts` exists to prevent exactly this: *"the inner bound must be strictly
   less than the outer one, and that is why both live here."*

The corrected order asserts the caller's terminal first and durable storage second, which is also a
**strictly stronger** claim than the old poll made: on the owner the ack FOLLOWS the flush
(`PATCH_MERGE_STAMPED → PATCH_ECHO_SEEN → Flush → ack`), so once the caller holds a success the store
already holds the value. A phantom success — the write reported saved while no store holds it, which
is the class this test exists to refuse — now fails on the storage assertion and **says so**, instead
of hiding inside an anonymous timeout.

🚨 **`TestTimeoutLiteralRatchetGuard` does not see either literal.** It is deliberately narrow — it
counts `30.Seconds()`, `TimeSpan.FromSeconds(30)` and `Timeout = 30_000` — so a test carrying a
*different* guessed number is invisible to it. Both of this issue's sightings were that shape
(`45.Seconds()`, `Timeout = 90_000`), and there are ~700 `Timeout = <literal>` sites in core's test
tree, so widening the ratchet is a fleet-scale change rather than part of this fix. When you write
one, write the standard comment beside it: the constant must DOMINATE `TestTimeouts.TestMilliseconds`
(216 s at the CI factor), because an attribute argument cannot be a property.

## 🚨 Still open: `WriteVerdictBound` is not the bound it says it is

`LatePatchResponseRegistry.WriteVerdictBound` documents itself as *"the outer bound on a
caller-visible mesh WRITE: the instant `UpdateRemote` gives up and reports `OwnerUnreachable`"*, and
`TestTimeouts.Convergence` derives from it so that every waiter in the fleet dominates it by
construction. **For a write that re-enqueues, it is false.** Each attempt arms its own deadline from
its own post, `MaxOwnerDisposingReenqueues` is 2, and each attempt also pays a base read outside that
deadline — so the true worst case before a caller sees any terminal is

```
3 attempts x (BaseStateWaitBound 30 s + GetMeshNode 10 s + WriteVerdictBound 31 s) ≈ 213 s
```

against a published 31 s. Nothing in this change alters that; the two candidate fixes are a
maintainer decision, not an implementation detail:

- **Mint the deadline ONCE per write** (at attempt 0's entry) and thread the remaining budget into
  every re-attempt, exactly as `correlationId` is threaded. The published bound becomes true, and a
  NACK arriving late in the budget makes the re-attempt fail fast rather than land — a write that
  would have landed at 90 s is now refused at 31 s with a diagnosis.
- **Publish the composite bound** instead. Honest, but `TestTimeouts.Convergence` then becomes ~218 s
  locally and ~654 s on CI, which inflates every convergence wait in the fleet to buy a bound almost
  nothing needs.

The first is the better shape; it changes caller-visible behaviour on the slow path, so it is stated
here rather than smuggled in.

🚨 The corroboration is that `ClassifyPatchException`'s own remarks already predicted the shape
without naming the mechanism: *"Routing more faults here makes that class MORE likely, not less, and
it is silent when it happens."* The faults it routes are the post-echo ones.

## Reading the next occurrence

`PHANTOM_BASE` is the one line to grep, and it is a **Warning**:

```
[UpdateRemote] PHANTOM_BASE hub=… target=… attempt=1 corr=… — the owner NACKed this write as
NEVER APPLIED, yet this hub's mirror already carries it …
```

It fires only when a write was about to be lost, which is exactly when the information is worth its
cost. Paired with `corr=` (#3532) one grep yields the whole chain: the parent's NACK, the child's
registration, the phantom, and the authoritative re-read that resolved it.

## Verification

`ReattemptBaseIsAuthoritativeTest` pins the rule deterministically — both bases are seams, so no hub,
no cluster, no scheduler and no wall clock are involved:

- a phantom mirror + an owner that does **not** have the write ⇒ the owner's state is the base;
- a phantom mirror + an owner that **does** ⇒ the no-op stands (faulting here would report failure
  for a durable write);
- a mirror that is behind ⇒ the mirror is used and the authoritative read is **never subscribed**;
- an ordinary write ⇒ untouched, even when its lambda is a legitimate no-op;
- an authoritative read that cannot answer ⇒ raises, never falls back;
- 🚨 a phantom mirror holding the value as **untyped JSON** against a lambda producing it **typed**
  ⇒ still rebases on the owner. That case also asserts that the pre-#3477 predicate (`IsRecordNoOp`
  alone) does **not** see it, so it is a positive control on the gap rather than a restatement of the
  fix.

🚨 The cases call `MeshNodeStreamHandle.PostsNothing` — the predicate `UpdateRemote` itself passes —
rather than a local copy of it. An earlier version of this file spelled the record comparison out in
the test, so the production guard could be narrowed without a single case going red: a test asserting
a rule nothing under test was using.

🚨 It was falsified by planting the pre-fix behaviour (`=> mirrorBase`, unconditionally) and watching
it go red. It is deliberately **not** verified by re-running `LateNackReenqueueTest` hoping to catch
the 1/330 — the race stays unreproduced, and a test that can only pass by luck proves nothing.

## Correlation-test timeout is a separate observation

Static review of core `e29d540a038f55d6f41f3cc298d236db8bb60654` on 2026-09-09
identifies a test-contract defect in `LateNackReenqueueCorrelationTest`. This does not establish
the cause of every failure recorded under #3477, including the original durable-storage timeout
and the later 90-second method timeout. No new test runs were used for this finding.

The test parks an owner merge until either its release flag is set or `owner.IsShuttingDown`
becomes true. Disposing the owner therefore releases accepted work, which may finish with an ACK.
`LateNackReenqueueTest` explicitly documents that ACK as the normal graceful-shutdown outcome;
a retryable NACK is only a safety net. The correlation test nevertheless waits unconditionally
for `LATE_NACK_REENQUEUE`. A successful durable write can therefore leave this log wait unsatisfied.

Its timing fence also does not establish a late response. `LatePatchResponseRegistry.ArmedCount`
is the dictionary entry count. `UpdateRemote` registers that entry before posting through
`Hub.Observe` and starting `UpdateResponseWaitBound`. Seeing an entry proves neither expiration
of that bound nor owner acceptance of the patch. An early retryable NACK takes the separate
`OWNER_NACK_REENQUEUE` branch, whose warning also cannot satisfy the test's regex.

The logger lifetime does not supply a demonstrated alternative explanation. `UpdateRemote`
captures the cache workspace logger on entry and emits the late-NACK warning immediately before
the recursive retry. Owner shutdown closes the owner's child scope, not the cache/root scope.
The test's capturing provider has a no-op `Dispose`, and its sink accepts the emitted category.
Persistence alone cannot prove that the late-NACK branch ran or that a warning was lost.

### Deterministic correlation regression

The corrected test constructs a late verdict rather than racing for a shutdown NACK:

1. Capture the specific patch request ID and correlation ID, and fence on the unique path's
   response-timeout transition rather than the global registry count. That path has only the
   original attempt until the controlled NACK is supplied.
2. Dispatch an explicit late `OwnerDisposing` verdict through the existing registry seam.
   `ArmedRequestIds` documents this purpose: construct a late verdict for an actual in-flight patch.
3. Assert that the resulting child attempt retains the correlation ID, has a distinct request ID,
   and is actually armed in the registry. The previous test only checked that the warning's
   correlation value was nonblank and had no spaces.
4. Release the parked merge in cleanup and verify caller termination and durable persistence with
   an idempotent update. Keep graceful-disposal coverage accepting either valid terminal verdict.

Before parking the merge, a test-local Debug sentinel through the actual cache logger factory
proves that this capture sees the diagnostics. The regression requires no production log-level
changes or increased timing bounds. Synthetic dispatch does not cancel the original accepted patch;
both pending assignments are idempotent, and the gate is released before awaiting completion.
This exercises correlation propagation through the late callback, not natural shutdown verdict
generation. The corrected correlation test and its unchanged disposal sibling both pass locally
under the two-processor CI shape, with Release warnings-as-errors builds.
As a negative control, temporarily replacing the late retry's inherited correlation with a newly
minted one makes the corrected test fail at the parent/child equality assertion in two seconds;
the control is reverted and is not part of the change.
The queue-owned delayed conflict retry fixed by #3818 is a different path from `UpdateRemote`'s
direct recursive late-NACK retry; its passing regressions do not close #3477.

## Related

- [Write Verdict Totality](../WriteVerdictTotality) — the sibling fail-open on the base read
- [Silent Completion](../SilentCompletion) — why an empty completion is invisible to every timeout
- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — the general form: local replicas are
  not authoritative about what a store holds
