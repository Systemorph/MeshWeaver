---
Name: Release Failure Classes
Category: Architecture
Description: >-
  One log line carried four unrelated defects, so its issue could never be closed: every close
  against one cause was followed by a recurrence on another. What an incident's identity is actually
  computed from — including the two-line trap that hid the last cause entirely — and the rule that
  puts a failure CLASS in the template instead of a parameter — and the same defect one layer down,
  where the REMEDY's four failure channels collapsed into a bare null and the one the incident names
  wrote no log line at all.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 5h16"/><path d="M4 12h10"/><path d="M4 19h6"/><circle cx="18" cy="17" r="3"/></svg>
---

# Release Failure Classes

A NodeType release can fail for reasons that have **nothing to do with each other** — a permission
the caller does not hold, a node that does not exist, a store that cannot be reached, a host tearing
down mid-write. Until #1549 every one of them was reported through the same sentence:

```csharp
logger?.LogError("[Recompile] Release request for {Path} failed: {Message}", path, msg);
```

The template is constant and the whole reason lives inside `{Message}`. That one line is why an issue
opened in August 2026 could not be closed: it was closed on its converged cause, and reopened days
later on a different one, twice.

## What was measured

`Admin/_LogIncident/19be79b3588b8152` on the control instance, read 2026-09-19:
**326 occurrences**, `firstSeen` 2026-08-14, `lastSeen` 2026-09-17, across **16 pods**, `variants: 1`.
ONE incident — and its ten retained samples carry **four unrelated conditions**:

| sample | what it is | who owns it |
|---|---|---|
| `Update aborted: no initial state arrived for 'Hosting/InstanceAction' within 30s` | a base-state stall | the cause #1549 converged on, fixed by #1990 |
| `The release request for 'Crm/Contact' produced no answer within 180s` | a non-terminating leg | the ordered bound's own refusal (#3510's shape) |
| `MeshNode OwnerUnreachable at 'Store/Tier': … returned no verdict …` | a routing no-verdict | the routing/no-verdict family |
| `Cannot access a disposed object. Object name: 'MeshNodeStreamCache'.` | a host teardown | #1540's family |

A fifth, rotated out of the samples but quoted in the issue's own recurrence comments:
`No node found at 'rbuergi/OperationRequest'` — a release requested for a node that **does not
exist**, which is not a defect at all. The code reported it correctly and it still reopened a
production incident.

## How an incident's identity is actually computed

Three parts, and **not one of them is the template**
([Reading a Recurrence Reopen](../ReadingARecurrenceReopen) covers what a reopen does and does not
assert):

1. **WHERE** — the burst's top application frame if it has one; otherwise the log SITE (category +
   event id).
2. **WHAT** — the exception's simple type name. A call that passes no exception contributes nothing
   here.
3. **WHICH** — the discriminating *detail*: the exception's own message when there is one, else the
   **logged message**, with volatile parts masked (paths, quoted values, numbers, guids, timestamps,
   and any token the message itself spells out as a path segment).

Two consequences decide how a log line should be written, and the release line got both wrong.

🚨 **The words that discriminate must be in the message, not behind it.** A cause that differs only
*inside* a structured parameter has nothing of its own in part 3 once masking has run. Compare the
same category's OTHER site, `[Recompile] Deriving the recompile set failed.`, which passes its
exception: it produced **three separate incidents** — `OperationCanceledException`,
`UnanchoredQueryException`, `TimeoutException` — off one identical message, because part 2 split
them. The release line had neither an exception nor a varying template, so it split on nothing.

🚨 **Only the FIRST body line of a burst reaches the identity when there is no exception line.** That
is what hid the teardown cause completely: the console writes

```
fail: MeshWeaver.Graph.NodeTypeRecompileExtensions[0]
      [Recompile] Release request for Store/Core failed: Failed to start the release: Cannot access a disposed object.
      Object name: 'MeshNodeStreamCache'.
```

and `Object name: 'MeshNodeStreamCache'` — the only words that said *which* disposed object, i.e.
which defect — is on the second line. A diagnosis that arrives one line too late arrives nowhere.

## The rule

**The failure CLASS goes in the template. The subject and the reason stay structured parameters.**

