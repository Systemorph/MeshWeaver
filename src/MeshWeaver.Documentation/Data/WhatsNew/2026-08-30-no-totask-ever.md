---
Name: No ToTask, ever — the test exemption is retracted
Category: Fix
Description: Bridging an observable to a Task with .ToTask() is now forbidden everywhere, tests included, because the awaiter resumes inline inside Rx's trampoline and the continuation inherits it.
Order: -20260830
Icon: Bug
---

# No ToTask, ever — the test exemption is retracted

The guidance used to say tests were the one sanctioned place to bridge an observable to a `Task`.
That exemption never held: a `Task` completed from inside an Rx pipeline resumes its awaiter
**inline, on the signalling thread, still inside Rx's trampoline**, and everything the continuation
does inherits that — so a bridge written "only in a test" changes how the code under test runs.

> ⚠️ **Corrected 2026-09-18 — the paragraph below was wrong, and this is where the mistake
> started.** It recommended awaiting the observable *directly*. That is the **same** defect in fewer
> characters: Rx's own awaiter is an `AsyncSubject<T>` that completes its continuation from inside
> `OnCompleted`, so it resumes inline on the signalling thread exactly as `.ToTask()` does. What a
> test should write instead is an assertion on the stream —
> `await x.Should().Within(TestTimeouts.Convergence).Emit("because")` — or, where an `async Task`
> signature genuinely must take a value out of one, `.Await(ct)` / `.ObserveCompletion(reportLateFault, ct)`.
> The original text is kept below so the record of what was recommended, and for how long, stays
> readable. See *In-mesh test cases no longer continue on the portal's own threads* (2026-09-18).

~~A test now awaits the observable directly under a timeout
(`await hub.DisposalCompleted.FirstOrDefaultAsync().Timeout(30.Seconds())`), exactly as production
code composes and subscribes.~~ The only place a bridge may still work is inside an activity, where
nothing mesh-side runs after the await — and even there the reactive shape is preferred.
