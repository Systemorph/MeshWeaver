---
nodeType: Markdown
name: Candidate Release Protocol
category: Architecture
description: How a new version is verified against what actually depends on it — inside the mesh a module verifies its dependents before promoting, and for the framework the gate sits at the DEPLOY boundary, where a portal refuses an image its own installed plugins cannot compile.
---

# Candidate Release Protocol

**Releases are fast; deploys are gated.** A new version ships as soon as it is ready. What is
verified — and where — depends on who can answer the question.

Inside the mesh, a module can name its own dependents, so it verifies them: it builds itself,
publishes the build as a **candidate**, and every dependent builds against that candidate. A clean
closure promotes it to a release; a broken one publishes a **preview** carrying the complete list of
what it broke.

The framework cannot do that, and should not try. It ships as a container image, it has no
enumerable set of dependents, and holding its release until every downstream repo was rebuilt would
slow the train while still answering the wrong question. **The set that matters is per-instance —
the plugins a given portal actually installed** — so for the framework the gate sits at the *deploy*
boundary: a portal verifies a candidate image against its own installed plugins, and refuses to
adopt one they cannot compile.

This page specifies both: the states, the failure semantics, and where each gate lives.

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

## Where it is enforced — the gate is on the INSTANCE, not the release

**Core releases immediately.** It does not wait on its dependents, does not fan out to node repos,
and does not need to know who depends on it. Holding a release until every downstream repo has been
rebuilt would slow the release train, require cross-repo credentials, and still not answer the only
question that matters — because *no* central set of dependents is the right set.

**The right set is per-instance: the plugins THAT portal actually has installed.** A NodeType broken
on a plugin nobody deployed is not an outage; a NodeType broken on a plugin one portal installed is
an outage for exactly that portal. Only the instance knows its own set.

So the gate moves to the deploy boundary:

> Before a portal **adopts** a candidate image, that exact combination — the candidate image × the
> module versions THAT instance has installed — is built and its tests run. Every module must build
> and every test must be green for **that very combo**. If anything fails, the instance does not
> roll: it keeps serving the image it is on and reports what broke.

This is a *deploy* gate, not a release gate. A broken candidate stops at whichever instances it would
break and rolls everywhere else.

### The unit of verification is the COMBO, and it includes TESTS

Two things are easy to get subtly wrong here, and both were wrong in earlier drafts of this page.

**It is a combo, not a repo.** A node repo's CI verifies *that repo at HEAD* against *a pinned image*.
Neither factor matches production: an instance runs a specific image and a specific, cross-repo set of
module **versions**, which no single repo can see. `PackageManifest` already pins exactly what is
needed per instance — `Id`, `Version`, `ModuleVersion`, `Source`/`SourceFolder`, `Requires` — so the
combo is fully identifiable from the instance's own install records.

**It is build AND tests, not compilation.** A NodeType that compiles can still be broken: a signature
survives while its behaviour changes. "Green" means every module builds, every default area renders,
and every module's `Tests` area passes — **for that combo**.

That is not a new harness. `mw-plugin-test` (`tools/MeshWeaver.PluginTester`) already does precisely
this: it boots a fresh in-process mesh, installs each package, waits for every NodeType to reach a
terminal `CompilationStatus` (printing Roslyn diagnostics on error), renders each type's default
area, and **executes each type's `Tests` layout area — a red test fails the run**. Exit 0 = all green.

**What is missing is only the combo assembler.** `mw-plugin-test` today takes a *repo root* — every
module in one checkout, at HEAD. The gate needs it pointed at *an instance's set*: read that
instance's install records, materialise each module at its recorded `ModuleVersion` (the node repos
already tag per module, e.g. `Store/v1.0.15`), assemble that set, and run the tool inside the
**candidate** image. Same executable, different input.

### 🚨 Blocker: an instance does not currently RECORD its combo

The gate cannot verify a combination the instance cannot state. Measured on memex-cloud:

- **The module SET is knowable** — every installed module is a top-level `Store/Plugin` node
  (`nodeType:Store/Plugin`, 40+ of them).
- **The module VERSIONS are NOT.** `Plugins/*` — the partition `PackageInstaller` writes its
  `Package` records into (`InstalledPartition = "Plugins"`, `PackageNodeType = "Package"`) — contains
  only `_Policy`. There are no install records, so nothing pins `ModuleVersion` per module. The
  plugin root node's `PluginContent` carries no version either.

The reason is that these modules arrived by **GitSync / repo import**, not through `PackageInstaller`.
Only the installer path writes a `PackageManifest` record; the sync path does not. So on this portal
"which version of each module am I running" has no answer on the instance.

**Consequence for implementation order.** Building the combo assembler first would produce a gate that
verifies *the latest of each repo* — which is emphatically NOT the instance's combo, and would give
exactly the false confidence this whole design exists to remove. The first task is therefore to make
**every** path that lands a module on an instance record what it landed: module id + `ModuleVersion` +
source ref, in one place, whether it came from the installer or from sync.

Only then is the combo identifiable, and only then is verifying it meaningful.

