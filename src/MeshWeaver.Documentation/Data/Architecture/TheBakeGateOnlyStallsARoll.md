---
Name: The Bake Gate Only Stalls a Roll
Category: Architecture
Description: Why the NodeType bake readiness gate took the control instance fully down, the two rules that now make a refusal possible only on an image that is newer than the build it would be protecting, why its verdict is read by readiness alone and never by the startup probe, and how a sweep a store outage faulted is re-run on the same pod.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l8 4v5c0 5-3.5 8-8 9-4.5-1-8-4-8-9V7z"/><path d="M9 12l2 2 4-4"/></svg>
---

The NodeType bake gate (`PreWarm:GateReadiness`, the `nodetype_bake` check, read by `/ready`)
refuses readiness when a type that used to build no longer builds on this image. Its message states the
contract: *"refusing readiness so the rollout stalls with the previous image still serving"*. The
gate is only safe while that clause is true. On 2026-09-25/26 it was false, and the gate took the
whole control instance down ([#5544](https://github.com/Systemorph/MeshWeaver/issues/5544)).

## What happened

After the V58 migration on the control instance:

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

## What the refusal looked like from outside: a death every 3.03 hours

The refusal had one more cost, and it was mistaken for a separate fault. Between 2026-09-25 ~18:00Z
and the break-glass at 04:05Z, every control-instance portal container on both images died about **3.03 h after
it booted**. There was no crash dump for these deaths. Silo departures of this kind produce the
Orleans timeout family filed as [#5704](https://github.com/Systemorph/MeshWeaver/issues/5704)
(folding #5709, #5730, #5705). That issue was opened from earlier, 2026-09-24 samples, so most of
its evidence predates this window and is only related. The in-window evidence is narrower:
`Dequeue` timeouts at 2026-09-25 18:57:39–41Z against silo `S10.244.3.247`, the incarnation of
`56fbdcd48f-cbms9`, after that pod's 18:55:12Z SIGABRT. The in-window samples tie these
timeouts to a crash, not to a probe kill. That the probe kills produce the same lines is
inferred, not observed.

Nothing deleted those pods. **The kubelet killed each container when its startup probe ran out of
budget.** The readings, all taken 2026-09-26:

| reading | value |
|---|---|
| the control instance's `startupProbe` (from the record, its `values.<instance>.public.yaml` overlay) | `/health`, `periodSeconds: 10`, `failureThreshold: 1080`, so **10 800 s = 3 h** |
| boots of `7bd794f9b5-ggf88` (`[PlatformStartup]`) | 17:58 → 20:59 → 00:01 → 03:03, a 3 h 01–02 m period |
| its `[LIVENESS]` heartbeat (10 s) | reaches tick ~1085 and never 1088+, so death comes at about 10 850 s |
| its log, once per 10 s up to its last minute (e.g. 05:51:57Z) | `Health check nodetype_bake with status Unhealthy … 1 NodeType(s) regressed on this image: BinaryClickerV2/BinaryToggle` |
| kubelet event 05:52:27Z | `Startup probe failed: HTTP probe failed with statuscode: 503` on the same pod, 3 h into its fourth boot |
| `56fbdcd48f-cbms9` (ci.9218) | had served for ~29 h, SIGABRTed at 18:55Z ([#4654](https://github.com/Systemorph/MeshWeaver/issues/4654)), came back 18:58Z, then died at 22:00 and 01:02 |

So the loop was:

1. A container starts on any image.
2. `/health` stays red because the gate refuses.
3. After 1080 failed probes the kubelet kills the container and restarts it in place, with the same
   pod name and `restartCount` + 1.
4. The next attempt reaches the same verdict.

The previous image did not stay safe either. A serving pod that crashed for an unrelated reason
(cbms9 above) had to pass the startup probe again, and could not. The replicas that the stall clause
counts on were exactly the ones the gate turned away.

The pods that looked "deleted and recreated" after 04:05Z are a different event. The break-glass
`kubectl set env` created a new ReplicaSet, and the ordinary rollout then scaled the old ones down
(`SuccessfulDelete … 84f4fb6dcc-wnhdr` at 05:53:47Z). Its second pod had waited `Pending` from
04:07Z to 05:52Z on `Insufficient cpu` (silos requests at 55–91 %, autoscaler at `max node group
size reached`), which is why that roll took 1 h 47 m.

**How to recognise it:**

- The cadence equals `periodSeconds × failureThreshold` of the running Deployment.
- The pod name stays the same while its boots repeat.
- `/health` is red on that pod for the whole window.

`Sample` now records `lastTerminationReason`, `lastExitCode` and `containerStartedAt` per replica
(Systemorph/MeshWeaver.Plugins#2397). That separates this kill from a crash (134/139) or an OOM kill
without `kubectl`. Before that change a restart count was all it carried.

**Not established:** why these kills left no `Application is shutting down` line in Loki when the
two rollout deletions did. The `/drain` endpoint's `Drain:` lines, which would record whether
preStop and SIGTERM ran, returned zero lines in Loki over the 14 h, including for the rollout
deletions. So these images do not emit them, and their absence proves nothing either way.

## The gate holds readiness only

Policy [`bake-gate-readiness-only`](../PolicyNotProse). **The startup probe proves only that the
process booted. The gate's verdict is read by readiness (`/ready`) alone.** A refusal therefore
stalls a roll: the new pod stays alive, it is not in the Service, the previous image keeps
serving, and nothing is killed. A restarted pod of the previous image keeps serving too, because
the startup probe it must pass no longer carries the verdict.

The two rules above made the gate unable to refuse on the serving image. They could not stop the
second failure: the verdict rode the startup probe. On the control instance the startup probe read `/health` with
`periodSeconds 10 × failureThreshold 1080`, a three-hour budget. A startup probe that never records
a success kills the container at the end of that budget and restarts it into the same verdict. So
from 2026-09-25 ~18:00Z every portal container of both images died 3.03 h after it booted, and a
restarted pod of the serving image had to pass the same probe. The control instance was down from
20:54Z to 04:07Z ([#5704](https://github.com/Systemorph/MeshWeaver/issues/5704)).

### How it is wired

- **`ProbeEndpoints.RollGateTag` (`roll-gate`)** marks a check as a roll gate. The portal's service
  defaults (`ServiceDefaults.TagRollGates`, a `PostConfigure` over every host's registrations) add
  it to the checks named in `ServiceDefaults.RollGateChecks` and strip any `live` tag from them.
  Those are `NodeTypeBakeGateExtensions.HealthCheckName` (`nodetype_bake`),
  `ProbeEndpoints.RequiredModulesCheckName` (`required_modules`),
  `ModuleForwarderHealthCheck.HealthCheckName` (`module_forwarders`, MeshWeaver#6847) and
  `UserDirectoryReadiness.HealthCheckName` (`user_directory`). The host registers the first two
  in MeshWeaver.Plugins, by name. The service defaults register the other two
  ([The User Directory Opens at Start](../UserDirectoryOpensAtStart)). The rule holds whatever tags those registrations carry, so no
  host can put a roll gate back on a killing probe by forgetting a tag.
- **`/ready`** reads checks tagged `ready` plus the roll gates. Its body names every check that is
  not Healthy, so the kubelet's probe-failure event says why.
- **`/health`** still runs every check, and it still prints the gate's reading, whatever it says,
  with the suffix `[roll gate: read by /ready only, never by the startup probe]`. Its status code
  and the word on line one are the **startup verdict**: the worst status over every check that is
  not a roll gate. The operator's instrument (`/health`, the `Sample` action's `healthDetail`) keeps
  showing the gate.
- **The chart.** The readiness probe was already on `/ready`. The rollout's
  `progressDeadlineSeconds` now adds `probes.rollGate.bakeSeconds` (1800 s) in every render,
  because the bake's time is spent after startup, holding readiness, and the gate can be armed by an
  env source the render cannot see. Running out of
  it reports `ProgressDeadlineExceeded` and kills nothing. Arming the gate no longer means raising
  `probes.startup`.
- **The guards.** `RollGateReadinessOnlyTest` drives the real endpoints over HTTP, on the probe
  paths the chart ships. A refusing `nodetype_bake` must leave the startup probe at 200, turn
  readiness to 503, leave liveness at 200, and still print on `/health`. An ordinary Unhealthy check
  must still fail the startup probe. The same test holds an Unhealthy `required_modules` to the
  same three answers, shows that its Degraded verdict (a store-delivered module still expected)
  holds nothing, and pins `ProbeEndpoints.RequiredModulesCheckName` to the literal the host
  registers today. That pin sees the core side only: the host still registers a literal, so a rename
  there would not turn it red. Invariant 10b of `check-chart-invariants.sh` refuses an armed
  gate whose readiness probe is not on `/ready`, and any render whose rollout deadline does not
  cover a cold bake. `PreWarmGateReadinessGuard` holds the chart's own prose and prerequisites to the same rule.

### The startup budget, once the gate is off the startup probe

With the gate on readiness, the startup budget no longer has to cover a bake. It covers a plain cold
boot only. Both AKS instances that ran the three-hour budget now carry **`failureThreshold: 60`**
(`periodSeconds: 10`, so **600 s**) in their records in the deployments repository:
the control instance's `values.<instance>.public.yaml` (Memex #547) and
the public instance's (Memex #549). The records keep the old
1080 rationale as a comment, marked superseded, so a reader who finds 1080 in an older render knows
why it existed.

That budget is safe only on an image that carries the WHOLE readiness-only split: #5749 for the bake
gate and #5754 for `required_modules`. On an image missing #5749 the gate still rides `/health`, and
a bake outlasts 600 s: on the public instance a pod's bake-report sweep was stamped about 21.5 min after the
pod booted. On an image missing #5754, a required module that arrives late still fails the startup
probe inside the shorter budget. So arm the gate on an instance only once its running image carries
both, and never roll such an instance back to an older image with the gate
armed and the short budget.

### Which checks may fail the startup probe

The question for each check is whether killing and restarting the container is the right answer to
its "no". If the "no" is a property of the image, or of the roll, a restart cannot change it, and
the check belongs on readiness.

| Check | Registered in | Can fail the startup probe? | Why |
|---|---|---|---|
| `nodetype_bake` | Plugins host, `if (gateBake)` | **No: roll gate, `/ready` only** | Its verdict is about the image. A restart reaches the same verdict, and killing a previous-image pod removes the roll's fallback. |
| `db_version` (+ `DbVersionGate`) | Plugins host | Yes | A portal ahead of its schema must not serve. A restart is the right retry once the migration Job has run. `DbVersionGate` stops the process itself at startup. |
| `required_modules` | Plugins host | **No: roll gate, `/ready` only** (policy [`required-modules-readiness-only`](../PolicyNotProse)) | A declared-required module that the image should ship is absent from the pod's shelf, or a present one did not install against this platform (both Unhealthy). A store-delivered module not here yet is Degraded, a 200 that holds nothing, as before. If the registry cannot serve a module, it is missing for every image, so on the startup probe it would kill the previous image's restarted pods too, the same failure as the bake gate. As a roll gate a missing module stalls the roll and keeps the pod out of the Service. The cost: a restart no longer re-runs the bundle-fetch init container by itself. A pod whose modules arrive later needs a governed `Restart`. |
| `module_forwarders` | core service defaults | **No: roll gate, `/ready` only** (MeshWeaver#6847) | A root forwarder cannot be resolved in its module's current generation, so every activation that leases it fails on this replica. The replica leaves the Service; whether to restart it is the governed `Restart`'s decision, never a probe's — on the startup probe a live swap that reached every replica alike would have the kubelet kill them all. |
| `user_directory` | core service defaults | **No: roll gate, `/ready` only** | Unhealthy until the user directory has its first snapshot: a pod that cannot identify anyone must not take traffic. The directory read is shared, so a restart would not fill it. After the first snapshot the check is Degraded at worst ([The User Directory Opens at Start](../UserDirectoryOpensAtStart)). |
| `process_progress` (`live`) | Plugins host | Yes, and it restarts via `/alive` | A GC-bound process is fixed by a restart. |
| `PostgreSql` | Plugins host | Yes | No database, no portal. |
| `self` (`live`, `ready`) | core | Never fails | The process can run a delegate. |
| `view_packs` | Plugins host | **No: roll gate, `/ready` only** (MeshWeaver#7328, MeshWeaver.Plugins#4471) | A required render pack (default views, entity-views, graph-views) or the layout client did not register on the root hub, so every form on this replica would render as the control's own `ToString()`. Unhealthy holds the replica out of the Service and stalls the roll; each change of the missing set logs one Error, which feeds the red-log path. It never fails the startup probe: on a fresh install the packs arrive after boot, and a restart would not bring them sooner. Until 2026-10-28 it answered Degraded at worst, and a customer installation served `ToString()` forms on three Ready pods with nothing raised. |
| `content-types`, `storage_capacity`, `data_volume_free_space`, `pending_module_activation`, `bundle_adoption`, `entitlement_anchor` | core / Plugins host | No: Degraded at worst, which is a 200 | Readings, never verdicts. |
| `bake-report`, `source-discovery`, `publication-seal` (`census`) | core | No: Degraded at worst, which is a 200 | They always print; they never gate. |

This table is correct for core's `main` and Plugins' `main` on the day it was written. A check added
later that can answer Unhealthy must be placed deliberately: startup (`/health`, the container is
killed) or roll gate (`/ready`, the roll stalls).

## A sweep the store refused is re-run on the same pod

Taking the gate off the startup probe removed the only thing that ever re-ran a faulted sweep. On
2026-10-22 the managed database behind memex restarted and refused connections from about
21:22:51Z to 21:23:34Z (#7068). One pod was mid-boot. Its sweep met the refusal on `Admin/Build` at
21:22:55Z and recorded `Faulted`. It logged no further `DynamicTypePreWarmer` line, and
`nodetype_bake` answered Unhealthy every ten seconds until the pod was replaced at 22:37Z. That was
74 minutes after the database came back, and the roll could not converge in between
([#7066](https://github.com/Systemorph/MeshWeaver/issues/7066)).

The sweep is now re-run on the same pod when its fault was a transient store fault.

**What counts as transient** is `StandingWatchRecovery.IsTransient`, the same classification a
standing watch re-opens on ([Standing Watches Re-open](../StandingWatchesReopen)). It covers a
database connect or IO fault, the query fan-in's stall terminal, and a store fault that an owner hub
met and classified before it answered. The last one matters for this incident. The exception never
crosses a hub boundary; only the `MeshNodeError` record does. So the sweep held
`MeshNode Unknown at 'Admin/Build': NpgsqlException: Failed to connect` with no inner exception,
and nothing could classify it. The owner now classifies the fault while it still has its type
(`ClassifyPatchException`) and sends the answer on the record as `MeshNodeError.Transient`. The
caller reads that flag, never the message text. A compile error, a query or schema error, or a
deserialization defect is not transient. It ends the sweep on its first occurrence and stays a
named failure, as before.

**What triggers the re-run** is the platform's one "the store answers again" evidence,
`StoreReachability.StoreAnswersAgain` (#7072, the same evidence a refused NodeType release re-cut is
re-issued on). It fires on a declared `ChangeFeedGap`, which a backend whose cross-process feed can
drop declares when it re-opens its session (the PostgreSQL listener does so after a lost
connection). It also fires on a commit the store announced (`IMeshInvalidationFeed.DurableCommits`),
because such a commit is announced only after the store accepted it. The sweep reads every
partition, so a commit in any partition counts. The sweep applies the same two rules
`ReissueWhenStoreAnswers` applies to a single write:

- Evidence is observed for the sweep's whole life. Evidence that arrives while an attempt is still
  in flight is retained, and the re-run starts the moment that attempt faults. The feed never
  replays a reconnect, so without this it would be lost.
- A re-run started by a commit that meets the same fault shows that a commit is not evidence for
  this sweep. From then on only a reconnect starts one, so a busy mesh cannot turn into a re-run
  loop.

The evidence alone is not enough, and the sweep adds one documented clock to it. A gap is declared
only after the feed had come up once, and a backend with no cross-process feed and no announced
commits gives no evidence at all. So for the first few re-runs a timer races the evidence, on the
schedule a standing watch uses (2 s, 5 s, 15 s, 30 s, 60 s). After those, only evidence can trigger
a re-run, and no clock runs at all. `StoreRecoveryRerun` holds the rule; `StoreRecoveryRerunPolicy`
holds the schedule. It defines no second "the store is back" signal.

**What the pod says while it waits.** The fault is recorded honestly first: the gate reads
`Faulted`, and its detail names the pending re-run and what will trigger it. The log line is a
Warning that says the fault is not final. A re-run puts a strict pod back to `Running`, which is
still not ready, and now names its progress again. A pod whose fault already granted readiness
stays in rotation through the re-run: an image that has served here, or `AllowUnprovenBake`. A
completed re-run supersedes the fault, so the gate lands on `Complete` and readiness follows.
The served-build witness is offered again on each re-run, because the first offer was discarded
when the fault refused the pod. The bake barrier that boot flows sequence on (#1114) is held while
a timed re-run is pending, and released once only the signal can trigger one.

The regression test is `AWarmUpSweepRerunsWhenTheStoreRecoversTest` (MeshWeaver.FaultInjection.Test).
It runs the real hosted service on a real mesh, refuses the sweep's catalog read with the incident's
fault shape, and allows no timed re-run. It asserts that the same gate instance reaches `Complete`
once the store recovers and the change feed declares the gap, through `StoreReachability`. Its negative control holds the sweep the way the
service held it before. That gate stays unready after the same recovery, while a fresh sweep on the
same mesh passes. A third case shows a non-transient fault is not re-run.

The host's readiness message (`NodeTypeBakeHealthCheck`, in the MeshWeaver.Plugins host) still
ends "A restart re-runs the sweep". For a transient fault the gate's own detail, which that
message carries, now names the re-run that is actually pending.

## What this does not cover

- **A `Faulted` sweep on an image that has NOT served here still refuses readiness** unless
  `PreWarm:AllowUnprovenBake` is set. That is deliberate: an unproven new image is exactly what the
  gate exists to hold back. On an image that HAS served (the marker exists), a `Faulted` sweep no
  longer refuses. The gate reads the same witness directly (`NodeTypeBakeGateState.ServedBefore`),
  because a sweep that errored has no per-type evidence for rule 1 to judge.
- **A `Faulted` sweep is no longer retried by a restart.** While the gate rode the startup probe,
  a refused pod was killed at the end of the budget and the restart re-ran the sweep. Now a refused
  pod stays alive and out of the Service. A sweep that faulted on a TRANSIENT store fault is re-run
  by the pod itself once the store is reachable again (next section). A sweep that faulted on
  anything else stays errored until someone acts: a governed `Restart` of the instance, or
  `PreWarm:AllowUnprovenBake`. That is the price of never killing anything, and it is paid on
  purpose.
- **Legacy records** that carry no `CompiledPlatformVersion` get the strict reading until they are
  next stamped.
- **The first armed boot after this ships writes no marker for the images already serving.** A
  marker exists only once a pod of that build is admitted with the gate armed. Until then, a
  restart falls back on rule 1 and the provenance witness.

## Incident history

The table lists the control instance's outage of 2026-09-25/26 as three failures, each with the
change that closed it. The failures are the evidence above. The table is here so a reader who arrives from a
later incident can see what was already fixed.

| Failure | What closed it |
|---|---|
| A type that had never built (`BinaryClickerV2/BinaryToggle`, #3883) read as a regression, and the serving image refused its own readiness | #5725: the two rules. A regression needs a working build from an older image, and an image that has served here never refuses itself |
| The gate's verdict rode the startup probe, so every refused container was killed at `periodSeconds × failureThreshold` (10 s × 1080 = 3 h), on both images | #5749: the gate holds readiness only (policy `bake-gate-readiness-only`). #5754 put `required_modules` under the same rule (policy `required-modules-readiness-only`). Sample reports how each container's previous run ended (Plugins #2397) |
| The previous image was not a safe fallback: a serving pod that crashed for another reason (#4654) had to pass the same probe, and could not | the same split: a restarted previous-image pod no longer meets the verdict on its startup probe. The startup budget went back to a cold boot, 600 s, on the control and public instances (Memex #547, #549) |

Two conditions made the recovery slower than the fix. The migrate-first roll plan could not reach
the control instance, because the Hosting sources that carried it were held behind a seal; see
[Planning a Database Migration](../PlanningADatabaseMigration) and
[Module Sync Per Manifest Hash](../ModuleSyncPerManifestHash). And the replacement pod waited
`Pending` on `Insufficient cpu` with the silos node pool at its autoscaler maximum; the pool's
maximum and its declaration moved into the estate's governed infrastructure lane (Memex #546,
#551).

## Related

[Deploying Across Platform Versions](../DeployingAcrossPlatformVersions) ·
[Standing Watches Re-open](../StandingWatchesReopen) ·
[Mesh Admission](../MeshAdmission) ·
[NodeType Compilation](../NodeTypeCompilation) ·
[Deployment (AKS)](../DeploymentAKS)
