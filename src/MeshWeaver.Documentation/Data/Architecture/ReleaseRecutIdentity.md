---
Name: Release Re-Cut Identity
Category: Architecture
Description: A release is identified by the settle that owns it, not by the second an attempt runs in — otherwise the post-condition's re-cut duplicates its own first attempt, or collides with it and leaves the type advertising a build no release names.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7"/><polyline points="21 3 21 9 15 9"/><circle cx="12" cy="12" r="2.5"/></svg>
---

# Release Re-Cut Identity

**A release belongs to a compile SETTLE, not to the moment an attempt at writing it runs.** The
settle mints one `ReleaseIdentity` before its first attempt; every later attempt at that release —
in practice the post-condition's re-cut — reuses that value verbatim and writes it with an
idempotent upsert. So a retry converges on the node the first attempt addressed, whether or not that
first attempt landed.

That one rule is what makes the re-cut a *retry*. Without it, a retry is a second cut, and the wall
clock decides which of two wrong outcomes you get.

## The two outcomes the wall clock used to pick between

`TryCreateReleaseNode` computed the release id — `{yyyyMMddHHmmss}-{8charContentHash}` — from
`DateTime.UtcNow` **inside its own body**, and wrote it with a create-only verb. The hash is a
function of the build (the durable `Collection/ContentPath`), so two attempts at one settle always
agreed on the suffix and differed only in the timestamp:

| The retry lands in | The minted id | The create-only write | Result |
|---|---|---|---|
| a **later** second | a NEW id | succeeds | **two release nodes for one build** — byte-identical content under two paths |
| the **same** second | the id the first attempt already created | refused, `Node already exists` | the refusal is swallowed to `null`, `LatestReleasePath` is never advanced — **the type advertises a build no release names** |

Both were measured on 2026-09-06 (Systemorph/MeshWeaver#3407). The duplicate on `memex`:
`Hosting/InstanceAction/Release/20260906113905-cgZ9cItJ` and `…113915-cgZ9cItJ`, identical
`assemblyStoreVersion: 769`, identical `assemblyContentPath`, identical source snapshot,
`createdAt` 10.005 s apart — the client-side bound on the first attempt's *observation*, expiring
while its write had already landed 49 ms in. The collision on `memex.localhost`:
`Edu/CourseInvite` compiled to build 767, the re-cut minted `…094912-EsO3j1xI` a second time,
`InvalidOperationException: Node already exists`, and `AgenticOffice/MyExercises` went on serving
the previous assembly with a fix in the new one its reader could not see. A pod restart cleared it;
nothing in the pipeline did.

## Why the release write is re-attempted at all

The re-cut is the remedy half of the post-condition described in
[Node Type Compilation](/Doc/Architecture/NodeTypeCompilation): once a release request has been
CONSUMED, `LatestReleasePath` must never name a build older than `LastCompiledVersion`. The compile
settle checks that from facts all in hand and, on a violation, re-cuts from the bytes the compile
just produced — no recompile, under System.

The trigger for the re-cut is *`newReleasePath` came back `null`*, and that is deliberately a
statement about **observation**, not about the write:

- The release write is bounded client-side so a hung owner can never hold the compile's terminal
  `Status` write (the NodeType would sit at `Compiling`). The bound abandons the *observation*; it
  cancels nothing, so the write it gave up on may still land — and on `memex` it landed in 49 ms.
- A response can also simply not come back, and the request-level bound is the hub's
  `RequestTimeout`, enforced where the request lives.

So "the outcome was not observed" is a normal, recurring state, and the retry that answers it has to
be **idempotent by construction** rather than merely lucky. That is the whole design constraint.

## The shape

```csharp
// At the settle — ONCE, before the first attempt.
var releaseIdentity = NodeTypeBuildState.MintReleaseIdentity(outcome.Result!);

// The first attempt.
NodeTypeBuildState.TryCreateReleaseNode(
    hub, hubPath, outcome.Result!, outcome.PendingNode, activityPath, releaseIdentity, logger)
    // …and, when its outcome was not observed, the post-condition's re-cut — SAME identity.
    .SelectMany(newReleasePath => ReleasePostCondition.Restore(
        hub, hubPath, outcome.Result!, outcome.PendingNode,
        activityPath, newReleasePath, releaseIdentity, logger))
```

`ReleaseIdentity` carries three values, and all three are minted together on purpose:

| Field | What it is | Why it is minted with the others |
|---|---|---|
| `Version` | `{yyyyMMddHHmmss}-{hash}` — the node's `Id` and the last path segment | the id a retry must reuse |
| `Hash` | the 8-char content hash alone → `NodeTypeRelease.Release` | derived from the durable assembly coordinates, so it is stable across silos |
| `CreatedAt` | → `NodeTypeRelease.CreatedAt` | a retry then writes **byte-identical** content, so the owner's no-op upsert guard adopts without bumping the node's version |

The write is `IMeshService.CreateOrUpdateNode` — the framework's own answer to this exact problem
("for idempotent writes that may run concurrently or re-run … NOT a client-side
`CreateNode().Catch(already-exists → UpdateNode())`", see
[Data Access Patterns](/Doc/Architecture/DataAccessPatterns)). The OWNER decides create-vs-update by
existence and serialises both branches, so the retry is race-free even against its own first attempt
still in flight. Attribution is unchanged: the upsert's create branch pins the caller onto the inner
`CreateNodeRequest.CreatedBy`.

## What this does NOT do

- **It does not check whether the release exists before writing.** A point read of a node that may
  not exist is a framework defect in its own right — it faults the read stream and opens the storm
  breaker on that path, which fast-fails *writes* too. Skipping the check and using the upsert is
  the rule, not an optimisation ([CQRS](/Doc/Architecture/CqrsAndContentAccess)).
- **It does not catch "already exists" and call it success.** That would be the same
  exception-as-control-flow the upsert exists to replace, and it would still leave the duplicate
  half of the defect standing.
- **It does not remove the client-side bound on the release write.** That bound is why the
  observation can be lost at all, and a client-side ceiling on a mesh write is a shape core
  deliberately removed from `MeshService` (#1270: *"a client-side ceiling makes the CALLER abandon a
  write that is still running"*). It survives here for a stated reason — the terminal `Status` write
  runs in this observable's `OnNext`, so a hung owner would wedge the NodeType at `Compiling` — and
  after this change its failure mode is benign: the retry it provokes adopts rather than duplicates
  or collides. Removing it is a separate call about that wedge risk, not about data loss.

## Convergence is per identity, not per NodeType

A NodeType publishes many releases, and each build gets its own. The identity hashes the build's
durable assembly coordinates, so two builds are two identities and two release nodes — the
convergence above binds only the attempts at *one* identity. `ReleaseRecutIsARetryTest` pins both
halves: a re-cut converges on its own first attempt, **and** a different build still gets its own
release. The second is the falsifying arm — a "fix" that stapled every attempt to one path would
satisfy the first assertion while making a second release impossible.

## Related

- [Node Type Compilation](/Doc/Architecture/NodeTypeCompilation) — the compile settle and the
  release post-condition it carries
- [Plugin Packaging](/Doc/Architecture/PluginPackaging) — what the `Release` node records and how a
  consumer resolves bytes from it
- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — why existence is not
  a query, and why the create-or-update verb is the answer
- [Data Access Patterns](/Doc/Architecture/DataAccessPatterns) — the write surfaces and which verb
  each operation takes
