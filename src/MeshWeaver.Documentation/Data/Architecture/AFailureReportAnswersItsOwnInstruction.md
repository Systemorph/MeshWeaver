---
NodeType: Markdown
Name: "A Failure Report Answers Its Own Instruction"
Abstract: "Live incidents in which a failure report told the reader to go and find a fact the reporting code already held: the deferred-delivery discard that said 'find why this hub disposed' without naming the teardown, the commit-stage delete timeout that printed an outstanding-work field as '-' while the drain was stuck on the rest of the subtree, a report that re-derived a past fact from a structure that had since emptied, and a pooled-I/O attribution subscribed to a completion signal the failing case never publishes. The rule, the four shapes it covers, why a field that renders 'not measured' identically to 'none' is worse than no field, and why a report behind a hard-coded bound is a report nobody has ever read."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#b23c17'/><path d='M12 7v6' fill='none' stroke='white' stroke-width='2' stroke-linecap='round'/><circle cx='12' cy='17' r='1.3' fill='white'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Disposal"
  - "Diagnostics"
  - "Operations"
---

# A Failure Report Answers Its Own Instruction

> **The rule: if a report ends by telling the reader to go and find something, the reporting code
> must first check whether it is already holding it — and if a report carries a field for
> outstanding work, every producer of that report fills it, because an unfilled one does not
> abstain, it asserts the opposite. A fact about a PAST event is read from what was RECORDED then,
> never re-derived now from a structure that has moved since — and a report about a FAILURE is never
> subscribed to a signal that only SUCCESS publishes.**

All four shapes were measured on live portals in September 2026, within two weeks, in subsystems that
share no code. They are the same defect.

Related: [Reading a Disposal Stall Verdict](../DisposalStallVerdicts) — what a snapshot field actually
measures; [Bounds Must Be Ordered](../BoundsMustBeOrdered) — why the innermost, most specific answer
has to win the race; [Controls That Cannot Fail](../ControlsThatCannotFail) — one word covering several
states.

---

## Shape 1 — the report tells you to find what it already knows

`MessageService.Dispose()` discards deliveries a hub accepted and parked behind its initialization
gates, and files each one at Error (event **7301**). The line ended:

```text
[DISPOSE-DISCARD] Hub RiskTransfer is disposing with SubscribeRequest (id=…, from cache/…)
still deferred; initialization gates closed at deferral: [DataContextInit,MeshNodeInit] — the
message is NOT processed; the sender is answered ShuttingDown. RunLevel=ShutDown. Accepted work
must be drained before a hub goes down; find why this hub disposed before its deferred work
could run.
```

