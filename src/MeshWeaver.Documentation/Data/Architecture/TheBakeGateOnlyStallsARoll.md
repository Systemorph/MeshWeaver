---
Name: The Bake Gate Only Stalls a Roll
Category: Architecture
Description: Why the NodeType bake readiness gate took memex.systemorph.com fully down, the two rules that now make a refusal possible only on an image that is newer than the build it would be protecting, and why its verdict is read by readiness alone and never by the startup probe.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l8 4v5c0 5-3.5 8-8 9-4.5-1-8-4-8-9V7z"/><path d="M9 12l2 2 4-4"/></svg>
---

The NodeType bake gate (`PreWarm:GateReadiness`, the `nodetype_bake` check, read by `/ready`)
refuses readiness when a type that used to build no longer builds on this image. Its message states the
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
`NodeTypeBakeReport.ThisBuildHasServed` is true when either of two witnesses says so. Both are
admission-gated publications, so a refused pod can write neither:

- **The durable admission marker (`ServedBuildWitness`).** When the gate is armed, the sweep offers a
  row at `Admin/ServedPlatformBuilds/<build>` to `MeshPublicationGate`. The gate holds the offer
  while the bake measures, writes the row when the pod is admitted, and discards it when the pod is
  refused. It is read and written straight through `IStorageAdapter`, the same pattern as the build
  claim lock. There is no hub and no point read of a missing node. A read that fails counts as "not
  served", which is the strict reading. The report carries the answer as `ServedBefore`.
- **A record whose working build this very build produced.** A compile stamp carrying this build's
  identity is released only once the stamping process was admitted.

The marker is the witness that matters in the ordinary case. The first cut of this fix relied on
record provenance alone, and review showed why that is not enough. An ordinary roll compiles
nothing, because the compatibility key is equal across builds of one epoch. Prebuilt adoption keeps
the PRODUCER's platform version on purpose. So a serving image can leave no record naming itself,
and a restart of it would read as a stranger. A pod that finds either witness is a restart of a
serving image, not a roll candidate, so nothing it reports may gate. `NodeTypeBakeReport.GateRelevant`,
which the witness-unreadable path refuses on, applies the same two rules.

**Together:** a pod can still refuse on a regression only if both hold:

- its build is strictly newer than the build that produced the working build it failed to reproduce;
- no replica of its build has ever been admitted here.

Once a build has been admitted with this code, its own pods never meet the second condition, so
the old ReplicaSet can always come back. Before that, rule 1 still holds: a type is never a
regression of an image that is not newer than the image that built it. Failures that no longer gate are still recorded and named in the health payload
(`WithoutBaseline`). Once the pod is admitted, the held `Error` stamp is released, and the record
finally says `Error`. That ends the loop in defect 2.

## The gate holds readiness only

Policy [`bake-gate-readiness-only`](../PolicyNotProse). **The startup probe proves only that the
process booted. The gate's verdict is read by readiness (`/ready`) alone.** A refusal therefore
stalls a roll: the new pod stays alive, it is not in the Service, the previous image keeps
serving, and nothing is killed. A restarted pod of the previous image keeps serving too, because
the startup probe it must pass no longer carries the verdict.

