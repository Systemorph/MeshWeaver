---
nodeType: Markdown
name: Candidate Release Protocol
category: Architecture
description: How a new version of a module — including the framework itself — is verified against every dependent BEFORE it is promoted, so a removed API can never again reach production as a runtime compile failure.
---

# Candidate Release Protocol

A module does not get to declare itself released. It builds itself, publishes the build as a
**candidate**, and every dependent builds against that candidate. Only when the whole closure
compiles does the candidate become a release. Anything less is published as a **preview**, carrying
the complete list of what it broke.

This page specifies that protocol: the states, the failure semantics, and the two places it has to
be enforced.

## Why — the gap it closes

The framework deleted an extension method (`AddTracking` on `MessageHubConfiguration`; tracked
changes now derive from history). Three NodeTypes in a plugin still called it. Every check that
exists today was green:

| Gate | Why it passed |
|---|---|
| `dotnet build -c Release -warnaserror` | Node source is `<None>` content — the compiler never sees it |
| The full test suite | Same reason; no test compiles in-mesh source |
| The plugin repo's own `Compile every NodeType (vs core)` | It compiles against a **pinned** core digest that still had the method |
| Plugin CI triggers (`push`/`pull_request`/`workflow_dispatch`) | Only fire when the **plugin** changes — never when **core** does |

So the break was invisible in both repos at once, and surfaced only when a portal started on the new
core: `CompileError` → dependents `UpstreamFailed` → `REFUSING READINESS` → the instance hub never
activates → every request burns the full 60 s activation budget → hung pages and failed probes.

**The pin is not the bug — it is a deliberate, correct decision** (a moving `:latest` makes two runs
of identical code disagree). The bug is that nothing re-runs a dependent's build when the thing it is
pinned to moves. That is exactly what this protocol adds.

## The protocol

1. **Build self.** The module compiles its own sources. On failure it reports the diagnostics to the
   requester and stops — no candidate is published.
2. **Publish a candidate.** On success it records the built assembly and announces the version as
   **Requested**, carrying a reference to the built code. A candidate is a real, addressable build;
   it is simply not the current release.
3. **Dependents build against the candidate.** Every direct dependent starts its own run at step 1,
   resolving this module to the candidate rather than to the last release. Recursively.
4. **Promote only on a clean closure.** If every transitive dependent reports success, the candidate
   becomes the release. If any fail, the candidate does **not** promote, and the result names every
   failure — the walk continues past the first one, the way a build reports all errors rather than
   the first.
5. **A failed closure still yields a preview.** The candidate is retained and published as a
   *preview* with the failure list attached, so the author can inspect and iterate without rebuilding
   from scratch.

### State

A version is in exactly one of:

| State | Meaning | Instances adopt it |
|---|---|---|
| `Building` | self-build in flight | no |
| `Candidate` | self-build succeeded; closure unverified | no |
| `Released` | closure verified clean | yes — becomes `LatestReleasePath` |
| `Preview` | self-build succeeded, closure did not | only via explicit `RequestedReleasePath` pin |
| `Failed` | self-build failed; no artifact | no |

`Released` and `Preview` are both terminal and both keep their artifact. Only `Released` moves the
pointer instances follow, so promotion is a single pointer write, not a rebuild.

## Failure semantics — what "collect all errors" can and cannot mean

**Breadth-complete, depth-stopped.** Every sibling at a level is attempted, so one broken dependent
never hides another. But a dependent of a module that produced *no assembly* cannot be compiled at
all — reporting speculative errors for it would be fiction. It is reported as blocked, naming its
blocker.

This distinction is not academic. In the incident, `SocialMedia/Post`, `Profile` and `PostsHub` each
carried the identical `.AddTracking()` call. Only `Post` was ever reported; the other two were
recorded as `UpstreamFailed: blocked by SocialMedia/Post`, so **two of the three bugs were
invisible** until the first was fixed. Under this protocol all three surface in one run, because they
are siblings in the dependency order — none is downstream of another.

The report distinguishes three outcomes, and a run is only clean when the failed and blocked sets are
both empty:

- `Compiled` — built against the candidate.
- `Failed` — attempted, with its own diagnostics.
- `Blocked` — not attempted, naming the upstream that produced no assembly.

## Design decisions

**Cycles.** `Store/Coupon`, `Store/Order` and `Store/Plugin` form a genuine source cycle today
(they share sources through cross-type `sources` entries). A cycle is compiled as **one unit** — a
single compilation containing every member's sources — and promotes or fails atomically. Rejecting
cycles is not an option; they already exist and are legitimate.

**Concurrent version requests.** Two modules each verifying against the other's *previous* release
can both come back clean and still be jointly broken. Candidates are therefore versioned as a
**set** — a release train. A dependent resolves every module in the train to its candidate, and the
train promotes atomically. Where trains would overlap, they serialise per dependency closure.

**Cross-partition dependents.** A module's dependents may live in partitions the publisher cannot
read. The closure walk runs as System so it is complete, but the report is filtered to the caller:
full diagnostics for paths they may read, aggregate counts for the rest. Completeness of the *gate*
must never leak the contents of a partition.

**Cost.** A full cold sweep of 232 NodeTypes takes about ten minutes. This protocol rebuilds only the
transitive closure of actual dependents, runs independent topological *levels* in parallel rather
than strictly serially, and caches on `(source hash, framework version, upstream candidate ids)`.
Anything it bounds — a truncated closure, a skipped level — is logged explicitly; a silent cap reads
as "verified everything" when it did not.

## Where it is enforced

The framework ships as a container image, not as a mesh module, so it cannot enter the protocol from
inside a portal. It enters at the release boundary instead. Two gates, in the order a change meets
them:

**1. Core release → dependents (the gate that was missing).** When core publishes an image, that
image is a **candidate**. Each node repo's `Compile every NodeType (vs core)` runs against the
candidate digest — not its pinned one — and every result must be `Compiled` before the image is
promoted to the tag that portals self-update from. A failure publishes the image as a preview and
names every broken NodeType across every repo. Mechanically: a dispatch from core CD to each node
repo carrying the candidate digest, an accepted digest override in the repo's workflow, and results
aggregated back into the promotion decision.

**2. Node repo PR → pinned core (the gate that already exists).** Unchanged, and still pinned: a
plugin PR must not go red because core moved underneath it. Pin bumps stay deliberate — gate 1 is
what proves the bump is safe.

Neither gate replaces the other. Gate 2 protects plugin authors from core; gate 1 protects core — and
production — from plugins.

## What this does not do

It verifies that dependents **compile**. It does not verify they still behave correctly; a signature
that survives with changed semantics passes this gate. Behavioural compatibility remains the job of
each module's own tests, run in its own repo against the candidate as part of step 3.

It also assumes the dependency graph is discoverable from declared sources. A module reaching another
module's types through a path the graph cannot see is invisible to the closure walk — which is
another reason `sources` entries are a contract, not a convenience.

## Related

- [NodeType Compilation & Releases](/Doc/Architecture/NodeTypeCompilation) — the runtime side:
  triggers, `HasUsableBuild`, framework-version freezing, where releases live.
- [Deployment](/Doc/Architecture/Deployment) — how a promoted image reaches the portals.
