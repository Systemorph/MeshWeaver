---
Name: Guidance no longer teaches the forbidden Rx-to-Task bridge
Category: Fix
Description: Documentation, agent skills and the automatic code-review instructions stopped prescribing the `.ToTask()` bridge that the platform forbids — including the copy-paste script examples an agent pastes into a mesh node — and a new ratchet keeps a C# example from reintroducing it.
Icon: ShieldError
Order: -20260830
---

# Guidance no longer teaches the forbidden Rx-to-Task bridge

MeshWeaver is `IObservable<T>` end to end, and the Rx observable-to-`Task` bridge is forbidden
everywhere — tests included, as of the 30 August ruling that retracted the older "tests are the one
sanctioned place" carve-out. Rx completes its `TaskCompletionSource` without
`RunContinuationsAsynchronously`, so it resumes the awaiter *inline on the thread that signalled*,
which in the mesh is a hub's action block or a grain's turn scheduler.

The rule was being enforced in source while the *guidance* still prescribed the shape. That is the
worse half: a page that presents the bridge as the right thing to write generates new violations
faster than any sweep removes them, and no code scanner ever sees a markdown file. Two categories
mattered most. `.github/copilot-instructions.md` drives the automatic review on every pull request
here, and it listed the bridge as "the accepted bridge" in test projects — so review was actively
defending the banned shape. And the `.csx` script examples in the script-execution and
activity-control-plane pages are pasted into mesh nodes, which compile at *runtime* in the portal,
where no build and no test can catch them.

Every prescriptive occurrence is converted. Where the signature is ours the example now returns
`IObservable<T>` and subscribes; where a foreign signature genuinely hands back a `Task` it uses
`ReactiveCompletion.ObserveCompletion`, which completes asynchronously and keeps its error arm
attached for a fault arriving after the wait settled; and tests assert through the reactive
assertion surface, which owns the wait. The pages that *warn* against the shape — the war stories,
the anti-pattern blocks, the "delete on sight" checklists — are untouched on purpose: a rule stated
without the shape it forbids is unrecognisable in review.

A new ratchet keeps it that way. It counts the shape only inside a fenced C# code block, so prose
warning against it stays free, and its seeded inventory can only shrink.
