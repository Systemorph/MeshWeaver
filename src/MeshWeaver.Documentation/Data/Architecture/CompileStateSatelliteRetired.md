---
Name: The Compile-State Satellite Is Retired
Category: Architecture
Description: A phase-1 dual-write that nobody ever read cost one node-operation round trip per NodeType activation and one grain activation per NodeType whenever its compile state moved. After the compile lane moved off the ThreadPool, those satellites were still the destination of boot-time placement timeouts, stalled activations and routing back-pressure legs. The write was removed, not batched.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="m9 9 6 6"/><path d="m15 9-6 6"/></svg>
---

# The Compile-State Satellite Is Retired

Issue #748 planned to move a NodeType's operational compile state off the authored node in three
phases. Phase 1 (#752) added a **dual-write**: `NodeTypeCompileStateMirror`, installed on every
per-node hub by `MeshDataSource`, copied the operational members onto a fixed-id satellite at
`{type}/_Activity/compile-state`. Phase 2 was meant to move readers onto that satellite, and
phase 3 to stop the writes to the node. **Phase 2 never happened.** No code in the platform, in
MeshWeaver.Plugins or in the sample trees reads the satellite. Every compile gate, view and tool
reads the NodeType node itself (`Store/Publishing`'s `ReadCompileState` included). #748 was
closed with the mirror still running.

## What the write cost

- **One node-operation round trip per NodeType activation.** The mirror wrote the first state it
  observed without checking it first. That meant a `CreateOrUpdateNodeRequest` through the
  node-operation hub every time the type's hub activated, including when the write turned out to
  change nothing.
- **One extra grain activation per NodeType whenever the state moved.** A real update is applied
  through the satellite's own per-node hub, so each changed satellite activated a grain: placement,
  path resolution and a storage read. A framework roll re-stamps every type, so on memex-cloud a
  roll meant about 371 of these activations. memex-cloud had nine rolls and restarts between
  12:05Z and 14:17Z on 2026-09-23.

## What it looked like in production, after #5327 / #5375

The compile lane moved off the shared ThreadPool (#5327, #5375), and both portals were running
images that contain those fixes. The satellites were still where the boot-time bursts landed:

| instrument | reading (memex-cloud, 2026-09-23) |
|---|---|
| Placement timeouts, `Admin/_LogIncident/f7d8f9982b20cce5` (#5334) | 9 of the 10 retained samples at 14:22Z target `messagehub/*/_Activity/compile-state` |
| Activation faults on a stalled path resolution, `Admin/_LogIncident/df1ef8b39a2d8601` (#5531) | 6 of the 7 retained samples at 13:33–13:35Z are compile-state satellites |
| Routing back-pressure crossings filed after 10:00Z (51 parsed from issue bodies) | the second most common family of the **oldest** in-flight leg: 8 of 51, behind `cache/*` at 15 |

MeshWeaver.Plugins' gate measured the same writes earlier, as the per-package nodeops tax (#4141,
folded into #2543). Its open question was whether a package's N satellite writes needed to be N
serialised round trips. The answer is that none of them are needed.

## The decision: remove it, don't batch it

Batching would have kept a write that has no reader. So `MeshDataSource` no longer installs the
mirror, and `NodeTypeCompileStateMirror.Install` is `[Obsolete]` rather than deleted. A caller in
in-mesh source is invisible to every CI build, so deleting the method would break that caller at
runtime compile. The value shape (`StatePath`, `StateNode`, `Parse`) stays, so satellites that
already exist can still be read. They are now inert records.

**Pinned by** `NodeTypeCompileStateMirrorTest.NodeTypeActivationAndStateChange_WriteNoCompileStateSatellite`
(`test/MeshWeaver.Graph.Test`). The test runs on a real mesh with a positive control on each side:

- the state change does land on the NodeType node;
- the same reader does see a satellite that was written directly.

It then asserts that no satellite appears for the activated type. As a negative control, the test
was run with the `Install` call put back into `MeshDataSource`. It failed on that assertion.

## What this does NOT establish

- The share of the back-pressure bursts this removes. Only the latest target and the oldest leg of
  each crossing are logged. The biggest family, `cache/*` (the node-stream cache multiplexer), and
  the per-user `_Install` / `_Access` / `_Entitlements` legs are untouched by this change.
- Whether placement timeouts at a roll also need directory churn from a departing silo. The 14:22Z
  burst was five minutes after the roll to `3.0.0-ci.9259`.

See [Reading a Routing Saturation Report](../ReadingARoutingSaturationReport) and
[Compiling Off the ThreadPool](../CompileOffTheThreadPool).