`NodeTypeReleaseFailure` names fifteen classes, and
`NodeTypeRecompileExtensions.LogReleaseRefusal` has one literal template per class, each keeping
`{Path}` and `{Message}`:

```
[Recompile] Release request for {Path} failed — NO NODE EXISTS at that path: {Message}
[Recompile] Release request for {Path} failed — the HOST WAS TEARING DOWN under the in-flight write: {Message}
[Recompile] Release request for {Path} failed — NO INITIAL STATE for the node ever arrived, …: {Message}
```

Each is a distinct first body line, so each cause arrives as its own incident naming its own cause.

### Classify at the arm, never from the text

🚨 **The class is decided where the exception still is.** Recovering it downstream by matching
`{Message}` would re-derive, badly, what the producing arm already knew — and would mis-file silently
the day someone rewords a sentence. Every arm of `ObserveNodeTypeRelease` states its own class; the
trigger-write arm, the only one whose shape varies, asks `NodeTypeReleaseFailureClassifier`, which
reads:

- the **typed** `MeshNodeErrorCode` the owner put on the wire (`NotFound`, `AccessDenied`,
  `OwnerUnreachable`, `OwnerDisposing`/`OwnerNotReady`, `Conflict`/`Validation`/`Deserialization`) —
  the producer classified once, where the condition was known;
- then the shared [`AreaErrorClassifier`](../UserInterface) predicates the layout layer already uses for
  exactly these shapes (`IsHubDisposalRace`, `IsNodeGoneNotFound`, `IsAccessDenied`,
  `IsAvailabilityFailure`, `IsStorageUnavailable`, `IsTransientHubFailure`).

No second classifier is written: two rules for one question drift the first time either moves, and
the drift is invisible because both keep answering.

One ordering is load-bearing. `MeshNodeStreamHandle.IsBaseStateTimeout` is asked **before** the
transient rule, because the base-state terminal *is* a `TimeoutException`
([Reading a Base-State Timeout](../BaseStateTimeoutCensus)) — and `IsTransientHubFailure` matches every
`TimeoutException`. Asked the other way round, #1549's original cause would be indistinguishable from
any routing timeout. The predicate is typed and walks the inner chain, because both wrapper sites
carry the original as INNER precisely so the question stays answerable: the same outer sentence with a
*foreign* timeout inside is a different defect and gets a different class.

### The fallback is named

`Unclassified` is a class of its own, with a template that says nothing claimed the shape. A fallback
folded into the nearest-looking class is how a ticket comes to carry a cause it does not have — and
it puts the bucket straight back, just under a specific name. Traffic on the `UNCLASSIFIED` template
is a finding about the classifier, and it is expected to shrink to zero.

One more ordering is load-bearing, and for the same reason as the whole page. A bare
`ObjectDisposedException` says only *something was disposed*. It is a teardown race when the host is
actually tearing down, and a **genuine disposal defect** when the scope is alive — so the teardown
rule is gated on the existing probe (`AreaErrorClassifier.IsHubDisposalRace(ex, scopeDisposed)`,
`hub.IsServiceScopeDisposed`), never on the exception type alone. Claiming every disposed dependency
as `HostTearingDown` would fold real disposal bugs onto the teardown family's incident: the exact
mistake this taxonomy exists to end, reintroduced inside the fix for it. With a live scope such a
fault lands in `Unclassified` — loud, and on nobody else's ticket.

### Every terminal answers, and says why

A refusal with **no** cause is the same defect one step further along. The composed leg's closing
`DefaultIfEmpty(false)` answered the caller and told the refusal sinks nothing at all: an empty
permission terminal skips the arm that reports, so a release that did not happen produced no line
anywhere. That terminal now has its own class, `PermissionCheckNoVerdict`.

🚨 It is **not** reported as a denial. Defaulting the check to `false` *above* the decision would send
an unanswered check into the "you lack Compile" branch and render a no-verdict as an access denial —
which is what #974 forbids, because nothing was decided about the caller's rights. Three classes cover
the three honest outcomes: granted-and-refused (`CompileDenied`), the check **faulted**
(`PermissionCheckFailed`, with an exception to look at), and the check **ended** (this one, with none).

