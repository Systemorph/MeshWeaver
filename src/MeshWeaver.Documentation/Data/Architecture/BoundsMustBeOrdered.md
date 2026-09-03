---
Name: Bounds Must Be Ordered
Description: "Two independently authored timeouts that share a number do not merely overlap — the shorter one silently suppresses the longer one's diagnosis, on every occurrence. The fix is not a bigger number but deriving the outer bound from the inner one."
---

# Bounds must be ordered, and derived — never authored twice

Every wait in this system has a bound. Most of them were written as literals, independently, by
people who could not see each other's numbers. That is fine right up to the moment two of those
bounds sit on the *same* operation — because then the shorter one always fires first, and whatever
the longer one was going to say is never said.

This is not a tuning problem. **A bound placed just under another bound converts a diagnosable
failure into an anonymous one**, and it does so silently, on every occurrence, forever.

## The concrete case (#2819)

A mesh write has a framework-side outer bound. `UpdateRemote` waits for the owner's commit verdict
and, if none arrives, fails the write:

| constant | value | why |
|---|---|---|
| `LatePatchResponseRegistry.LateResponseWatchBound` | 30 s | the registry stops honouring a late verdict here |
| `LatePatchResponseRegistry.VerdictBoundGrace` | 1 s | **deliberate** slack so the caller's failure cannot race a verdict that is still admissible |
| `LatePatchResponseRegistry.WriteVerdictBound` | **31 s** | the instant the caller is told `OwnerUnreachable` |

The grace is the interesting part. Someone thought carefully about ordering *those two* bounds, and
wrote a comment saying so: fire at the same instant and you race an admissible verdict, so add a
second.

Meanwhile, the test convention for waiting on anything was a hand-written `30.Seconds()` — about
2,679 times across the fleet. Nobody chose it to relate to `LateResponseWatchBound`. It is the same
number by coincidence.

The consequence is exact and total: **a test awaiting a mesh write gives up one second before the
framework produces its diagnosis.** Not sometimes — always. So every one of these failures reads

```
ObservableAssertionException : Expected the observable to emit a value within 30s,
but it did not. The observable emitted nothing at all.
```

when the framework was one second away from saying

```
MeshNodeStreamException: MeshNode OwnerUnreachable —
the owner produced no terminal for this patch (bound=31s)
```

The second message names a subsystem, a path and a contract breach. The first names nothing. The
bound had been placed at precisely the value that destroys the most information.

## Why this is worth a rule

Note what did *not* go wrong:

- Nobody set the bound carelessly. 30 s was a reasonable guess about convergence.
- The framework's own two bounds *were* correctly ordered, with a comment explaining why.
- The test bound and the framework bound were each defensible in isolation.

The defect is **entirely relational**. It exists only in the pairing, and neither author could see
the pair. That is why "be careful when choosing a timeout" cannot prevent it, and why a review
cannot catch it: the two numbers live in different trees, and both look right.

It also explains why this survived a fix. #2708 introduced `TestTimeouts` for #2700 and scaled the
CI bound to 90 s, which incidentally cleared 31 s — so on CI the diagnosis started arriving. Locally
the baseline stayed 30 s, so on the machine where a developer actually reads the failure, it still
said nothing. **A partial fix that repairs the loud environment and leaves the quiet one is worse
than none**, because it removes the pressure to look again.

## The rule

> 🚨 **When one bound wraps another, the outer one is DERIVED from the inner one, never authored
> alongside it.**

Concretely, in `TestTimeouts`:

```csharp
// The framework's own outer bound on a caller-visible write.
private static TimeSpan FrameworkWriteBound => LatePatchResponseRegistry.WriteVerdictBound;

// Derived, so the ordering holds by construction rather than by whoever writes the next test.
private static TimeSpan LocalConvergence => FrameworkWriteBound + TimeSpan.FromSeconds(5);
```

Two properties matter, and both are load-bearing:

1. **Derivation, not duplication.** The moment the framework bound moves, the test bound moves with
   it. A literal that "currently satisfies" the ordering is a bug scheduled for the next time
   somebody tunes the other end.
2. **Additive slack, not a ratio.** What must be covered is the propagation of *one terminal* — the
   framework produces its verdict and it has to reach the assertion. That cost is constant; it does
   not scale with the bound. A multiplier buys the same few seconds while inflating every wedged
   test's failure time.

Make the framework bound `public` even if nothing outside needs to *use* it. **A bound nobody can
see gets re-authored as a literal somewhere else** — and that is the whole mechanism above.

## Guarding it

The ordering is a property, so assert the property, under the inputs that could break it — at every
scale factor, including the local `1.0`:

