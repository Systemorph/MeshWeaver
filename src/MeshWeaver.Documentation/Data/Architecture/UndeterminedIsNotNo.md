---
Name: Undetermined Is Not No
Category: Architecture
Description: A read that did not answer is a THIRD state, and rendering it as a negative is a defect class this platform keeps producing. What the third state costs when it is missing, why a second witness in the same failure domain is not a second witness, and the rule for deciding what a gate does with "I could not determine".
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="12" r="2"/><circle cx="18" cy="5" r="2"/><circle cx="18" cy="12" r="2"/><circle cx="18" cy="19" r="2"/><path d="M8 12h8"/><path d="M8 11.2 16.3 6"/><path d="M8 12.8 16.3 18"/></svg>
---

# Undetermined Is Not No

**A read that did not answer is a third state.** Ready, NotReady, and *Undetermined* — and a
component that models only the first two must render the third as one of them, which means it is
always wrong about one of the two cases it collapsed.

This page exists because the collapse keeps happening here, in different subsystems, wearing
different clothes, and because the correct response to it is not obvious: *"then fail open"* and
*"then fail closed"* are both guesses, and the interesting answer is usually neither.

## The shape

```text
    read the evidence
          │
   ┌──────┴───────┐
 answered      did not answer          ← the branch that keeps getting dropped
   │              │
 yes / no      ??? ──► collapsed into "no"
```

The collapse is attractive because it is *conservative-looking*: "no" is the cautious answer, so
folding an unreadable answer into it feels safe. It is not, for two reasons.

1. **It is a claim the process is not entitled to make.** The log line, the health payload and the
   incident report all end up saying *"there is no X"* about a question nobody asked. An operator
   reading it goes looking for the wrong thing.
2. **Whether "no" is even the cautious answer depends entirely on what the caller does with it.**
   The same `null` that licenses a redundant, harmless rebuild in one caller holds a production
   rollout in another. A folding whose safety rests on "every caller's negative branch is
   conservative" expires silently the moment someone adds a caller for whom it is not.

## The measured instance: the pre-warmer's second door

`Doc/Architecture/BuildCoordination` describes the pre-warmer's two doors — the
`SubscribeRequest` to `Admin/Build`, and, when that cannot be opened at all, a direct read of the
durable build root. Issue #3404 is what forced the second door into existence: on `memex-cloud`,
2026-09-06, two pods refused readiness at 11:44:32Z and 11:49:37Z for a fingerprint whose GO had
been written on an already-`Ready` build root at **11:28:56Z**. They refused a build that had
already been approved.

The second door closed that. What it did **not** close is that `ReadBuildGo` folded three outcomes
into one `null`:

| Outcome | What actually happened |
|---|---|
| The row carries no GO for this fingerprint | The witness **answered**. A real negative. |
| There is no durable store at all | Nothing was established. |
| The read **failed** | Nothing was established. |

The door had two branches, so it refused on all three — and its log line said *"the durable witness
carries no GO for framework X"*, about a read that had timed out.

### 🚨 The second door was in the FIRST door's failure domain

This is the part that turns a tidiness argument into a defect. In the fleet's portal wiring
`AddPartitionStorageHubs` **replaces** `IStorageAdapter` with `RoutingProxyAdapter`, whose `Read`
is:

```csharp
hub.Observe<ReadNodeResponse>(new ReadNodeRequest(path, options), o => o.WithTarget(addr))
```

— a hub request on the **same transport**, under the **same 60 s budget**, as the
`SubscribeRequest` that just went unanswered. Of the five candidates on record for #3404's silence
(a routing loss; a wedged per-node hub; a lost reply; the deferred-queue ordering defect #3408; a
root that stops emitting), the first, fourth and fifth take **both** doors down together. So on the
very fault the second door was built for, the expected reading is *Undetermined* — arriving as the
same `TimeoutException` — and that was exactly the reading being laundered into "no GO".

**A recovery path that shares a failure domain with the thing it recovers from is not a recovery
path.** Check the domain, not the API surface: two different methods on two different interfaces
can be one Orleans hop apart.

## What the third state does at a gate

The house rule for a readiness gate is unambiguous and stays unchanged:

> *Finding nothing is not passing. A gate that certifies "I verified nothing" is worse than no gate,
> because the rollout proceeds with a green light nobody earned.*

That rule answers *"may I pass on no evidence?"* — no. It does **not** answer *"what counts as
evidence?"*, and that is the question `Undetermined` actually raises. Both stock answers are
guesses:

- **Fail open** — grant, on the argument that lazy compilation covers correctness anyway. This
  serves a pod that may genuinely be broken, and it deletes the gate on precisely the days the
  cluster is unhealthy, which are the days it matters.
- **Fail closed** — refuse, on the argument above. This is what shipped, and it holds a rollout on
  a transient. In #3404 it held two rollouts for a build that had already been approved.

**Neither is the answer. The answer is: do not guess — look for a witness in a different failure
domain, and MEASURE.**