The two rules above made the gate unable to refuse on the serving image. They could not stop the
second failure: the verdict rode the startup probe. On memex the startup probe read `/health` with
`periodSeconds 10 × failureThreshold 1080`, a three-hour budget. A startup probe that never records
a success kills the container at the end of that budget and restarts it into the same verdict. So
from 2026-09-25 ~18:00Z every portal container of both images died 3.03 h after it booted, and a
restarted pod of the serving image had to pass the same probe. The control instance was down from
20:54Z to 04:07Z ([#5704](https://github.com/Systemorph/MeshWeaver/issues/5704)).

### How it is wired

- **`ProbeEndpoints.RollGateTag` (`roll-gate`)** marks a check as a roll gate. The portal's service
  defaults (`ServiceDefaults.TagRollGates`, a `PostConfigure` over every host's registrations) add
  it to the check named `NodeTypeBakeGateExtensions.HealthCheckName` (`nodetype_bake`) and strip
  any `live` tag from it. The host registers the check in MeshWeaver.Plugins, by name. The rule
  holds whatever tags that registration carries, so no host can put the gate back on a killing
  probe by forgetting a tag.
- **`/ready`** reads checks tagged `ready` plus the roll gates. Its body names every check that is
  not Healthy, so the kubelet's probe-failure event says why.
- **`/health`** still runs every check, and it still prints the gate's reading, whatever it says,
  with the suffix `[roll gate: read by /ready only, never by the startup probe]`. Its status code
  and the word on line one are the **startup verdict**: the worst status over every check that is
  not a roll gate. The operator's instrument (`/health`, the `Sample` action's `healthDetail`) keeps
  showing the gate.
- **The chart.** The readiness probe was already on `/ready`. The rollout's
  `progressDeadlineSeconds` now adds `probes.rollGate.bakeSeconds` (1800 s) when the gate is armed
  in the render, because the bake's time is spent after startup, holding readiness. Running out of
  it reports `ProgressDeadlineExceeded` and kills nothing. Arming the gate no longer means raising
  `probes.startup`.
- **The guards.** `RollGateReadinessOnlyTest` drives the real endpoints over HTTP, on the probe
  paths the chart ships. A refusing `nodetype_bake` must leave the startup probe at 200, turn
  readiness to 503, leave liveness at 200, and still print on `/health`. An ordinary Unhealthy check
  must still fail the startup probe. Invariant 10b of `check-chart-invariants.sh` refuses an armed
  gate whose readiness probe is not on `/ready`, or whose rollout deadline does not cover a cold
  bake. `PreWarmGateReadinessGuard` holds the chart's own prose and prerequisites to the same rule.

### Which checks may fail the startup probe

The question for each check is whether killing and restarting the container is the right answer to
its "no". If the "no" is a property of the image, or of the roll, a restart cannot change it, and
the check belongs on readiness.

| Check | Registered in | Can fail the startup probe? | Why |
|---|---|---|---|
| `nodetype_bake` | Plugins host, `if (gateBake)` | **No: roll gate, `/ready` only** | Its verdict is about the image. A restart reaches the same verdict, and killing a previous-image pod removes the roll's fallback. |
| `db_version` (+ `DbVersionGate`) | Plugins host | Yes | A portal ahead of its schema must not serve. A restart is the right retry once the migration Job has run. `DbVersionGate` stops the process itself at startup. |
| `required_modules` | Plugins host | Yes | A declared-required module missing from the pod's shelf. A restart re-runs the bundle-fetch init container. Open question, not decided here: if the registry is down for every image, this kills previous-image pods too. |
| `process_progress` (`live`) | Plugins host | Yes, and it restarts via `/alive` | A GC-bound process is fixed by a restart. |
| `PostgreSql` | Plugins host | Yes | No database, no portal. |
| `self` (`live`, `ready`) | core | Never fails | The process can run a delegate. |
| `content-types`, `storage_capacity`, `data_volume_free_space`, `pending_module_activation`, `bundle_adoption`, `entitlement_anchor`, `view_packs` | core / Plugins host | No: Degraded at worst, which is a 200 | Readings, never verdicts. |
| `bake-report`, `source-discovery`, `publication-seal` (`census`) | core | No: Degraded at worst, which is a 200 | They always print; they never gate. |

This table is correct for core's `main` and Plugins' `main` on the day it was written. A check added
later that can answer Unhealthy must be placed deliberately: startup (`/health`, the container is
killed) or roll gate (`/ready`, the roll stalls).

## What this does not cover

- **A `Faulted` sweep on an image that has NOT served here still refuses readiness** unless
  `PreWarm:AllowUnprovenBake` is set. That is deliberate: an unproven new image is exactly what the
  gate exists to hold back. On an image that HAS served (the marker exists), a `Faulted` sweep no
  longer refuses. The gate reads the same witness directly (`NodeTypeBakeGateState.ServedBefore`),
  because a sweep that errored has no per-type evidence for rule 1 to judge.
- **A `Faulted` sweep is no longer retried by a restart.** While the gate rode the startup probe,
  a refused pod was killed at the end of the budget and the restart re-ran the sweep. Now a refused
  pod stays alive and out of the Service, so a sweep that errored stays errored until someone acts:
  a governed `Restart` of the instance, or `PreWarm:AllowUnprovenBake`. That is the price of never
  killing anything, and it is paid on purpose.
- **Legacy records** that carry no `CompiledPlatformVersion` get the strict reading until they are
  next stamped.
- **The first armed boot after this ships writes no marker for the images already serving.** A
  marker exists only once a pod of that build is admitted with the gate armed. Until then, a
  restart falls back on rule 1 and the provenance witness.

## Related

[Mesh Admission](../MeshAdmission) ·
[NodeType Compilation](../NodeTypeCompilation) ·
[Deployment (AKS)](../DeploymentAKS)