```csharp
[Theory]
[InlineData(null)] [InlineData("1")] [InlineData("3")] [InlineData("10")]
public void AConvergenceWaitDominatesTheFrameworkWriteBound(string? factor)
{
    using var _  = new EnvironmentVariable("GITHUB_ACTIONS", factor is null ? null : "true");
    using var __ = new EnvironmentVariable("MW_TEST_TIMEOUT_FACTOR", factor);

    Assert.True(TestTimeouts.Convergence > LatePatchResponseRegistry.WriteVerdictBound, "…");
}
```

🚨 **Check the local factor.** A guard that only runs at CI scale passes on a codebase where the
laptop still reports nothing — which is exactly the state #2708 left behind, green.

And **prove it by mutation**: restore the old literal and confirm the guard goes red on the local
and `1` cases and stays green on `3` and `10`. That asymmetry is itself the evidence — it shows the
guard is measuring the ordering and not merely the size of the number.

## A bound's JUSTIFICATION is a claim about other code, and it rots

Derivation keeps two numbers ordered. It does nothing for the sentence that says *why* a number is
big enough — and that sentence is usually a claim about code somewhere else, which can change
without ever touching the bound.

`LateResponseWatchBound` (30 s) was stated as **protocol, not tuning**: it had to dominate every
owner-side terminal path, enumerated as the disposal NACK after a teardown whose *"hosted-hub drain
[is] capped at 5 s"*, the cold-store defer (~10 s), and the ack watcher's 20 s. Two years of edits
later (#3197):

- **The 5 s cap was gone.** `HostedHubsCollection.DisposeHubsReactive` deliberately dropped its flat
  `Timeout(5s)` in #1317, and what replaced it is a *stall* detector re-armed on every `RunLevel`
  transition anywhere in the subtree — so a large subtree that keeps making progress never trips it.
  The drain has no duration bound at all, and the claim's first term was unsupported.
- **The terms were being added wrongly.** They were enumerated as alternatives, taking their maximum
  (20 s). On the in-turn path they compose *additively*: cold-store defer → identity-gated echo →
  durable flush.
- **Two different clocks.** The owner's starts at handler entry, the caller's at post, with unbounded
  routing latency between them — measured at 33–49 s during a bake.

Nothing failed a build. A comment cannot go red. The next reader inherits a number presented as
proven and reasons from it.

> 🚨 **When a bound's justification enumerates other people's bounds, it is a dependency — and it
> needs the same treatment as any other: re-measure it, or stop asserting it.**

Where re-establishing the guarantee would mean reverting a deliberate change — as it would here —
the honest repair is to **say what is actually guaranteed** and make the gap *visible* rather than
asserted away. Two shapes do that:

- **Check the premise where it is used.** The owner's ack watcher stands aside for the disposal NACK
  instead of posting its own verdict. That deferral assumed "the NACK is coming, and soon". It now
  asks: `ILatePatchVerdictSink.IsAdmissible(requestId)` — is a route actually armed? With no sink, or
  a watch already gone, standing aside would convert an answerable write into silence, so it answers
  now on whatever transport is still open. The predicate answers *"is a route armed now"*, not *"will
  it still be armed then"* — no implementation can promise the latter, which is exactly why the
  second shape is also needed.
- **Make the overrun observable.** A verdict arriving past the window is still not delivered — acting
  on it is what the bound exists to prevent — but it used to return the same `false` as a request
  nobody ever armed, so a failing run showed `VERDICT_TIMEOUT` with zero late-terminal records and
  "never produced" was indistinguishable from "produced, too late". Those are two different
  investigations. The registry now logs `VERDICT_EXPIRED` with how late it was, and counts it.

## Where else to look

The same shape appears wherever a wait wraps a wait. When you write a bound, ask what *else* bounds
the operation underneath it:

- a test wait around a framework write bound (this page);
- an xunit `[Fact(Timeout)]` around an internal convergence wait — `TestTimeouts` already derives
  `TestMilliseconds` from `Convergence` for exactly this reason;
- a CI step `timeout` around a test-runner bound — see
  [Shard weight headroom](../ReadingCiSignals);
- a caller's `.Timeout(...)` around `RequestTimeout` (60 s).

In every case the question is the same, and it is not "is this long enough?" but **"if this fires
first, whose explanation do I lose?"**

## Related

- [Guards and unknown states](../GuardsAndUnknownStates) — a guard whose stated reason is wrong can
  still be load-bearing; a classifier with no "I cannot tell" bucket picks the nearest one.
- [Writing tests](../WritingTests) — wait on the condition, never on a delay.
- [Debugging message flow](../DebuggingMessageFlow) — read the duration first: seconds means a real
  assertion failure, minutes with no assertion text means a hang.