### An additive OVERLOAD, because the callers are not all in this repo

`Action<string>? onError` stays exactly as it was, and so does the **metadata signature** of the
methods that take it. The class rides a second, optional `Action<NodeTypeReleaseRefusal>? onRefused`,
which fires **alongside** `onError`.

🚨 **An overload, not an appended optional parameter.** Appending a parameter — even a defaulted one —
REPLACES a method's metadata signature: source-compatible, binary-**breaking**. A module assembly
compiled earlier holds a MethodRef to the five-parameter method, and after the platform advances that
call raises `MissingMethodException` at the point of use. It is the same break
`scripts/check-record-signatures.py` refuses for a record's primary constructor, for
the same reason, and no repo-local build can see it. So the five-parameter methods keep their metadata
and forward to six-parameter overloads that carry no defaults of their own (two all-optional overloads
would make every short call ambiguous).

`onError` has callers in this repository and in `MeshWeaver.Plugins` — its release wave, its
provisioning flow and six of its tests — and one of those is **in-mesh C#** that no compiler in either
repository ever sees ([NodeTypeCompilation](../NodeTypeCompilation)). A caller that only surfaces a
refusal to a user keeps using `onError`; a caller that LOGS one should take `onRefused`, so the class
reaches the template.

## The REMEDY's failure needs a class too (#5057)

Everything above is about a **refusal** carrying its cause. The same defect sits one layer down, on
the **repair**: `ReleasePostCondition` exists because a consumed release request can leave
`latestReleasePath` naming an earlier build, and its answer is to re-cut the release from the bytes
the compile just produced. That remedy can itself fail — and when it did, it reported the failure in
the one shape nobody can act on.

### What was measured

`Admin/_LogIncident/a98877ee6204cad1`, category `MeshWeaver.Graph.CompileWatcher`, **8 occurrences
over 3 minutes across two `memex` pods**, seven node types (`Store/Catalog`, `Store/Purchase`,
`Store/Provision`, `Store/Tier`, `Store/Publishing`, `Store/Subscription`, `Hosting/InstanceRequest`).
The line, in full:

> `[ReleasePostCondition] Hosting/InstanceRequest: a release request (requestedReleaseAt=…) was
> consumed and this compile succeeded, yet latestReleasePath still names '…' — cut for an EARLIER
> build (lastCompiledVersion 4650 → 4658) — AND the release could not be re-cut. The node advertises
> a build no release names; instances will keep binding '…' until a release is created for it.`

It names the consequence, the stale path, the build and the cost. It cannot name the cause, and the
cause is the only thing that decides what to do. **The reason was discarded one frame below**:
`NodeTypeBuildState.TryCreateReleaseNode` collapsed four unrelated failures into a bare
`IObservable<string?>` emitting `null` —

| channel | what it logged |
|---|---|
| no `IMeshService` on the hub | nothing |
| the create was **refused** (attribution, validation, a partition the requester may not write) | `Warning` + stack |
| the create **threw** while being composed | `Warning` + stack |
| the 10 s bound **expired** | **nothing at all** |

The last row is the one the incident names, and it was invisible by construction:
`Timeout(bound, Observable.Return<string?>(null))` **substitutes** the fallback sequence instead of
faulting, so the expiry never reached the `Catch` that logs. The only trace it left in production is
a gap between two adjacent lines — `Hosting/InstanceRequest` logged "Re-cutting…" at 22:16:14Z and
"…could not be re-cut" at **22:16:24Z**, exactly the bound. And the three rows that *did* log logged
at `Warning`, which the log watcher does not ingest, so the loudest line in the log is the one that
cannot say why.

This is the same rule as the top of this page, applied to a value rather than a template: **the words
that discriminate must travel with the failure, not behind it.** A `string?` can hold "landed" and
"did not land"; it cannot hold "did not land BECAUSE x", and it cannot distinguish either from "no
create was attempted".

### The fix: three states, and a bound that faults