For the pre-warmer that witness was already in the pod's hands: the `IAssemblyStore` — a blob
container in production, a mounted volume in the monolith and in tests, and **never a hub message**.
`NodeTypeBakeStatus.Probe` asks it, per NodeType, whether bytes exist for the live framework
identity. That is a measurement of exactly what the readiness gate is about, taken outside the
domain that shut both doors.

So the verdict rule is:

| Reading | Verdict | Why |
|---|---|---|
| `Go` | **Ready** — grant, then probe the share | Another process certified this image's build. |
| `NoGo` | **NotReady** — refuse, fail closed | The witness **answered**. No process has certified a build for this image, and a pod does not overturn a coordination answer from its own volume. |
| `Undetermined` | **Measure** — grant only if every previously-healthy NodeType is already baked on this pod's own store; refuse otherwise | Nobody said anything at all, so the only honest input left is what this process can establish itself. |

### The grant is STRICTER than the door it substitutes for

That is what makes it defensible rather than a fail-open in disguise. A pod that receives a GO
grants and then reports still-pending types as `PreWarmStatus.TimedOut`, which `IsGatingFailure`
deliberately does **not** gate on — so **a half-baked share passes with a GO**. The undetermined
branch grants only when `NodeTypeBakeReport.GateRelevant` is *empty*: every type that was healthy
before this image is `Baked` for this framework identity. A share that passes *with* a GO can be
refused *without* one. The asymmetry is the point — a GO is somebody else's certification, and this
branch has none, so it may only grant on evidence it measured itself.

And when the measurement comes up short, the refusal is unchanged in force and improved in honesty:
it re-throws the original `BuildCoordinationUnreachableException` (so `DescribesUnreachableCoordination`
still separates *no verdict* from *a bad verdict* in the health payload), and it says the witness
was **unreadable**, quoting why — never that there is no GO.

## The rule, generalised

1. **Model the third state in the type.** A `T?` can carry an answer but not its provenance.
   `BuildGoReading` is `(Witness, Go, Detail, Error)`: the classification, the value, always a
   reason, and the fault when there was one. A nullable return that documents *"null means one of
   three things"* is a third state that was modelled in prose instead of in code — which is to say,
   not modelled.
2. **Undetermined always carries a diagnostic.** If it cannot say *why*, the caller cannot report
   honestly and the next investigation starts from zero.
3. **Never narrate Undetermined as an answer.** This is half the defect on its own: #3404's refusal
   line sent an operator looking for a build nobody had refused.
4. **Decide what the gate does with it, out loud, and write down what decided it.** Fail-open and
   fail-closed are both defensible; the unacceptable outcome is a verdict nobody argued.
5. **Prefer a third witness over either guess** — but check that it is genuinely in a different
   failure domain first.
6. **A fold is licensed by its callers, not by itself.** `ReadBuildGo` still folds, and is still
   correct for a caller whose negative branch is *bake into a content-addressed store*: that costs
   one redundant compile and can never corrupt. Its contract now says so explicitly, and points a
   caller whose negative branch is anything else at `ReadBuildGoReading`. When you add a caller to a
   folding read, re-derive the licence — do not inherit it.

## What this is not

Nothing here is a retry, a widened bound, a watchdog, a poller, or a `catch {}`. The transport fault
that shuts both doors is **real, unfixed, and separately tracked**; it is still logged at its own
severity with its own diagnostic detail on every branch, because it remains the only signal that the
pod↔`Admin/Build` path is broken. Modelling the third state changes the readiness **verdict** and
the **reporting**; it does not diagnose or repair the silence, and it must never be mistaken for
having done so.

## Where this is pinned

- `test/MeshWeaver.Hosting.Test/UndeterminedWitnessIsNotNoGoTest.cs` — the durable read fails
  exactly as the transport does; the reading classifies `Undetermined`, the grant rests on staged
  bytes in a real store, the refusal survives a genuinely pending type, and the log never says
  "carries no GO".
- `test/MeshWeaver.Hosting.Test/PreWarmerReadsTheDurableGoTest.cs` — the complementary arm, which
  needs a *readable* witness: `NoDurableGo_StillRefuses_EvenWhenTheShareIsFullyBaked`. An
  implementation that simply granted whenever the share looked complete passes every case in the
  first file and fails this one.

## See also

- [An Unreachable Store Is Not a Refusal](../StoreUnreachableIsNotARefusal) — the same distinction
  one layer down, on the WRITE side: a store that could not be reached means the operation was never
  evaluated, and reporting that as a refusal is how a retried create becomes a duplicate. That page
  is the classification; this one is what a GATE does with it.
- [Build coordination](../BuildCoordination) — the protocol, the two doors, and the five candidates
  for the silence.
- [Reading CI Signals](../ReadingCiSignals) — the same collapse in a different subsystem: a skipped
  or absent required check is *undetermined*, and GitHub paints it the colour of a pass.
- [NodeType compilation](../NodeTypeCompilation) — what the assembly store actually holds, and why
  probing it is a measurement rather than a marker.
