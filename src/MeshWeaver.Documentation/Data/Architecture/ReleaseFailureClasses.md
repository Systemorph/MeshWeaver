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

### 🚨 What the reportable expiry immediately revealed: the bound stops the WAIT, not the CREATE

Once the expiry had a name, one read settled what it means — and it is not what "timed out" reads as.
`Timeout` disposes this process's subscription to the `CreateNode` response. **The request is already
on the bus**, so the owning hub writes the node whether or not anyone is still listening. A release
id is `{yyyyMMddHHmmss}-{8 chars of SHA256(Collection/ContentPath)}`, so the id records when the
attempt STARTED and the node's `createdDate` records when it LANDED — the gap between them is
measurable from the store, with no instrumentation at all.

Measured on the control instance over `Hosting/InstanceRequest/Release/*`, 200 nodes (**a floor** —
the listing truncated at the limit):

| | |
|---|---|
| median id-mint → landed | **0.7 s** |
| beyond the 10 s bound | **8 of 200 (4%)** |
| slowest | **17.8 s** (`20260920222136-3V8XoerZ`, landed 2026-09-20T22:21:53Z — minutes after the incident burst) |

So a refusal reading *"the release could not be re-cut"* was, 4% of the time, emitted over a release
node **that exists**. The pointer was never advanced to it, and the type went on advertising a build
whose release was sitting right there.

🚨 **And the retry compounds it, because the id encodes the SECOND.** Two nodes in the same sample:

```
20260917173651-dU1GWMZG   landed 2026-09-17T17:37:06.550Z
20260917173701-dU1GWMZG   landed 2026-09-17T17:37:15.169Z
```

Identical content hash — so, by the id's own construction, identical bytes — with ids **exactly 10
seconds apart**: the bound. The first attempt's wait expired, the re-cut minted a *new* id for the
same build, and **both landed**. `AdoptOnOwnCollision` cannot rescue this: it adopts only a create
REFUSED for `NodeAlreadyExists`, which requires the same id, and #3407's reasoning explicitly rests
on the collision happening *in the same second*. A retry one bound later collides with nothing, so a
second node is created and nothing adopts either.

**The remedy this points at** — taken in the change after the diagnosability one, and described in
the next section — is that the re-cut reuses the abandoned attempt's release path rather than
minting a fresh one: the same id turns the late landing into a `NodeAlreadyExists` refusal, which the
adoption mechanism already resolves correctly, and the duplicate stops being minted. It is a
behavioural change to release-id minting with a genuine in-flight race to design against, so it was
kept apart from the diagnosability fix and given its own control. **Widening the bound is not the
alternative**: a tail that reaches 17.8 s would only move the same failure further out while making
it rarer and therefore harder to catch.

## The re-cut mints the SAME id, and a build with no release is STAMPED (#5057, second half)

### Why a fresh id could neither adopt nor be idempotent

Two things a re-cut has to be able to do, and a fresh id can do neither:

