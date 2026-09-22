---
Name: A Departed Silo Is Not A Delivery Defect
Category: Architecture
Description: >-
  Two production incidents — 191 occurrences on the router's own verdict, 3,959 on Orleans' addressing
  log — are ONE root: a message addressed to a pod incarnation that is gone. Which predicate sees
  which of the four rejection shapes, why the cure is the classifier and not a retry, and why the
  occurrence counts of the two incidents are not comparable numbers.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12h-6l-2 3h-4l-2-3H2"/><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/><path d="m9 9 6 6"/><path d="m15 9-6 6"/></svg>
---

# A Departed Silo Is Not A Delivery Defect

**Every rolling deploy addresses some messages to a pod that has gone. The framework has always
retried those and re-resolved the target on each attempt. What decided whether the *sender* survived
the window was the verdict after the retries — and for the two ways Orleans actually reports a
departed pod, that verdict was `Failed`, terminally.**

The consequence is not a lost message. It is a lost **subscription**: `SynchronizationStream`'s
resubscribe latch and `MeshNodeStreamCache`'s transient-owner rule ride out `ErrorType.ShuttingDown`
and tear down on `ErrorType.Failed`. So one ordinary roll permanently killed live views, mirrors and
sync streams whose target was answering again, on the surviving pod, seconds later.

## Two incidents, one root — and the discriminator is WHICH LOGGER fingerprinted it

