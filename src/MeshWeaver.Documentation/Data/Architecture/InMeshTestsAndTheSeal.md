---
Name: In-Mesh Tests and the Seal
Category: Architecture
Description: A live Tests area that no REQUIRED context executes is a latent trunk red, and the seal is where it detonates fleet-wide — plus the upstream double-judging fix that is already in place, and how to measure a gate before you require it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>
---

# In-Mesh Tests and the Seal

**A `Tests` layout area is real, executable code that no `dotnet build` and no `dotnet test` ever
touches.** It runs in exactly one place — the node-repo gate — and if that gate is not a *required*
context, the test's first execution is on `main`. From there it does not merely sit red: the
**seal** re-runs it on every platform delivery, so one red in-mesh test holds the entire fleet.

## 🚨 The one sentence

**A test that no required context executes is not a test — it is a latent trunk red with a fleet-wide blast radius.**

## The failure mode, measured

The canonical instance is 2026-09-03 (MeshWeaver.Plugins #1213 → #1224 → #1225, core #3153 → #3164).

1. **A live in-mesh test lands.** Plugins #1213 added `CouponKeyTests.CouponWithATier_LandsAnActiveSubscription`
   to `Store/Licensing/Test/LicensingTestsArea.cs`. The compile-check gate proved it *compiles*.
   Nothing proved it *passes*: `test-repos` — the only lane that executes a `Tests` area — was not
   among the repo's required contexts.
2. **It merges, and fails on first execution.** The gate's verdict on `main`:

   ```text
   RED Store/Licensing: compile=Ok render=ok tests=FAILED
   ```

   The defect was genuine and in two halves: `RequestActivation`'s CREATE branch filed a
   control-plane request and never woke the owner, while the PATCH branch worked because
   `GetMeshNodeStream().Update()` point-reads and activates as a side effect.
3. **The seal detonates.** Every core CD run then failed at the Plugins publication:

   ```text
   GATE FAILED — install: Hosting; tests: Store/Licensing — 2 new failure(s), 0 stale allow entr(ies).
   ```

   **Six core CD runs failed this way between 01:03Z and 05:53Z** — the job
   `Plugins: bake + seal the publication for this identity`. No seal means no publication, and a
   satellite whose upstream is unsealed exits without building. Delivery was blocked ~5 hours by one
   test that had never been executed before it merged.

The shape to recognise: **the gate worked perfectly.** It caught the defect on the first run that
executed it. What failed is that nothing made merging *wait* for it.

## 🚨 Upstream packages are already installed-never-judged — do not rebuild this

A natural reading of the incident is "the seal re-runs upstream tests, so one red upstream holds
everyone; stop re-running them." **That fix is already implemented, and re-implementing it would
change nothing.** A package that arrives through the seed is installed so its NodeTypes register and
its assemblies adopt — and is then excluded from every verdict:

```csharp
// An upstream package is INSTALLED, not gated: its types register and its
// assemblies adopt from the seed, but compile/render/Tests verdicts belong to
// the repo that owns it - running them here would double-judge every upstream
// on every satellite and let an upstream flake red a repo that changed nothing.
var types = upstream
    ? (IReadOnlyList<NodeTypeUnderTest>)[]
    : DiscoverNodeTypes(package, files);
```

`PluginGateRunner.TestPackage` hands an upstream package an **empty type list**, so it is never
compiled, rendered or tested here; `BakeOutput.Persist` likewise drops it, so one repo can never
republish another's bytes under its own name. The discriminator lives in `SeedPackages.Materialize`:
a bundle whose package id already has a top-level folder in the repo snapshot is *the repo's own*
module and is judged from its tree; everything else came from an upstream publication.

The guard is pinned by `GateInstallsUpstreamPackagesTest.TheSatelliteInstalls_BecauseTheGateInstalledItsUpstream`,
which asserts all three claims — the upstream installs, `Assert.Empty(upstream.NodeTypes)`, and its
bundle is absent from the gate's own bake output.

**So a satellite is not exposed to its upstream's test results at all.** Education, Reinsurance,
SocialMedia and Manufacturing consume the sealed `plugins` publication and never re-run its tests.

### Why the seal still runs Store's tests, and must

Core CD's `plugins-bake` job declares `upstream-sources: ''` — **Plugins is a root there, staged as
its own content, not seeded as an upstream.** Store/Licensing is therefore the *stage*, and the gate
judges it exactly as it judges every other package in the repo being baked.

That is not redundant with the Plugins PR gate, and must not be deduplicated away:

| | Plugins' own `test-repos` | Core CD's `plugins-bake` seal |
|---|---|---|
| Reference set | a **pinned** platform image digest | **this run's promoted** portal image |
| Question answered | does this content work against the pinned framework? | does it work against the framework we are about to ship? |

The two measure different things. **A publication must never be sealed against a framework identity
its content has not been shown to work on** — that is the whole point of the seal, and the reason a
red there correctly refuses to publish. The seal is the last line of defence, not the bug.

## Measuring a gate before you require it

Requiring a context that is red or flaky converts an unenforced gate into a blocked trunk. But the
naive measurement — the workflow's own conclusion — answers the wrong question, because a run is red
if *any* job failed. Measure the **specific job**, then establish the **identity** of each failure.

```bash
# per-job conclusions across recent main runs, not the run-level colour
gh run list --repo Systemorph/<repo> --workflow ci.yml --branch main --limit 30 \
  --json databaseId,event --jq '.[] | select(.event=="push") | .databaseId' \
| while read id; do
    gh api "repos/Systemorph/<repo>/actions/runs/$id/jobs?per_page=100" \
      --jq '.jobs[] | "\(.conclusion)\t\(.name)"'
  done
```

Measured this way on MeshWeaver.Plugins `main`, 28 push runs to 2026-09-03T08:03Z:

| Context | pass | fail |
|---|---|---|
| `test-repos / Compile + render node repos (MeshWeaver from ACR)` | 8 | **20** |
| `Tests-area ratchet over the gate log` | 26 | 2 |
| `The Tests-area gate's inputs are present` | 26 | 2 |
| `Every gate executed` | **28** | **0** |

A 28.6% pass rate reads as "far too flaky to require" — and that reading is **wrong**. Classifying
the 20 failures by their annotation and correlating them against the other jobs:

- **18 were real content verdicts.** Seventeen form one uninterrupted streak from the run that
  merged #1213 to the run that merged its fix — the gate reporting the same true defect every time.
- **2 were the input assertion firing correctly.** Both land on exactly the runs where
  `The Tests-area gate's inputs are present` also failed: the module bundles the gate composes were
  not produced, so the gate red-failed naming that rather than measuring nothing.
- **0 were unexplained or infrastructure flakes.**

**The pass rate was low because the trunk was broken, not because the gate is unreliable.** A gate
with zero unexplained failures in 28 runs is a *good* candidate to require — the low number is the
gate doing its job on a repo that needed it. This is the general lesson: **a raw pass rate is not a
reliability measurement until every failure has an identity.** Requiring the gate would not have
wedged the repo; it would have prevented the merge that broke it.

## 🚨 An aggregate that only detects skips cannot report a red

The same measurement exposes a second trap. `Every gate executed` passed **28 of 28** while the gate
it aggregates was failing 20 times. It is honest about its narrow job — it asserts that no gate
`skipped`, because a skipped required context
[counts as satisfied](/Doc/Architecture/ReadingCiSignals) — but it says nothing about a gate that
ran and *failed*.

An aggregate job is therefore **not** a substitute for requiring the gate itself. It closes the
skip hole; the failure hole is closed only by the gate's own context being required, or by the
aggregate also asserting `result == 'success'` for each need. A wall of ticks that includes an
aggregate which cannot go red is the same defect as a gate that skips on missing input.

## The lever

Three of the four satellites already require the gate that executes their Tests areas:

| Repo | requires `test-repos / Compile + render node repos (MeshWeaver from ACR)` |
|---|---|
| MeshWeaver.Reinsurance | yes |
| MeshWeaver.SocialMedia | yes |
| MeshWeaver.Manufacturing | yes |
| MeshWeaver.Plugins | **no** |

The repo carrying the largest module surface — and the one whose publication every other repo's
delivery waits on — is the one that does not require it. **Adding
`test-repos / Compile + render node repos (MeshWeaver from ACR)` to the Plugins required set is the
fix for this failure mode**, and the honest order is the one the record already states: confirm the
lane is green, then arm.

Restructuring the in-mesh Tests areas into an already-required lane is *not* an alternative. The
gate needs a real mesh, the composed module bundles and the platform image; moving it into
`Build + test the portal hosts` would either duplicate that expensive setup or re-run suites twice,
against the standing cost directive that a suite runs once per PR and consumes the platform image.

## See also

- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — why a skipped or absent required context counts as satisfied
- [The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — all-or-nothing publication
- [Node Type Compilation](/Doc/Architecture/NodeTypeCompilation) — the in-mesh source no `dotnet build` sees
- [Writing Tests](/Doc/Architecture/WritingTests) — the house standards for the suites CI does run
