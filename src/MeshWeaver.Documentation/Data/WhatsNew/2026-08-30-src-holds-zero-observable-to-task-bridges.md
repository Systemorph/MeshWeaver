---
Name: src/ holds zero observable-to-Task bridges, and a ratchet keeps it there
Category: Fix
Description: Every .ToTask() in the framework, the CLI tools and the shared test helpers is gone — waits now go through ReactiveCompletion.ObserveCompletion, which queues the caller's continuation instead of resuming it inline on the signalling thread.
Icon: Bug
Order: -20260830
---

# `src/` holds zero observable-to-Task bridges, and a ratchet keeps it there

Bridging an observable to a `Task` completes that Task from **inside** the Rx pipeline, without
`RunContinuationsAsynchronously` — so the awaiting code resumes **inline, on the signalling thread,
still inside Rx's trampoline**, and everything it then calls inherits that. It is how a grain
teardown came to park the very scheduler its own deactivation needed, and how a live children
listing came to sit empty forever with no error and no completion.

Every such bridge is now gone from `src/`, `tools/`, `samples/` and `clients/`. Waits that must
still produce a `Task` — an ASP.NET endpoint, a silo lifecycle hook, the I/O pool's own
reactive-leaf adapter — go through `ReactiveCompletion.ObserveCompletion`, which queues the
caller's continuation instead of running it on the producer's thread and keeps its error arm
attached so a late fault is reported rather than orphaned.

The shared test helpers moved with them: the request/response wait the whole suite runs
(`MonolithMeshTestBase.AwaitResponseAsync`), the fluent stream assertions, the permission waits and
the storage-adapter helpers. The old guidance said tests were the one sanctioned place to bridge;
that was never true, because an assertion is the last thing a test awaits before it tears the mesh
down, so the scheduler it resumes on is the scheduler the teardown runs on.

A new guard holds the line: `src/` and friends are at zero **with no allow-list at all**, while the
trees still being swept carry an inventory that may only shrink. The full account — what to write
at each kind of boundary, the conversion traps, and how the guard proves it is not passing on an
empty scan — is in
[Removing Observable-to-Task Bridges](/Doc/Architecture/RemovingObservableToTaskBridges).
