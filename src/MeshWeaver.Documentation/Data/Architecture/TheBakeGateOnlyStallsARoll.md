---
Name: The Bake Gate Only Stalls a Roll
Category: Architecture
Description: Why the NodeType bake readiness gate took memex.systemorph.com fully down, and the two rules that now make a refusal possible only on an image that is newer than the build it would be protecting.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l8 4v5c0 5-3.5 8-8 9-4.5-1-8-4-8-9V7z"/><path d="M9 12l2 2 4-4"/></svg>
---

The NodeType bake gate (`PreWarm:GateReadiness`, the `nodetype_bake` check on `/health`) refuses
readiness when a type that used to build no longer builds on this image. Its message states the
contract: *"refusing readiness so the rollout stalls with the previous image still serving"*. The
gate is only safe while that clause is true. On 2026-09-25/26 it was false, and the gate took the
whole control instance down ([#5544](https://github.com/Systemorph/MeshWeaver/issues/5544)).

## What happened

After the V58 migration on `memex.systemorph.com`:

- the new `ci.9332` pod refused readiness with `1 NodeType(s) regressed on this image: BinaryClickerV2/BinaryToggle`;
- the last pod of the previous image (`ci.9218`) then restarted and refused ITS OWN readiness with
  `2 regressed: BinaryClickerV2/BinaryToggle, Store/Review`;
- nothing was serving, and nginx answered 503 until the gate was switched off by hand.

`BinaryClickerV2/BinaryToggle` is a demo type whose layout source calls a
`LayoutAreaHost.GetData<T>()` that does not exist (CS1929, [#3883](https://github.com/Systemorph/MeshWeaver/issues/3883)).
It has failed on every image since it was written. It is not a regression of any image.

## Why a type that never built read as "regressed"

Two defects, and each one hid the other.

1. **"Not known to be broken" was read as "working".** `NodeTypeBakeEntry.WasHealthy` is true for
   every state except `PreviouslyBroken`, including `NeverBuilt`. That is correct for what it says,
   because a type nobody has built is not damaged goods. But the regression baseline was built from
   it. A never-built type has nothing to regress from, so it was filed as a regression anyway. The
   first-bake rule ([#4472](https://github.com/Systemorph/MeshWeaver/issues/4472)) covered only the
   case where every type in the report is `NeverBuilt`. One never-built type on an established
   instance was still treated as a regression.
2. **The record could never learn the type was broken.** A failed compile writes an `Error` stamp,
   and that stamp is a [`MeshPublicationGate`](../MeshAdmission) publication. The gate holds it while
   the sweep runs and **discards it when the pod is refused**. So the failure refused the pod, the
   refused pod could not record the failure, and the next pod read the record as `Ok` again. That
   loop is why the record said `Ok` while the compile failed on every boot (#3883), and why #1391
   was closed on a `Not found` and then came back.

## The two rules

The per-type question and the report-level question are both answered in one stamp,
`DynamicTypePreWarmer.BaselineStamp`. The sweep and `BuildProtocolDriver.OutcomesOf` both use it, so
the GO and the gate cannot disagree
([#4496](https://github.com/Systemorph/MeshWeaver/issues/4496)). The answer is carried to the gate
as `PreWarmOutcome.HasRegressionBaseline`. `WasHealthyBeforeBake` keeps its meaning.

**Rule 1: a regression needs a working build to regress FROM, built by another, older image.**
`NodeTypeBakeEntry.IsRegressionBaselineFor(livePlatformVersion)` is true only when:

- the record names a working build (`HadWorkingBuild`: not `NeverBuilt`, not `PreviouslyBroken`), and
- that build was produced by a DIFFERENT platform build that is NOT newer
  (`ProducedByPlatformBuild`, read from `NodeTypeDefinition.CompiledPlatformVersion`).

If this same build produced the working build, the image can build the type, so a failure now
comes from content or the environment. If a NEWER build produced it, this process is the OLD image
of a roll, and refusing it only removes the replicas the rollout falls back on. A record with no
producer stamp keeps the strict reading.

**Rule 2: an image that has already served this mesh never refuses itself.**
`NodeTypeBakeReport.ThisBuildHasServed` is true when some record names a working build that this
very platform build produced. That is proof, not a guess. Such a stamp is a publication, and a
publication is released only once the stamping process was admitted (or on a host that never armed
the gate). A pod that reads it is a restart of a serving image, not a roll candidate, so nothing it
reports may gate. `NodeTypeBakeReport.GateRelevant`, which the witness-unreadable path refuses on,
applies the same two rules.

**Together:** the only pod that can still refuse on a regression is one strictly newer than the
build that produced the working build it failed to reproduce, while no replica of it has been
admitted. The older image's own pods never meet that condition, so the old ReplicaSet can always
come back. Failures that no longer gate are still recorded and named in the health payload
(`WithoutBaseline`). Once the pod is admitted, the held `Error` stamp is released, and the record
finally says `Error`. That ends the loop in defect 2.

## What this does not cover

- **A `Faulted` sweep still refuses readiness** unless `PreWarm:AllowUnprovenBake` is set, and that
  includes a restarted pod of the serving image whose enumeration errors. The pod has no per-type
  evidence, so rule 2 cannot apply. This is a remaining path to a full outage and is not addressed
  here.
- **The startup probe is still the gate's only reader.** The gate is wired to `/health` on the
  startup probe (the chart's `probes.startup.path`). A startup probe answers "has the process
  started", and a failing one KILLS the pod after `failureThreshold × periodSeconds`. It does not
  just hold the pod out of rotation. The rules above make the census unable to refuse on the
  serving image. They do not move the roll gate to a readiness-only reader. On 2026-09-17 a slow
  `/health` census on the startup path meant no memex pod could restart on any image
  ([Probe Semantics](../ProbeSemantics)). The startup path is host and chart wiring
  (MeshWeaver.Plugins and the deployment record), not core.
- **Legacy records** that carry no `CompiledPlatformVersion` get the strict reading until they are
  next stamped.

## Related

[Mesh Admission](../MeshAdmission) ·
[NodeType Compilation](../NodeTypeCompilation) ·
[Deployment (AKS)](../DeploymentAKS)