`NodeTypeBuildState.ReleaseCreateOutcome` carries the path **or** the reason, with `Attempted`
separating a failure from a create nobody asked for. `Timeout(CreateBound)` now faults, routing the
expiry into the same `Catch` as every other failure, where it becomes a reason naming the bound it
waited out **and** that the create's fate is *unknown* rather than known not to have happened — the
difference between re-issuing it and going to look. Every sentence the post-condition emits — the
`Error` line and the compile `_Activity` diagnosis — names the cause of **both** attempts: the
settle's own create (previously a `Warning` reaching no operator-facing surface at all) and the
re-cut. `ReleaseCreateOutcome.Because` never returns the empty string: an attempted failure that
arrives with no reason SAYS the reason is missing, so the defect cannot reappear wearing terseness.

🚨 **The bound was not widened, and must not be.** A create that cannot land in ten seconds is not a
slow create, and raising the number is precisely the band-aid that would have made this unobservable
for longer. `CreateBound` is named so the refusal can quote it and so no test writes the number
again.

🚨 **The batch bake runs no post-condition at all**, and its stamp keeps the previous release path
when a create fails — the same silently-wrong state with no reporter above it. It now logs the reason
itself, because the bake is unwatched *by design*, which is exactly why the reason has to be in its
log rather than inferred from a release that never appeared.

### The control

`ReleaseRecutReportsWhyItFailedTest` drives the pure sentence composition
(`FirstAttemptClause`, `RestoredDiagnosis`, `FailedDiagnosis`, `Describe`) with no hub and no stream.
Measured: against the pre-fix wording restored on top of the fix, `TheFailedDiagnosis_NamesBothCauses`
and `ATimeout_NamesTheBoundItWaitedOut` both go **red** — the assertions can fail. The other side of
the control is `TheRestoredDiagnosis_ReadsAsARepair`: a re-cut that WORKED must read as a repair with
no failure wording in it, because a fix that made every outcome sound like a failure would satisfy
every other assertion here and be worse than the defect.

## What this does not claim

- **It does not establish WHY the re-cut's create does not land.** That is the point: the reason was
  unobservable, the filed issue's own first task was to make it reach the log, and inventing a cause
  from a ten-second gap would be a guess dressed as a finding. The candidates the evidence admits —
  a boot-time storm (the `Store/*` requests were consumed a day and a half after they were made), a
  cross-hub write refused mid-flight, an owner that never answered — are distinguishable only by the
  next occurrence, which is now diagnosable. Nothing here should be read as excluding any of them.
- **It does not fix any of the four causes.** It makes them arrive separately, each naming itself, so
  each can be owned, fixed and closed on its own evidence. The base-state one was fixed (#1990); the
  teardown one belongs to #1540's family; the routing no-verdict and the non-terminating leg keep
  their own homes.
- **It asserts nothing about a fingerprint.** The identity is computed in `MeshWeaver.Plugins`, which
  core cannot reference, so the control in `ReleaseFailureClassIsInTheTemplateTest` pins **distinct
  templates** — a guard that copied the identity rule into core would keep passing while its subject
  moved.
- **No log level moves.** Three of the fifteen classes are provably not defects
  (`NodeMissing`, `CompileDenied`, `OwnerRecycling`) and every class still logs at `Error`. Whether
  any of them deserves a lower level is a real question with a per-class cost argument, and it is a
  separate change — naming them is what makes it answerable at all. Until now there was one line to
  argue about and it covered everything.
- **The two other core callers are deliberately untouched.** The GUI's Create Release button and the
  package installer's release wave log their refusals at `Warning`, which the log watcher does not
  ingest, so neither is an incident source.

## Related

- [A Census That Counts Must Name](../ACensusThatCountsMustName) — the general form: a count without the
  identity of what it counted reads as clean
- [Reading a Base-State Timeout](../BaseStateTimeoutCensus) — the typed terminal one of these classes is
  decided by, and the census it carries
- [Reading a Recurrence Reopen](../ReadingARecurrenceReopen) — what a bot reopen asserts, and the two
  ways it fails
- [NodeType Compilation](../NodeTypeCompilation) — the release path these refusals come from
- [A Failure Report Answers Its Own Instruction](../AFailureReportAnswersItsOwnInstruction) — the
  general form of the #5057 half: a report that reads complete and withholds the deciding fact
