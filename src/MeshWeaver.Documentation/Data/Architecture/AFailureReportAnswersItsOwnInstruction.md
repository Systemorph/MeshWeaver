---
NodeType: Markdown
Name: "A Failure Report Answers Its Own Instruction"
Abstract: "Two live incidents in which a failure report told the reader to go and find a fact the reporting code already held: the deferred-delivery discard that said 'find why this hub disposed' without naming the teardown, and the commit-stage delete timeout that printed an outstanding-work field as '-' while the drain was stuck on the rest of the subtree. The rule, the two shapes it covers, and why a field that renders 'not measured' identically to 'none' is worse than no field."
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
> abstain, it asserts the opposite.**

Both halves were measured on live portals in September 2026, one day apart, in two subsystems that
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

`DisposalAttribution` renders three distinguishable answers, and the absence of a routed request is
itself one of them:

| what happened | what the report says |
|---|---|
| a routed `DisposeRequest` that stated a reason | `requested by <sender>; why: <the reason>` |
| a routed `DisposeRequest` with no reason | `requested by <sender>; why: reason not stated by the caller` |
| no routed request (host teardown, a `using`, an owner cascade) | `requested by a direct Dispose() (no routed DisposeRequest)` |

The third rules the message path out, which is exactly what the production incident needed and could
not get. A rendering that printed a blank there would have been the defect again in a new costume:
an absent answer that renders as nothing reads to the next person as *"there was nothing to
report"*.

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
