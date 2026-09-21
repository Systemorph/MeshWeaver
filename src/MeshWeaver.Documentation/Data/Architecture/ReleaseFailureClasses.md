---
Name: Release Failure Classes
Category: Architecture
Description: >-
  One log line carried four unrelated defects, so its issue could never be closed: every close
  against one cause was followed by a recurrence on another. What an incident's identity is actually
  computed from — including the two-line trap that hid the last cause entirely — and the rule that
  puts a failure CLASS in the template instead of a parameter.
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

## What this does not claim

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
