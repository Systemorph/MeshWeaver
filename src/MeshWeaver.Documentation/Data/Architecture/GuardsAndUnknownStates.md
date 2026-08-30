---
Name: Guards and Unknown States
Description: "Two silent failure modes that produced five defects in one evening: a guard whose STATED reason is wrong while it is load-bearing for an unwritten one, and a classifier with no representation for 'I cannot tell'."
---

# Guards and unknown states

Two rules, each earned by more than one defect. Neither is about carelessness: both failure modes
survive careful work, which is why they are written down rather than left to judgement.

## 1. A guard's stated reason can be wrong while the guard is load-bearing for an unwritten one

`RegisterOwnerDisposingNack` refused to post its NACK once the parent hub was past
`DisposeHostedHubs`. The comment explained why:

> *"During a whole-mesh teardown the parent is past that mark too, the post is skipped, and nobody
> is waiting."*

**That reason is false.** A caller whose wait outlives the start of teardown is still waiting, and it
is the caller that most needs the answer that is guaranteed not to get it — measured twice, in two
repositories, as a writer burning its full 31-second budget (MeshWeaver#2778).

So the guard was removed and the NACK delivered through the first ancestor that could still post.
The caller was answered. And the project it was measured in went from **21 seconds to 176 seconds**
for the same 164 tests — roughly 0.9 s of added cost on *every* teardown.

The guard was load-bearing for a reason nobody had written down: **it bounds the work teardown
does.** Waking callers mid-drain is not free, and every hub that goes down with a delivery
outstanding pays for it. A *rare* 31-second hang had been traded for a cost *every* teardown pays.

> 🚨 **Before deleting a guard, ask what it costs when it does NOT fire.** That is the half a
> comment usually omits, because the author was thinking about the case where it does.

This is the inverse of the more familiar failure — prose asserting a guard that does not exist. Here
the prose *understated* what the guard was for, which is harder to catch: the stated reason is
checkable and wrong, so disproving it feels like completing the analysis.

### Why it was nearly shipped

Nothing in the normal toolchain saw it. CI named four failing tests with a **rotating cast** — a
different set each run, which reads as flakiness. Every one passed in isolation. A genuine audit
turned up 13 test methods whose inner bound exceeds their own method timeout — a real defect, and
**not this one**.

> **A genuine defect discovered while hunting a different one is the most convincing wrong answer
> available.**

And the obvious remedy would have buried it: widening the enclosing timeout turns the suite green and
ships the regression. What found it was timing **the whole project against `main`, one variable at a
time**. A slowdown spread evenly across a suite has no per-test signature — it only makes the
tightest bound the first to expire, which looks exactly like a flaky test.

## 2. A classifier with no representation for "I cannot tell" does not abstain

It assigns to the nearest bucket and reports it with full confidence. Three instances, one evening:

| where | the buckets it had | the state it could not express |
|---|---|---|
| `search_chunks` | "found N" / "found 0" | **nothing was searched** — reported as `count: 0` |
| `ApplyCompileFailure` | `Ok` / `Error` | **no verdict was formed** — reported as `Error`, and the automatic re-drive then correctly declined forever |
| CI triage | "timing casualty" / "real assertion failure" | **process alive, one subsystem permanently dead** — a test carrying a genuine assertion *and* not being the defect |

The cost is not the wrong answer; it is that the wrong answer is **indistinguishable from a right
one**, so it gets acted on. `count: 0` reads as permission to delete public surface. `Error` reads as
"your code is broken" about code nothing evaluated. "Timing casualty" reads as "already fixed, stop
looking".

**The fix is never a better heuristic. It is adding the third state to the type:**

- `searched: false` with **no `count` field at all**, so a consumer testing `count == 0` finds the
  field absent rather than a zero that means the opposite;
- `Unavailable` as a distinct verdict from `Error`, so a re-drive can tell "not attempted" from
  "attempted and refused";
- refusing to classify at all when the evidence does not discriminate.

The repo already had the idiom before it had the rule:

```csharp
.DefaultIfEmpty(SelfUpdateVerdict.NoOutcome())
// "the only thing that stops a third one being added is making
//  'produced nothing' itself an outcome that gets reported."
```

### The same shape one level down

A bound is a two-state classifier. `Expected value to be 6 … but found 4` cannot distinguish *the
retry is broken* from *I looked too early* — and the value it reports in the second case is the
signature of the first. A false red that spells out the regression's own signature is worse than no
test, because the correct response to it is to go hunting a bug that is not there.

The fix is the same: wait for the condition that actually ends the work — a terminal the production
code already publishes — rather than for a counter to stop moving.