**Therefore the surge pod is NOT sufficient on its own.** `DynamicTypePreWarmer` compiles this
instance's NodeTypes on the candidate image, which is the right *scope* — but it is compile-only and
it runs after the pod is already up. It is a good last line; it is not the combo check, and it must
not be mistaken for one.

### Most of this already exists — and it fired too late

`DynamicTypePreWarmer` already computes exactly the required signal: it captures a `WasHealthy`
baseline before baking, then refuses readiness for any NodeType that regressed on the new image —

```
REFUSING READINESS — N NodeType(s) regressed on this image.
The rollout will stall with the previous image still serving.
```

That sentence is the intent. **It is also, today, a lie** — and that is the whole defect.

**The gate is not armed.** The health check that consumes the regression state is registered only
when `PreWarm:GateReadiness` is true. On all three portals it is **false**. So the sweep runs, records
every regression into `NodeTypeBakeGateState` — and nothing reads it. The gate *state* is registered
unconditionally, so the `REFUSING READINESS` line fires regardless, while the pod goes Ready and takes
traffic. Anyone reading the pod log believes they were protected. That single misleading line is why
the outage looked inexplicable from the logs.

**Why it was switched off, and why that reason expired the next day:**

| | |
|---|---|
| `563019ee6` (2026-08-03) | *"Revert `PreWarm__GateReadiness` to off"* — the first gated roll stalled on "7 NodeType(s) regressed" with **zero** compiler diagnostics: all cross-silo `SubscribeRequest` timeouts, i.e. false regressions. |
| `974016bf4` (2026-08-04) | *"Bake gate: a timeout is not a regression"* — `MarkOutcome` routes `TimedOut` to *unevaluated*; only `CompileError`/`UpstreamFailed` on a previously-healthy type sets `Regressed`. |
| — | The config was **never turned back on**, and the "gate OFF" rationale in `values.aks.yaml` still argues from the pre-fix code. |

**The rollout shape is mostly NOT the problem.** It is tempting to blame single-replica deployments;
that is wrong for two of the three portals. `maxSurge: 1 / maxUnavailable: 0` is surge-first —
Kubernetes creates the new pod and keeps the old one serving until the new one passes its probes, and
never deletes first. While the startup probe fails, readiness is suspended and the surge pod stays out
of the Service. **A 1-replica portal is fully protected by readiness refusal — provided the check is
registered.**

🚨 **But the strategy is not uniform, and one portal is genuinely unsafe.** Measured live:

| namespace | maxSurge | maxUnavailable | surge-first? |
|---|---|---|---|
| memex-cloud | 1 | **0** | yes |
| atioz | 1 | **0** | yes |
| **memex** | 1 | **1** | **NO** |

With `maxUnavailable: 1` at `replicas: 1`, Kubernetes may delete the only serving pod before the
replacement is ready — so on that portal readiness refusal protects nothing even once the gate is
armed, and any slow start is a hard outage. Arming the gate without first setting
`maxUnavailable: 0` there would create false confidence. Fix the strategy and the gate together.

**The surge pod is the LAST line, not the gate.** Adoption is not the image patch; adoption is when
traffic moves, and readiness controls that — so a surge pod that never joins the Service does contain
the damage. But it only *compiles*, it runs no module tests, and it discovers the problem by failing
in production rather than before shipping. Treat it as the backstop that catches what the combo check
missed, never as the check itself.

The combo check therefore runs **before** the image is offered to that instance at all, off-cluster,
in the candidate image — not in the self-update poller. The poller patches the image and cannot know
compatibility without running the candidate's assemblies: a framework-identity change invalidates the
whole assembly cache by design, and `UpdatePolicyContent` carries no declared-compatibility metadata
to evaluate instead. A check there would be a guess; the combo run is an answer.

### What actually has to change

1. **Arm it** — `PreWarm:GateReadiness = true`, and replace the stale rationale with the
   post-`974016bf4` reasoning. The paired startup budget is already correct.
2. **`progressDeadlineSeconds` ≥ the startup budget.** It is unset, so Kubernetes defaults to 600 s
   against a 3 h startup budget: a legitimately-baking pod reports `ProgressDeadlineExceeded` after
   ten minutes and a healthy long bake reads as a failed rollout.
3. **Make the log honest.** When the health check is not registered it must say *"gate not armed:
   this pod WILL take traffic with N regressed types"* — never claim a stall it cannot enforce.
4. **Report the verdict where an admin looks.** A blocked upgrade is currently invisible: the admin
   tab shows "update available" forever, the poller re-patches the same tag every 20 minutes (a no-op),
   and the only evidence lives in the log and `/health` of a pod that never becomes Ready — the
   hardest place to look. The surge pod's verdict must land on `Admin/UpdatePolicy` as "cannot update
   to X — these installed types do not compile against it".

**Known boundary:** the sweep covers dynamic NodeTypes only. A break in a non-NodeType surface — a
standalone script, a layout area — is not swept and this gate will not catch it.

### The node repo's own pin stays

A node repo's `Compile every NodeType (vs core)` remains pinned to a fixed core digest. A plugin PR
must not go red because core moved underneath it, and a moving `:latest` makes two runs of identical
code disagree. Pin bumps stay deliberate — the instance gate is what makes an unsafe bump harmless,
because a portal that cannot compile its own plugins simply never adopts it.

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
