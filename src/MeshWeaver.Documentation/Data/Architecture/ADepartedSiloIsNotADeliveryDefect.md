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
| `…for 2 times after "DeactivateOnIdle was called." to invalid activation. Rejecting now.` | the target **grain** deactivated while the message was in flight | its own predicate — a separate change |

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

## The signal is Orleans' prose, and that is a managed weakness

Neither shape has an exception type this assembly can name: Orleans rejects with
`OrleansMessageRejectionException`, which carries *every* refusal kind, so accepting the type alone
would classify genuine refusals as transient — the one direction this classifier must not fail in.
The predicate is therefore two narrow phrases behind an `OrleansException` type guard, and both
halves are pinned by `DepartedSiloClassificationTest`:

- the phrases are asserted against the **shipped** Orleans assemblies' own string literals —
  `Unable to connect to` is a literal of **Orleans.Core** (where `ConnectionManager` lives),
  `The target silo is no longer active` of **Orleans.Runtime**. If an Orleans upgrade re-words either,
  the pin goes red. **Repair the marker; never delete the pin** — a classifier that stops matching
  fails *open* into the silence it removes.
- the refusing direction is pinned too, and mutation-measured in three directions: unwiring the
  predicate reds the four production shapes; dropping the phrase reds an ordinary Orleans fault and a
  rejection carrying no departed-silo phrase; dropping the type guard reds an application-level
  connect failure that happens to use the same words.

If Orleans re-words a phrase this returns to the previous answer rather than misclassifying anything —
the safe direction to fail in.

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