1. **Adopt the late landing.** The collision adoption (#3407) fires only on `NodeAlreadyExists`,
   which needs the *same* id. A re-cut minted one second later shares the hash suffix and nothing
   else, so the first node — which lands, 4% of the time, after the wait gave up — is never
   recognised as this compile's own release. The pointer stays on the previous build with the
   release sitting right there.
2. **Be idempotent with a create still in flight.** At a slow owner the first create is still
   queued when the re-cut arrives. At a fresh id that is a *second* create behind the first, at the
   same owner, under the same bound — which is why the re-cut typically expired too: the samples'
   "Re-cutting…" and "…could not be re-cut" lines sit exactly one bound apart. When both eventually
   landed, the store held two release nodes for identical bytes.

### The change

- `ReleaseCreateOutcome` carries `AttemptedPath` — the id an attempt was minting — on every
  attempted outcome, landed or failed. A failure before any id existed (the node could not be
  composed) carries none, and `NotAttempted` carries none.
- `TryCreateReleaseNode` takes a `reusePath`. `ReleasePostCondition.Restore` hands it the settle's
  own failed attempt's path, so the re-cut composes the Release node at **that** id. A late landing
  is then met as `NodeAlreadyExists` and adopted; a create that never landed is made at the id the
  stamped state already names. Either way, one node.
- 🚨 **A path is reused only for its own bytes.** `IsReusableAttempt` requires the id's hash half
  to equal `ContentHashOf(result)` — the durable content reference, `SHA256(Collection/ContentPath)`,
  which is what makes two attempts (or two replicas) for the same store version mint the same
  suffix. A path whose suffix names other bytes is refused with a `Warning` naming it, and a fresh
  id is minted. The split is positional (the fixed-width second stamp), because the hash itself may
  contain the dash the id uses.
- 🚨 **A build that ends with no release is STAMPED as such.** When the post-condition is violated
  and even the re-cut could not land, `ApplyCompileSuccess` writes `UnreleasedBuildPath` (the id the
  attempt was minting — the one place to look) and `UnreleasedBuildReason` onto the
  `NodeTypeDefinition`, beside the previous build's `LatestReleasePath` it has always kept. The batch
  bake stamps the same pair from its own attempt. Both clear the moment any release lands, and both
  are mesh-owned — masked by the sync seams like every other release pointer (the compile-state
  satellite that also mirrored them is [retired](../CompileStateSatelliteRetired); read the NodeType node). Before this the node **read healthy from every field** — `compilationStatus:
  Ok`, sources current, an assembly built, a release path present — and the only trace was an
  `Error` line at the moment of the settle.

What deliberately did **not** change: `RequestedReleaseAt` is still never cleared, so every later
compile of a type that once had a request consumed re-enters the post-condition. That is the correct
consequence, not a defect: a rebuild on a new framework produces bytes no existing release names,
and a released type owes a release per build. The re-cut now costs one node per build instead of
two, and nothing else about the trigger moved.

### What a roll's two-image window does

Measured on the control instance during a roll, with two replicas on two framework identities
(`s6f66941` and `sdc4cbaa`) both compiling: `get @MyAi/Panel` at v3420 read `lastCompiledVersion:
3418`, `latestAssemblyPath: MyAi_Panel/v3418-sdc4cbaa-…`, `requestedReleaseAt ==
lastReleaseRequestHandledAt` — and `latestReleasePath` naming a release node whose
`assemblyStoreVersion` was **3343**, artifact `v3343-s6f66941-…`, cut half an hour earlier by the
other replica. The shape this page is about, alive on an image that already carried the
diagnosability half. Each replica cuts a release for its own bytes and the shared record's pointer
is whichever landed last; the log line that would say which attempt failed and why was on a pod
whose log could not be read from outside the cluster without a write, so it is not quoted here.

### The control

`ReleaseRecutReusesTheAttemptedIdTest`, on a real monolith mesh. The FIRST attempt is driven through
the production seam `NodeTypeBuildState.Bounded` on a `HistoricalScheduler` over a **real**
`CreateNode` whose landing is delivered to the waiter only after the clock has expired the bound —
the wait gives up, the create lands anyway. The production remedy, `ReleasePostCondition.Restore`,
is then handed that outcome and must answer with the first attempt's path and leave exactly one
release node under `{type}/Release`. With the re-cut reverted to a fresh id (`reusePath: null`) the
same test is red on both assertions — a different path comes back and a second node exists. Beside
it: the hash guard on the real mesh (a foreign path is not reused and the refusal is logged), the
guard as a pure table (including a hash containing a dash), the outcome carrying its path through
the real bounded chain, and the stamp's three transitions (set / cleared by a landing / cleared by a
settle with nothing to say).

### What closes the incident

