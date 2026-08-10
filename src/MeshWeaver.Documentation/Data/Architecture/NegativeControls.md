---
Name: Negative Controls
Category: Architecture
Description: A pin is only a pin if it fails against the defect. Revert only the fixing lines, confirm the expected failure message, restore, confirm it passes — and record the result. Three tests that looked green and correct while proving nothing, and what each one was actually measuring.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"/><path d="M4 12h16"/><path d="M4 18h16"/><circle cx="8" cy="6" r="2.2" fill="currentColor" stroke="none"/><circle cx="16" cy="12" r="2.2"/><circle cx="8" cy="18" r="2.2" fill="currentColor" stroke="none"/></svg>
---

A regression test that passes proves the code passes the test. It does **not** prove the test can
detect the defect it is named for. Those are different claims, and only one of them is what a
regression test is for.

> **A pin is only a pin if it fails against the defect.** Revert *only* the fixing line(s), run the
> test, confirm it fails with the message you expect, restore, confirm it passes. Record the failure
> output in the change that introduces the test.

This is not extra rigour for hard cases. It is the cheapest available check on the one property a
regression test must have, and it costs a minute. The alternative is a test whose failure mode is a
**false pass**: the guard silently disappears and the green tick does not.

## The procedure

1. **Write the test against the fixed code.** Green.
2. **Revert only the fix.** Not the test, not the signature, not a nearby refactor — the specific
   lines that change behaviour. If you cannot isolate them, that is itself a finding: the fix is
   entangled with something else.
3. **Run and read the message.** It must be the failure you predicted, in the words you predicted.
   "It failed" is not enough — a test that fails for the wrong reason is still not a pin.
4. **Restore, re-run, confirm green.**
5. **Paste the red output into the PR body.** That output is the evidence; the green run is not.

Two things this also buys you, beyond confirming the pin:

- **It rules out competing explanations, for free.** A control holds everything else fixed by
  construction. When `SyncedQueryChangeFeedStarvationTest` was reverted to the old change feed and
  reproduced the reported 30-second signature verbatim, that simultaneously eliminated "does the
  write publish on the watched path?" and "does the path matcher disagree for a children-scoped
  watcher?" — same publish, same matcher, same query, only the fan-out differed between red and
  green. See [Change-Feed Isolation](/Doc/Architecture/ChangeFeedIsolation).
- **It tells you when a test is a guard rather than a pin.** In one four-test suite only two went red
  under the control; the other two passed in both states, and that was the point — they existed to
  stop the *fix* from breaking a neighbouring behaviour. Say which is which. A suite where everything
  passes in both states pins nothing at all.

**State plainly where you could not produce a difference.** One control broke coalescing "the other
way" — each track creating its own node — and both the old and new test shapes went red. The report
said so, and named the remaining exposure as timing-dependent and therefore not demonstrable without
rigging the clock. A negative control that comes out neutral is a result, not a failure to report.

## Three tests that proved nothing

Each of these was written in good faith, passed, and was believed. What broke them is not
carelessness — it is that the assertion measured something adjacent to the property.

### 1. The count was 1 either way

`TrackActivity_ConcurrentSamePath_DoesNotRaceAlreadyExists` fires five concurrent posts at one path
and asserts they coalesce into one node. It waited for the **first** node to surface on the
eventually-consistent query index, then asserted `HaveCount(1)`.

The path *is* the storage key. **The node count is 1 whether or not the race is coalesced** — with
the coalescing fold removed, four of five creates throw `Node already exists` and their increments
are simply lost, and the count stays 1. The test could not detect the race it was named for, and the
blind spot is structural, not timing.

Demonstrated rather than argued: a temporary replica of the old shape ran side by side with the
migrated test against the same deliberately broken handler, in the same run.

```
TEST FAILED: Expected value to be 5 ... but found 1     <- migrated test          (RED)
Passed                                                  <- old PollForFirst shape (GREEN)
Failed: 1, Passed: 1
```

The assertion that actually pins the defect is `AccessCount == 5` — every track folded its increment
onto the live record. The waiting had to change too: the wait now covers all five writes
(`WhenSettled(path, writes)`), because a wait that returns while writes are still in flight makes
even a correct assertion vacuous.

**The lesson to carry:** ask what the assertion's value would be **if the defect were present**. If
the answer is "the same", the assertion is not measuring the property. Counting is the usual culprit,
because a count is so often invariant under exactly the merging or deduplication you are trying to
test.

### 2. Something else was doing the blocking

`PixelRenderIsolationTests` covers the pixel export's server-side-request-forgery surface: a headless
browser renders user-authored slide HTML inside the *server's* trust boundary, so it can reach
internal services the deck's author never could. There are two independent defences — a
Content-Security-Policy meta tag in the composed document, and Chromium's own process-level network
denial flags.

The first draft asserted the CSP with **both** layers armed. It passed. It would have passed with **no
CSP at all**, because the process flags were doing the blocking.

"No request arrived" is a claim with many explanations, and most of them are not the one you want: the
browser never had a route, the listener was misconfigured, the markup was wrong. Testing two layers
at once hides a hole in either.

The rebuilt suite makes each test single-variable, and demands a leak before believing a block:

```csharp
// ── Control: without the policy the slide MUST reach the listener. ──
probe.Reset();
await renderer.Render(WithoutPolicy(html)).FirstAsync().ToTask();
leaked.Should().BeGreaterThan(0,
    "the control must demonstrate the vector is real — a slide CAN otherwise make the "
    + "server's browser open a connection of its choosing. At 0 the assertion below would "
    + "prove nothing, which is exactly how the first draft of this test fooled itself.");

// ── Protected: the composed document must reach nothing. ──
probe.Reset();
var pdf = await renderer.Render(html).FirstAsync().ToTask();
probe.Connections.Should().Be(0, "…this is the SSRF surface…");
```

`The_policy_alone_stops_a_slide_from_opening_a_connection` runs with the process-level denial
neutralised, so the CSP is the only thing left that could block.
`The_process_flags_stop_it_too_even_with_no_policy_at_all` strips the CSP entirely. Two layers, two
tests, one variable each.

Note the third safeguard: the helper that strips the policy **throws** if the meta tag is ever
renamed, rather than silently returning an identical document. A control that quietly stops being a
control turns the whole suite green-and-meaningless — which is the same failure class one level up.

**The lesson to carry:** when an assertion is *negative* — nothing happened, no request arrived,
nothing leaked — a live positive control is mandatory. Disable one layer at a time and require the
control to fail.

### 3. It samples, so a green run is not evidence

`MeshHubDisposalLeakTest` walks GC roots looking for a `MessageHub` that survived disposal. It is an
excellent **discovery** probe and it found nine real leaks. It is not a pin, and a green run from it
is not evidence:

- **It samples.** A root that is live only for a bounded window — 1 s for a watcher re-establish,
  100 ms for a log flush — is caught only if the probe's forced GC lands inside that window. Fire
  first, get collected, go green, with the defect fully present.
- **It cannot attribute.** It reports the first hub reachable from *any* non-stack root, which may be
  a different defect than the one you are chasing.
- **It is inconclusive off Linux.** ClrMD snapshot-attach throws on macOS, so a surviving hub skips.

The pin for a leak is a **timing-free ownership test** next to the code that owns the subscription:
after the owner is disposed, the handle holding the pending timer is disposed too — a property that
holds whether or not the timer has already fired, so the test can neither flake nor pass by accident.
A `WeakReference` probe would be a sampling test of a 100 ms window, i.e. the thing that pins nothing.
[Subscription Ownership](/Doc/Architecture/SubscriptionOwnership) carries the full treatment, including
the negative-control table for the two live sites.

**The lesson to carry:** if the assertion's truth depends on *when* it is evaluated, it is a sample,
not a proof. Sampling probes belong in discovery, never in the regression suite as the guard for a
specific defect.

## What a good control looks like in a PR

Name the test, show the reverted state, show the message.

```
`CreateNodeAlwaysAnswersTest` (4 tests). With the fix: 4/4 pass in 411 ms.
With ONLY MeshExtensions.cs reverted:

  Create_WhenAdapterDeclinesTheWrite_AnswersWithFailure [FAIL] [15 s]
    Expected the observable to emit a value within 15s … but it did not.
    The observable emitted nothing at all.
  Create_WhenSaveCompletesWithoutEmitting_AnswersWithFailure [FAIL] [15 s]
    … The observable emitted nothing at all.
  Failed! - Failed: 2, Passed: 2

The other two pass in both states, which is the point: they are guards, not the pin.
```

A hang is the pre-fix behaviour there, so a timeout is what the assertion reports — and saying so
explicitly is part of the evidence, because otherwise a reader cannot tell a real pin from a slow
test.

## When it is harder than reverting one line

- **The fix is a deletion.** Re-add the deleted lines; that is still "revert only the fix".
- **The fix spans a predicate used in several places.** Revert the predicate alone and expect several
  cases to go red together — five cases red against the previous predicate and green after is a
  perfectly good control, and is how the compile-settle predicate was pinned.
- **The defect only appears under a scheduler race.** Build both orderings as **arms of one test**,
  so each arm is the other's control: post-then-observe must provably lose the reply, pre-register
  must buffer and replay it. The fence is a real completion signal — two full round-trips, node
  readability — never a fixed delay, so it cannot race. `UpsertInnerCreateObservationTest` is the
  worked example.
- **You genuinely cannot make it deterministic.** Say so, say what you *did* prove, and name the
  residual exposure. Do not upgrade "I could not reproduce it" into "it is fixed".

## See also

- [Writing Tests](/Doc/Architecture/WritingTests) — the golden rules, the reactive assertion surface, and why a CI-only failure is never a flake.
- [Reactive Test Assertions](/Doc/Architecture/ReactiveTestAssertions) — the assertion API these controls are expressed in.
- [Subscription Ownership](/Doc/Architecture/SubscriptionOwnership) — the leak case in full, with its control table.
- [Silent Completion](/Doc/Architecture/SilentCompletion) — the failure shape whose only symptom is a timeout, so its pin *is* a timeout.
- [Change-Feed Isolation](/Doc/Architecture/ChangeFeedIsolation) — a control that ruled out two competing hypotheses for free.