[#2299](https://github.com/Systemorph/MeshWeaver/issues/2299) and
[#2307](https://github.com/Systemorph/MeshWeaver/issues/2307) were filed a few hours apart, read as
two defects for four weeks, and are one condition seen from the two ends of the same call:

| | #2299 | #2307 |
|---|---|---|
| fingerprinted on | `MeshWeaver.Hosting.Orleans.RoutingGrain` — **our** line | `Orleans.Messaging` event `100071` — **Orleans'** line |
| the line says | `[ROUTE] Directed delivery to pod hub … failed — surfacing Failed DeliveryFailure to sender …` | `Failed to address message Request […]` |
| so it counts | one per **verdict** | one per **attempt** |
| occurrences | 191 | 3,959 |
| leg named | `IPodHubGrain.Deliver` | `IMessageHubGrain.DeliverMessage` |

Both legs are composed by `RoutingGrain` (`BuildPodHubRoute` and `BuildGrainRoute`), both go out
through the silo's hosted client, both are retried by the same `DeliverToGrainObservable`, and both
end at the same `ClassifyDeliveryException`. The two issues even share a fix branch in their history:
`fix/2299-2307-pod-hub-delivery-retry` (PR #2314) landed the retry half for both at once.

🚨 **The occurrence counts are therefore NOT comparable, and the larger one is not the worse
defect.** #2307 counts addressing attempts, and our own bounded retry multiplies each delivery by up
to six with an exponential backoff of 250 ms → 3 s. A burst of "distinct message ids about one second
apart" — which #2307's body read as evidence of a **caller** resending on a fixed interval — is what
one delivery's retry ladder looks like from inside Orleans. There is no such caller: both retry sites
are `Observable.Defer`-cold (so every attempt re-invokes `GetGrain`, i.e. re-resolves placement),
bounded (6 and 5 attempts), and exponentially backed off.

## The four rejection shapes, and which predicate sees each

`ClassifyDeliveryException` answers `ShuttingDown` for `IsDirectoryUnstable ‖ IsShutdownShaped ‖
IsScopeTeardown`, and `Failed` for everything else. What production actually delivers:

| Orleans' rejection | means | seen by |
|---|---|---|
| `…is not stable to perform the lookup… Retry later.` / `hop limit is reached` / `on invalid silo` | the grain directory is mid-handoff | `IsDirectoryUnstable` (#1742 / #2357 / #3139) |
| `Unable to connect to S10.244.2.223:11111:148812047 …` — `HostUnreachable`, `ConnectionRefused` | nothing is listening at that pod incarnation | `IsDepartedSiloRejection` |
| `The target silo is no longer active: target was …:146524552, but this silo is …:146534005` | the pod restarted and reclaimed its address | `IsDepartedSiloRejection` |
| `…for 2 times after "DeactivateOnIdle was called." to invalid activation. Rejecting now.` | the target **grain** deactivated while the message was in flight | `IsDeactivatedActivation`, gated on a discriminator — and for one release that gate made it unreachable on the leg that produces it; see below |

The first row was already recognised. The middle two are this page's subject. The fourth is a
different statement — about an activation on a live silo, not about the silo — and is handled
separately.

## Why the cure is the CLASSIFIER, not a retry

This is the fourth instance of one shape in this codebase, and naming it is the point: **the machinery
that cures the condition already exists and is already applied to it; the defect is a classifier that
cannot read its input, so the machinery gated on it is unreachable.** #1742, #2357, #3139 and #2451
were all this. Adding a retry here would have been inventing a second cure for a condition the first
one already covers — `IsTransientFailure`'s `OrleansMessageRejectionException` type test matches both
shapes, so they had been retried with a fresh resolve six times since PR #2314. Only the verdict was
wrong.

## 🚨 The fifth instance of that shape: a SAFE DEFAULT made the fourth row's predicate inert

The shape above has a variant that is harder to see, because the classifier *can* read its input —
it was simply never handed it.

`IsDeactivatedActivation` is deliberately not a bare text match. The same rejection means two
opposite things, and `GrainActivationFailureRegistry` documents the other one: a **per-node hub** in a
*persistent* activation-fault loop — a NodeType whose compile cannot materialise a hub configuration
— has an alive window of about zero, so every delivery lands in a deactivation window and Orleans
answers with this exact sentence. That grain never recovers, and demoting it would hide a real defect
behind a transient NACK. So the text is a NECESSARY condition and the registry is the discriminator:

```csharp
internal static bool IsDeactivatedActivation(Exception ex, bool activationErrorRecorded) =>
    !activationErrorRecorded && /* the text test */;

internal static ErrorType ClassifyDeliveryException(
    Exception ex, Func<bool>? scopeDisposed = null, bool activationErrorRecorded = true) => …;
```

The default is `true` — *assume the worse case* — so a caller that cannot consult the registry leaves
the verdict terminal, exactly as this page's own rule demands.

**And the pod-hub leg was such a caller.** `BuildPodHubRoute`'s terminal arm called
`ClassifyDeliveryException(ex, IsServiceScopeDisposed)` — two arguments — so on that leg the arm could
never fire, while `BuildGrainRoute` passed
`activationErrorRecorded: !string.IsNullOrEmpty(activationError)` and worked. The evidence says which
leg matters: the rejection names `IPodHubGrain.Deliver`, and #2299 is almost entirely this shape — 947
recorded occurrences, every one of the newest samples, each logged as *"surfacing **Failed**
DeliveryFailure to sender …"*. The predicate shipped, its unit facts were green, and production kept
printing the pre-fix verdict in one word.

> 🚨 **A predicate gated on a discriminator that defaults to the safe answer is INERT at every call
> site that does not pass the discriminator — and the site that produces the fault may be one of
> them.** There is nothing to grep for, because the defect is an argument that was not written: the
> predicate reads correct, and the call site compiles.

### Why `false` is a FACT on this leg, not a guess

Passing it on a leg that merely *lacks* a registry would be a guess. Here it is a statement about the
grain:

1. **The registry is per-node-hub by contract** — "the LAST activation failure observed for each
   per-node-hub grain" — and only `MessageHubGrain` writes it.
2. **A `PodHubGrain` has nothing to fail at activation**: no NodeType, no hub configuration to
   materialise, and a class contract that it must *not* throw from `OnActivateAsync` because the
   refusal is the CALL's answer (`PodHubNotHereException`). It cannot enter the loop the discriminator
   separates, so it never records an error and never could.
3. **Therefore the only way a pod-hub activation becomes invalid is the idle deactivation it requested
   of itself** when it answered "not here" — a lifecycle transition by construction, which is this
   page's bar.

`ClassifyPodHubDeliveryException` states that once, with the argument attached, and the leg calls it.

### What the corrected verdict does NOT fix

The **bounce that produces the rejection**. With `[PreferLocalPlacement]`, a delivery to a pod-hub
address with no live owner activation is placed on the *caller's* silo, which holds no local route for
it; `PodHubGrain.Deliver` then requests its own idle deactivation **and** throws. The throw answers
*that* message terminally; every message arriving in the deactivation window is answered by **Orleans**
instead, transiently, and the router's six re-resolving retries can each place another throw-away
activation whose own window bounces the next arrivals. That is self-sustaining, and it is a lifecycle
question rather than a classification one:

| candidate | what it costs |
|---|---|
| stop requesting deactivation in `Deliver`, so the grain's own TERMINAL refusal is the only answer | the throw-away activation lingers instead of being reaped promptly. It holds no route and no stream, and the claim path still converges because `Attach` requests deactivation on its own non-owning path — but the ceiling becomes *silos × addresses recently mis-routed to them* rather than zero |
| bound that ceiling with Orleans' own reaper — a collection-age limit on the grain type, which can only affect UN-pinned activations (the owner's is pinned indefinitely, a released address carries a finite tombstone) | the age must stay at or above the collection quantum; it is a bound stated from source rather than a timer anyone wrote |
| refuse to re-send the delivery on this rejection for this leg alone, since prefer-local guarantees the retry re-places the squatter | loses the retries that DO succeed because the owner's claim landed between attempts — a reduction in a symptom's volume, not a removal of its cause. Recorded so it is not mistaken for one |

The first two belong together and want one measurement on a real cluster: is an inert activation cheap
enough that idle collection alone is an acceptable reaper. None of the three is a classification
change, which is why the classification landed on its own.

### What is pinned, and what is not

`PodHubDeactivatedActivationClassificationTest` pins the **decision** from both sides, with both
production texts quoted verbatim: the pod-hub classifier answers `ShuttingDown`, the general
classifier with its own defaults still answers `Failed` for the identical exception, and the verdict
flips on `activationErrorRecorded` alone — so the discriminator cannot be collapsed into an
unconditional text match. It also pins that the container probe is forwarded and that a rejection
carrying no recognised phrase stays terminal on this leg too.

🚨 **And it pins the WIRING, which took a second pass.** The first version of this change pinned only
the two classifiers, and review caught the obvious consequence: reverting the one line in
`BuildPodHubRoute` that chooses between them left all eight facts green — **the same shape as the
defect being fixed**, an argument not written at a call site, one level out. So the arm is now one
tested function: `TerminalCallFailure` delegates to `AnswerPodHubCallFailure`, which holds both
decisions (which classifier, and the level the verdict deserves), and two facts drive that function
and capture what it hands the sender and the logger.

Measured, with the revert applied as a control: **2 of the 10 facts go red and 8 stay green** — which
is exactly the blindness the review named, now visible from inside the suite rather than only in
prose. The remaining unpinned surface is a one-line delegation with no logic in it.

The transferable half: **when the defect is "a decision was not expressed at a call site", a fix
pinned only by facts about the decision's INPUTS reproduces it.** The fix has to move the decision
somewhere a test can execute the same code production does.

## The bar, and why a timeout deliberately fails it

`ClassifyDeliveryException` is **narrower than `IsTransientFailure` on purpose**, and the asymmetry is
load-bearing. "Is another attempt worth making right now" is safe to answer generously — it is bounded
by a retry budget. "Should the sender keep its recovery machinery armed" is the other side, so only a
condition that is **a lifecycle transition by construction** qualifies.

Both departed-silo shapes clear that bar because **a `SiloAddress` is generation-stamped** —
`S<ip>:<port>:<generation>`. Each shape is a statement about one *incarnation*: an incarnation whose
socket refuses connections, or one that has been explicitly superseded by a named successor. Neither
can begin answering at that address again, and the only cure is the re-resolve the retry performs.

A bare `TimeoutException` is the opposite statement and stays terminal: the silo *accepted* the
connection and did not answer across the whole budget, i.e. plausibly wedged rather than restarting.
Telling a sender "transient" about a wedge is a resubscribe storm against a hub that never comes back.

**And what the verdict arms is bounded, which is what makes the generous answer safe here.**
`MeshNodeStreamCache`'s transient-fault breaker gives a transient claim three grace failures and then
backs re-probes off exponentially (1 s base, 60 s cap), on the explicit reasoning that a *streak* is
empirical proof the transient claim was false. So a departed-silo condition that somehow persisted
costs a bounded, backing-off retry — against the old answer's cost, which was every live mirror on
that path torn down permanently by a roll.

## Two arms, and only one of them is prose

**Arm 1 is a TYPE, with no wording in it at all.** `Orleans.Runtime.Messaging.ConnectionFailedException`
is public, and it is thrown only by `ConnectionManager.GetConnectionAsync(SiloAddress)` — so it is only
ever about a *cluster* endpoint. That makes the type strictly stronger than any phrase and immune to an
Orleans re-wording. It is also the arm that catches Orleans' *carried exception wins* resolution
(`rejection?.Exception ?? new OrleansMessageRejectionException(…)`), where the caller receives the
connect failure **bare**, with no rejection wrapper in the graph at all — the mechanism `IsDirectoryUnstable`
exists for (#1742 / #2357).

**Arm 2 is the phrase, guarded by the CONCRETE rejection type.** The superseded-generation shape needs
it: Orleans rejects that one with no carried exception, so the caller does get
`OrleansMessageRejectionException` and its detail exists only as text. The connect shape is covered
twice over, because production's wrapper embeds the inner's text.

🚨 **The guard is the concrete rejection and deliberately NOT the `OrleansException` base.** The first
revision of this used the base, mirroring `IsDirectoryUnstable`, and review caught that it is broader
than the signal: a clustering or storage provider that cannot reach *its* endpoint throws a bare
`OrleansException` saying exactly *"Unable to connect to …"*, and that is a genuine defect — reporting
it as transient would arm a resubscribe against a misconfiguration. Prose is only a signal on Orleans'
own transport types.

Both arms are pinned by `DepartedSiloClassificationTest`, in both directions:

- the phrases are asserted against the **shipped** Orleans assemblies' own string literals —
  `Unable to connect to` is a literal of **Orleans.Core** (where `ConnectionManager` lives),
  `The target silo is no longer active` of **Orleans.Runtime**. If an Orleans upgrade re-words either,
  the pin goes red. **Repair the marker; never delete the pin** — a classifier that stops matching
  fails *open* into the silence it removes. (Arm 1 cannot go inert this way, which is why it exists.)
- the refusing direction is mutation-measured, five ways:

| mutation | red |
|---|---|
| predicate not wired in (**the pre-fix answer**) | **6** — the 4 production shapes, the by-type fact, the aggregate fact |
| arm 2 (the phrase) dropped | **5** — the 4 production shapes and the aggregate fact |
| arm 1 (the type) dropped | **1** — `AConnectionFailure_IsAcceptedByTypeWithoutAnyPhrase` |
| arm 2's guard widened back to `OrleansException` | **1** — `ABareOrleansExceptionCarryingThePhrase_StaysTerminal` |
| both guards dropped, phrase matched on any exception | **3** — adds an application-level connect failure |

If Orleans re-words a phrase this returns to the previous answer rather than misclassifying anything —
the safe direction to fail in.

## A THIRD site asks the same question, and is deliberately left alone

`ClassifyDeliveryException` lives on the silo side. The **caller** side has its own exception arm —
`OrleansRoutingService`'s dispatch `Catch`, which classifies with

```csharp
var shuttingDown = IsHostStopping;
…
    SendDeliveryFailure(delivery, $"Failed to deliver to {address}: {ex.Message}",
        shuttingDown ? ErrorType.ShuttingDown : ErrorType.Failed);
```

`IsHostStopping` is a statement about **this** process, so a departed-silo rejection there is reported
terminally exactly as it was on the silo side. And it is reachable: `RoutingGrain` carries no placement
attribute and its key is the single `"default"`, so a caller's `RouteMessage` can be addressed to a
routing-grain activation on **another** silo, which can depart while this process is perfectly healthy.

It is left unchanged on purpose, and the reason is worth keeping:

- **No incident is fingerprinted on it.** Both of these are on `RoutingGrain`'s verdict and on Orleans'
  addressing log. Changing a classification in the one place that NACKs *in bulk while a silo is
  leaving* — which is when the mesh can least afford an answering storm — on no evidence is the wrong
  trade.
- **The dependency runs the wrong way for a cheap fix.** `MeshWeaver.Hosting.Orleans` references
  `MeshWeaver.Connection.Orleans`, not the reverse, so that site cannot call the classifier; a fix
  today would mint a **third** mirrored predicate. Mirror drift is the documented historical failure
  mode here (*"a fix landed on one site and missed the other is precisely how #2346 outlived both of
  its earlier fixes"*). If evidence for this site ever appears, the fix is to move the classifier
  **down** into the shared assembly beside `IsTransientFailure` and `IsDirectoryUnstable` — removing a
  mirror rather than adding one.

## The reading traps these two incidents taught

- 🚨 **A `LogIncident`'s `namespace` field is the FIRST-seen namespace, not a per-sample one.** Both
  incidents read `memex-cloud`; their newest samples are on pod generations that portal never ran. A
  closure argued on *"that portal is pinned to an older image, so it cannot carry the fix"* would be
  reading the wrong deployment.
- 🚨 **An incident fingerprinted on a DEPENDENCY's logger counts what the dependency saw, not what we
  did.** #2307 has no MeshWeaver frame in any sample — its own body says so — which is why its stated
  defect ("something above `IMessageHubGrain` is re-sending on a fixed short interval") had to be
  checked against the code and turned out not to exist. A hypothesis in an issue body is a hypothesis,
  including in one the reporter was confident about.
- 🚨 **A rate that collapses is not a fix.** #2307 went from 3,038 occurrences in three days to ~20 a
  day, which reads like the defect going away and is really the deploy cadence changing. The condition
  was unchanged in `src/` the whole time.

## Related

- [Error Propagation And Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — what a terminal verdict
  costs a consumer, and the two roots of a storm.
- [A Name That Does Not Resolve Is Not Transient](/Doc/Architecture/ANameThatDoesNotResolveIsNotTransient)
  — the same question at the outbound-HTTP boundary, with the opposite answer: there, the permanent
  condition was being retried.
- [Pod Hub Delivery Roll Plan](/Doc/Architecture/PodHubDeliveryRollPlan) — why the pod-hub leg is a
  directed grain call rather than a stream publish, i.e. why these failures are observable at all.