On the control instance, after a roll carrying this change, for a type the sweep re-bakes:
`get @<type>` shows a `latestReleasePath` whose release node's `assemblyStoreVersion` equals the
type's `lastCompiledVersion`, and `unreleasedBuildPath: null`. A `[ReleasePostCondition]` `Error`
line, if any, ends in a reason and is followed by `release restored at …` naming the **first**
attempt's id. The `{type}/Release` listing shows no new pair of ids ten seconds apart sharing a
suffix. A type that still carries `unreleasedBuildPath` is a finding with its reason written on it —
which is the state this section exists to make visible.

### The control

`ReleaseRecutReportsWhyItFailedTest` drives the pure sentence composition
(`FirstAttemptClause`, `RestoredDiagnosis`, `FailedDiagnosis`, `Describe`) with no hub and no stream.
Measured: against the pre-fix wording restored on top of the fix, `TheFailedDiagnosis_NamesBothCauses`
and `ATimeout_NamesTheBoundItWaitedOut` both go **red** — the assertions can fail. The other side of
the control is `TheRestoredDiagnosis_ReadsAsARepair`: a re-cut that WORKED must read as a repair with
no failure wording in it, because a fix that made every outcome sound like a failure would satisfy
every other assertion here and be worse than the defect.

## A release that lands after BOTH bounds is adopted when it lands (#5057, third half)

### What was still live after the same-id re-cut

The same-id re-cut adopts a late landing that arrives inside the re-cut's own bound. A landing slower
than both bounds still ended the settle with no release. The node was stamped `unreleasedBuildPath` =
the id both attempts were minting, and then the node landed at exactly that id. Measured on the
control instance on the image carrying the same-id re-cut: `Hosting/TriageItem/Release/20260923055416-Hd-IFSiA`
landed 21 s after its id was minted, which is past two bounds. #5474, folded into #5057, carried 69 more
lines of `could not be re-cut: the create did not land within 00:00:10` from 2026-09-22 to 2026-09-24,
over `Hosting/*` and `Publish/Deck`, on both portals. Nothing read the stamp again. The release existed,
the stamp named it, and the type kept binding the previous build's release until someone asked for a
new one.

### The change: the pointer follows the landing, not a clock

`LateReleaseAdoption` is installed on every NodeType hub beside the compile and release-request
watchers (`MeshDataSource`). While the hub's own record carries an `unreleasedBuildPath`, the hub
watches that one path through a synced `path:` query. The query is empty while the node is absent and
holds the node once it lands. It is never a point read of an absent path, which is the storm shape.
When the node is there, one owner write moves `latestReleasePath` to it and clears
`unreleasedBuildPath`, `unreleasedBuildReason` and the spent `releaseNotes`. The write re-checks that
the stamp still names that path, and that check is the whole guard:

- the stamp is written only for an id minted for this build's bytes (`IsReusableAttempt`), so a node at
  that path names these bytes;
- every later settle rewrites or clears the stamp, so a stamp that still names the path still describes
  the current build.

No bound is widened, and no timer or poll is added. A release that never lands leaves the stamp
standing, and the stamp is the report. The watch is re-derived from the record, so a landing that
happened while no activation was alive is adopted by the next activation, whose first listing already
holds the node.

### The control

`ALateReleaseIsAdoptedWhenItLandsTest`, on a real monolith mesh. A NodeType is seeded in exactly the
stamped state and its owner is activated. The release node is then created at the stamped path, and the
type's `latestReleasePath` must follow it with the stamp cleared. With the watcher's registration
removed from `MeshDataSource`, the same test is red at that wait (measured: it times out after 72 s with
the pointer still on the previous release). The negative half is a release at another path: it is not
adopted and the stamp stands. The pure decision is covered as a table.

### What closes the incident, amended

The read in *What closes the incident* above stands. One addition: a type that carried
`unreleasedBuildPath` for an id that has since landed must now read `unreleasedBuildPath: null`, with
`latestReleasePath` naming that id. A stamp that stays set over an id that does not exist is still the
finding the stamp exists to make visible. Why a release create takes more than 20 s during a boot
compile wave is **not** answered here. It is a slow owner, not a lost create, and it belongs to the
compile-lane load work.

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