*Find why this hub disposed.* The hub knows. `MessageHub` records `disposeRequestedBy`,
`disposeReason` and `cascadeOwner` one frame earlier, in `HandleDispose` — added precisely so
[#3510](https://github.com/Systemorph/MeshWeaver/issues/3510)'s
*"`[QUIESCE-START]` on a root should name who asked"* could be satisfied. But `[QUIESCE-START]` is
`Information`, and the red-log pipeline files `Error`s. **So the one line that becomes an issue was
the one line without the attribution.**

Measured on `Admin/_LogIncident/d2249f800ffc2577`: **364 occurrences, 2026-09-08 → 2026-09-14,
13 pods**. Every captured sample names the message, its sender and the gates it sat behind. Not one
says which teardown threw it away — so the reader cannot tell an operator recycle from a NodeType
rebind from an owner's cascade, which are three different investigations.

The fix is not a new measurement. It is passing a value that already exists across ten lines of the
same method:

```text
… RunLevel=ShutDown. This teardown was requested by a cascade from its owner mesh; why: the
owner's own teardown — … . Accepted work must be drained before a hub goes down — that
attribution is who to ask why this hub went down with work still parked behind its gates.
```

### The second reader is the one who cannot see the log at all

The stranded sender is in another process as often as not. It gets a NACK, and that NACK said only
that the hub had gone away. The same clause therefore goes into the NACK text — the same reasoning
that removed the generic disposal sentence from the sibling path in
[Retiring an Activation](../RetiringAnActivation).

### Attribution must VARY, or it is decoration

`DisposalAttribution` renders four distinguishable answers, and the absence of a routed request is
itself one of them:

| what happened | what the report says |
|---|---|
| a routed `DisposeRequest` that stated a reason | `requested by <sender>; why: <the reason>` |
| a routed `DisposeRequest` posted by the hub to ITSELF (a rebind, a self-heal) | `requested by itself — a self-posted DisposeRequest (<address>), i.e. a rebind or self-heal recycle; why: …` |
| a routed `DisposeRequest` with no reason, or a blank one | `requested by <sender>; why: reason not stated by the caller` |
| **an owner's cascade** — this hub is going down because its owner is | `requested by a cascade from its owner <owner>; why: the owner's own teardown — <the ORIGINATING cause, propagated unchanged down the chain>` |
| no routed request and no cascade (host teardown, a `using`) | `requested by a direct Dispose() (no routed DisposeRequest)` |

Three of those distinctions are load-bearing and none of them can be inferred from the others:

- **The cascade form is not the direct-dispose form.** A cascaded child IS disposed by a direct
  `Dispose()` call from `HostedHubsCollection`, so before `cascadeOwner` existed every child in a
  wave reported `a direct Dispose()` — indistinguishable from host teardown, and reading the child
  told you nothing about the root. That was #3510's wedge one level down. The cascade carries the
  **originating** cause, not merely the immediate parent, so a leaf names the event that started it
  however deep the tree is.
- **The last row rules the message path out**, which is exactly what the production incident needed
  and could not get.
- **A blank reason is an unstated one.** `Reason` is free text from the poster; `null` was once the
  only value treated as "not stated", so an empty string rendered a literal `why: ` with nothing
  after it — the defect again in a new costume. It is normalised at the single capture point, so
  every reader inherits it rather than each having to remember.

### One cause, claimed once

Two writers can record the cause — the `DisposeRequest` handler on the action block, and the owning
collection's cascade note on whatever thread is disposing the owner — so "first cause wins" has to
be a **claim**, not a pair of reads. It is one `Interlocked.CompareExchange`, and the handler
additionally declines to claim at all when the teardown has already begun: `Dispose()` sets its
shutting-down flag and only then posts its first `ShutdownRequest`, so a routed request arriving in
that window still sees `RunLevel=Started`, is admitted, and its turn runs *after* the teardown
started. Without the check it would overwrite a true `a direct Dispose()` reading with its own.

**A report that names the WRONG cause is worse than one that names none** — it is this same defect,
pointing somewhere else.

**Corollary, and it is the reason this section names a poster:** the rendering is only as good as
what posters supply. `MeshOperations.Recycle` — the operations / MCP `recycle` verb, the one
teardown that comes from OUTSIDE the framework's own lifecycle and therefore the one whose cause a
log cannot reconstruct — was the single production poster of a `DisposeRequest` with no `Reason`. It
now states one.

---

## Shape 2 — a field that renders "not measured" the same as "none"

The recursive delete reports every stage failure through one line:

```text
[DeleteNode] timeout path=… stage=<stage> partial-deleted=<n> unanswered=<set>
```

`unanswered=` exists so an operator learns WHICH node stopped the operation. The pre-flight stage
fills it, and its occurrences read like this:

```text
stage=pre-validate-descendants partial-deleted=0
unanswered=sglauser/AgenticBusiness/01-MeetYourCoworker/AskAdvisor, …
```

The commit stage never set it. So a commit-stage timeout rendered:

```text
[DeleteNode] timeout path=Hosting/TriageStatus stage=commit partial-deleted=3 unanswered=-
System.TimeoutException: [DeleteNode:commit] the bottom-up delete of 'Hosting/TriageStatus' made
no progress for 30s — 3 path(s) removed from storage so far
```

(memex-cloud, 2026-09-14T08:45:38Z.)

`-` is the same rendering the line uses for *"there is nothing outstanding"*. The field did not
abstain — **it asserted the opposite of the truth**: three paths were removed, the drain was stuck
on the rest of the subtree, and the report said it owed nothing. And the sentence beside it offers
only a count, which identifies what SUCCEEDED and therefore identifies nothing about the failure.

Both halves of the answer are live in that closure: the plan (`collected.ToDelete`) and the progress
(`SnapshotProgress()`). The difference is the answer, and it costs one set operation on a path that
has already failed:

```text
[DeleteNode:commit] the bottom-up delete of 'Hosting/TriageStatus' made no progress for 30s —
3 of 9 planned path(s) removed from storage so far; still owed by the plan:
Hosting/TriageStatus, Hosting/TriageStatus/…
```

Two details that are not incidental:

- **"still owed by the PLAN", not "remaining".** A drain can remove MORE paths than it planned — a
  child created between the plan snapshot and the drain — which is the whole subject of
  [#3392](https://github.com/Systemorph/MeshWeaver/issues/3392). The set is what the plan still
  owes; it is never a claim about what else storage may hold.
- **The count stays.** Reporting real progress rather than `0` is the half of
  [#1198](https://github.com/Systemorph/MeshWeaver/issues/1198) that landed earlier. Names are
  ADDED; a rewrite that swapped one fact for another would read as a fix and be a regression.

---

## Shape 3 — a report that re-derives a fact from a structure that has since moved

This is the shape the `[]` in the very first log excerpt on this page came from, and it is worth
separating because the other two are about a fact that was never *reached*, while this one is about
a fact that was reached and then **overwritten by the passage of time**.

A hub holds its closed initialization gates in one dictionary, and `OpenGate` **removes** a gate
from it. So `gates` does not record what held a delivery; it answers *"which gates are shut right
now"*. Every report about a PARKED delivery that read that dictionary at report time was therefore
describing the hub at report time and not the delivery at park time — and the two disagree in
exactly the case a reader most needs the answer:

| when the report is written | what the live read says | what it means to the reader |
|---|---|---|
| during `Dispose()`, which opens every gate first to release the buffers | `[]` | *nothing was holding it* — the opposite of the truth |
| after the gate opened but before the restored turn ran | `[]` | the same, with no teardown anywhere near it |
| after ONE of two gates opened | the other one | the gate that held it for most of its wait is never named |

The empty case is the damaging one, because an empty list does not read as "not measured". It reads
as a measurement: **nothing was holding it.** That is what 364 production Errors said, for six days
across 13 pods, while alleging in the same sentence that the delivery was *"still deferred behind
its initialization gates"*.

The fix is a field, not a measurement: the deferral tracker records the gate set at the moment it
parks the delivery, under the same lock that made the deferral decision, and every report reads it
from there:

```csharp
// Called under gateStateLock at the deferral decision. Teardown opens the gates before
// draining these trackers, so reading gates.Keys during Dispose loses the cause (#3712).
var gatesAtDeferral = string.Join(",", gates.Keys.OrderBy(x => x, StringComparer.Ordinal));
```

### Two facts, separately labelled — not one replacing the other

The live read is not wrong; it is a **different fact**, and the pair is the diagnosis:

- *gates still shut* ⇒ the gate is stuck. Look at the dependency that never initialised.
- *all of them since opened* ⇒ the hub **did** initialise and the delivery's turn still never ran.
  Look at what is holding the turn loop. That is a different investigation, and before this change
  it was indistinguishable from the first — it rendered as the same empty list.

So the recorded set is the subject of the sentence and the live read is stated beside it, each
labelled as what it is. Where a hub-level line already exists (the startup-timeout Error names the
hub's own still-shut gates) it stays exactly as it was, and the per-delivery answer is ADDED — a
rewrite that swapped one fact for the other would read as a fix.

### Why the last of the three readers went unfixed for a week

The discard at disposal was fixed the day after it was filed. The other two readers of the same
drain — the startup-timeout answer and the 30-second per-message deferral timeout — kept the live
read, and the deferral one could not be reached by any test at all, because its budget was a
hard-coded `static readonly TimeSpan`. **A path no test can reach is a path whose wording nobody
checks**, which is why that bound is now per-hub configuration (`WithDeferralTimeout`) with the
default untouched, and why a regression now parks a hub's turn loop across the gate open on
purpose:

```text
Hub late-gate/1 deferred GatedRequest (id=…) for >4s; initialization gates closed at deferral:
[gate-the-test-opens] — every gate it was parked behind has SINCE OPENED, so this hub did
initialise and the delivery's turn still never ran — look at what is holding the turn loop,
not at the gates.
```

Read that report while the loop is still parked — it is logged one line before it is posted, which
is the only way to see it, since the `DeliveryFailure` it produces cannot be routed until the loop
is released. That asymmetry is itself worth knowing: **a hub can tell the log something it cannot
yet tell the caller.**

---

## Shape 4 — the attribution rides the SUCCESS signal, so the failure case names nothing

The first three shapes are about a fact the report could have read and did not. This one is
structural, and it is the hardest of the four to see in review, because the attribution code is
**there**, it is correct, it is tested, and it is wired to a signal the failing case never publishes.

At silo shutdown the pooled-I/O registry is asked to cancel every leaf and report what did not
unwind. Its per-pool attribution — added for
[#2480](https://github.com/Systemorph/MeshWeaver/issues/2480) precisely because *"the teardown report
does not name the offending pool or leaf"* — is subscribed to each pool's own completion:

```csharp
foreach (var kvp in pools)
    kvp.Value.Disposed.Subscribe(residual => { if (residual != 0) LogWarning(…, kvp.Key, residual); });
```

That is sound for a pool which unwinds LATE — it reports even after the caller's own bounded wait
has given up. But a pool holding a leaf that ignores its token **never completes `Disposed` at
all**; the pool's own remarks say so in as many words:

> *If a leaf never unwinds, `Disposed` simply never fires and the caller's bounded wait surfaces that
> as the timeout it is.*

So the one path the fault ever takes is the one path with no attribution. The registry also clears
its pool dictionary on the way in — correctly, so a pool handed out after disposal can never be
issued work nobody will join — which removed the caller's last way to enumerate what it was waiting
for. The result, measured across three weeks and 18 production occurrences on 16 pods:

```text
IoPoolSiloTeardown: pooled I/O did not finish within 00:00:30 — the silo is releasing over live
work. A leaf ignored its cancellation token; fix the leaf, do not widen the budget.
```

Every occurrence identical. No pool, no site, no stack — an instruction (*fix the leaf*) addressed
to a reader who is given no leaf, guarding a failure mode whose next step is a native
use-after-unload crash.

### The tell: which signal is the report attached to?

This is the report-shaped form of the ordering rule that
[#4466](https://github.com/Systemorph/MeshWeaver/issues/4466) states for joins — *the signal a joiner
waits on is the LAST thing the fact it asserts publishes*. Turned around for a report it reads:

> **A report about a failure must not be subscribed to a signal that only success publishes.** If
> the only way the line gets written is the path where nothing went wrong, the line does not exist.

The fix reads the pools the join was waiting on **directly**, at the moment the budget expires,
which needs no signal from them at all — `UnreportedResiduals()` subtracts the pools that did report
from the set the disposal captured, and each remaining one names its in-flight count and its leaf
call sites. The residual then travels as the return value of a plain read rather than as an event:

```text
… do not widen the budget. Did NOT report: Query=1 [MeshQuery+<>c__DisplayClass22_0.<MergeProviderObservables>b__1].
```

### The sweep: three of the four teardown budgets already did it right

This is worth stating because it makes the defect a deviation rather than a design gap. Four places
hold a bounded wait at teardown and report its expiry, and the other three all read their state
**directly at the moment the bound expires**:

| where | what its expiry report reads |
|---|---|
| `MeshTeardownHostedService` | `mesh.GetDisposalDiagnostics()` — the recursive snapshot, taken there |
| `MeshTeardownExtensions.TeardownAsync` | the same snapshot, onto the `TimeoutException` it throws |
| `RoutingQuiescenceSiloParticipant` | `quiescence.InFlightSample()` — the stuck legs, with target and delivery id |
| `IoPoolSiloTeardown` | **nothing** — it waited for the pools to tell it, and they never did |

`RoutingQuiescence` is the closest sibling and the direct precedent: same 30 s budget, same silo-stop
phase, and [#2833](https://github.com/Systemorph/MeshWeaver/issues/2833) filed *the same
diagnosability ask on the same day* as #2480 — *"logging the target/sender/delivery id would turn the
next occurrence into a direct pointer — the same ask #2480 made for the pool name."* It was answered
by reading the registry at expiry, and production shows the difference plainly: bare lines until
2026-09-02, and from then on

```text
… do not widen the budget. Stuck leg(s): dispatch → Ops/Status/partnerre (delivery nxnf1DdwRU6mG0t4iI9bwA) | …
```

Same ask, same week, two subsystems; the one that read its own state at expiry became actionable and
the one that subscribed to a completion stayed blind for another seventeen days. **The pattern to copy
is the sibling's, and the question to ask at review is simply: at the moment the bound expires, what
does this line READ?**

### The empty case is a different finding, and must not read as "clean"

If the budget expired and yet **every** pool reported, no leaf is holding anything — so the
aggregate join itself failed to complete, which is a defect in the registry's own combinator and not
in any leaf. That has to be spelled out, or it renders as the absence of a finding and sends the
reader to look for a leaf that is not there. Same trap as Shape 2, one level up.

### Why nobody caught it for three weeks — and why the budget is now settable

The issue was closed as fixed on the strength of reading the attribution code, and reopened by the
next occurrence. The reading was right about the code and wrong about which path reaches it, and no
test could have settled the argument either way: the join budget was a hard-coded `static readonly`
30 s, so the expiry report was **unreachable from any test** — at 30 s it cannot even be observed
under `test/xunit.runner.json`'s 30 s `methodTimeout`. It is now `IoPoolOptions.SiloJoinBudget`,
default unchanged, settable only so a test can let it expire; the regression pin parks a leaf that
genuinely ignores its token and asserts the pool name and the leaf site are both in the line.

That is checklist item 7 collecting its second victim in the same subsystem, and it is the reason to
treat it as a rule rather than a nicety: **a path no test can reach is a path whose wording nobody
checks.** Widening the budget would have been the forbidden move — the budget is not the defect, and
the report is what makes the defect findable at all.

---

## What this rule is not

It is **not** a licence to downgrade or silence a report. Both changes here leave every level,
every event id and every existing fact exactly where they were; they add the fact the reader was
told to go and find. The two nearby changes that DID reclassify a line
([#4178](https://github.com/Systemorph/MeshWeaver/issues/4178), a hub discarding its OWN deferred
delivery;
[#3647](https://github.com/Systemorph/MeshWeaver/issues/3647), a queued turn the pump is about to
take) each moved a level only after establishing, by measurement, that the line asserted stranded
work with no waiter — and each kept the line, at Debug, naming the same facts. A classification that
stops reporting is a silenced fault, not a classification.

---

## Checklist when you write or review a failure report

1. Does it end with an instruction (*find why…*, *check which…*)? If so, is the answer reachable
   from where the line is written? If it is, put it in the line.
2. Does it carry a field for outstanding / unanswered / pending work? Then **every** producer of
   that line fills it, and "unset" must not render as "empty".
3. Does the report identify what FAILED, or only count what SUCCEEDED?
4. Does the value VARY with the condition? Write the negative case down — the absence of a cause is
   itself an answer and has to be spelled as one.
5. Does the party who cannot read this log — a caller in another process — get the same fact in its
   NACK or response?
6. Is any fact in it about something that happened EARLIER? Then it is read from what was recorded
   then, not re-derived now — and ask what the re-derived value renders as when the structure has
   emptied, because an empty collection reads as a measurement, never as an abstention.
7. Can a test reach this line at all? A report behind a hard-coded bound is a report whose wording
   nobody ever checks — three readers shared one drain here, and the two nobody could reach are the
   two that stayed wrong.
8. **Which signal is the report subscribed to?** If the attribution is published by a completion, an
   `OnNext`, a `Disposed` or any other event that only the SUCCESS path raises, then the failure case
   writes nothing — and that is the only case the report exists for. Read the state directly at the
   moment the bound expires instead. And say what the EMPTY reading means, because "the budget
   expired and nothing is outstanding" is a different, sharper finding than "no finding".
