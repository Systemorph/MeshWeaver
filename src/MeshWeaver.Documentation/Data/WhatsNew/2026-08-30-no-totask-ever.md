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

A test now awaits the observable directly under a timeout
(`await hub.DisposalCompleted.FirstOrDefaultAsync().Timeout(30.Seconds())`), exactly as production
code composes and subscribes. The only place a bridge may still work is inside an activity, where
nothing mesh-side runs after the await — and even there the reactive shape is preferred.
