---
Name: Instance Reboot
Category: Architecture
Description: One operation, "reboot this instance", that brings it to a known-good, newest state in one step — sync every module source, land every module's newest compatible version, pick the newest admitted image, take ONE roll or restart, and verify every booted process (health readings plus a thread-start smoke check) — reported step by step on one request node. A person's call is the signature; the instance's own watchdog may trigger it on an explicit wedge predicate, rate-limited and alarmed.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v8"/><path d="M18.4 6.6a9 9 0 1 1-12.8 0"/></svg>
---

# Instance Reboot

> **The rule (policy `instance-reboot`, [register](../PolicyNotProse)).** An instance can be brought
> to a known-good, NEWEST state in ONE step. One durable request runs five steps in order, and each
> step reports its outcome on the request with a named reason when it is skipped or fails. A person's
> call is the signature, with no second approver, and the request records who made it. The instance's
> own watchdog may file the same request, without approval, but only on an explicit, conservative
> wedge predicate, at most once per interval, and every firing raises an alarm.

## Why it exists

The control instance once spent a day unable to recover itself. Hosting needed `MeshWeaver.AI` 1.21,
1.20.4 stayed loaded, and every thread start threw `MissingMethodException`. Every remedy existed:
a sync, a module landing, a restart, a newer image. But each was a separate act, some needed a
second person, and the one instance able to run them was the one that was broken. A reboot is all
of those acts as one request, executed by platform code that is shipped in the image, so it cannot
be broken by the module it repairs.

## The request

An `InstanceReboot` node lives at `Admin/_Reboot/{id}` and holds an `InstanceRebootRequest`
(`MeshWeaver.Graph.Configuration`). It is written only by `InstanceReboot.Request`, as System, after
the caller has authorised the requester. The executor acts only on a request whose `createdBy` is
System. This is the same trust rule as [Module Reload](../ModuleReload).

| field | meaning |
|---|---|
| `reason`, `requestedBy`, `requestedAt` | why, and who: the person's user id or `reboot-watchdog` |
| `trigger` | `Person` or `Watchdog` |
| `wedged` | the instance was judged wedged. A watchdog reboot always is; a person may say so. The image step then does not wait for a dependent-suite verdict |
| `wedgeEvidence`, `wedgeFingerprint` | for a watchdog reboot: the evidence that fired it, as one line, and its fingerprint |
| `status` | `Requested`, then `Preparing`, then `AwaitingRestart`, ending in `Done` or `Failed` |
| `steps[]` | `Sync`, `Modules`, `Image`, `Restart`, `Verify`. Each step is `Pending`, `Running`, `Ok`, `Skipped` (with the reason why) or `Failed` (with the reason why) |
| `modules[]` | the module rows the landing produced: found, landed and target version, or a decline by name |
| `runningImage`, `targetImage` | what ran before, and what the image step chose. A null target means a restart on the running image |
| `restartRequestedAt` | stamped BEFORE the roll or restart is requested, so a resumed executor never asks twice |
| `replicas{process}` | what each process that booted after the stamp reported from its own checks |
| `failure`, `completedAt`, `log[]` | the red steps by name, when it ended, and the audit trail |
| `executionWindow` | DECLARED when the request is filed (`Reboot:ExecutionWindow`, default 3 h): the longest the request may go without recording progress and still count as in progress. See "A request that stops moving" |
| `progressAt` | when it last recorded progress: stamped on every executor write (including each finished sync source) and on a re-arm |
| `requestedEndIfStalledAt` | a reader that saw the request stalled asks its owner to end it; the owner answers and clears it |
| `failureText`, `logText[]`, `steps[].detailText`, `replicas{}.checks[].detailText` | the keyed form of each English sentence beside it. See "Reading a request in your language" |

### Reading a request in your language

The request page (the node's Overview, `InstanceRebootLayoutAreas`) shows the facts, the failure, the
steps, the modules, each replica's checks and the log, built from the platform's controls and bound
to the request's own node stream. Each sentence the platform writes is stored twice. The English is
the wire value that logs, tests and the watchdog read. Beside it is a keyed form (`RebootText`): a
sequence of catalog keys (`reboot.*` in `strings.{en,de}.json`) with named arguments, each part glued
to the one before it by punctuation only. This is the `KeyedText` carrier of the compile refusals
(see [Localization](../Localization)), extended with the glue that the reboot's `; `, `, ` and
bracket joins need.

The page renders each keyed form in the viewer's language, taken from `ViewerLocale()` and never
from the ambient culture. A keyed form is used only while its English rendering is exactly the
stored English. Text that a later writer replaced, a request filed before the keys existed, and a
check that writes no keyed detail (a module's own smoke check) are shown as written. `logText` is
aligned with `log` from the end, because both only grow at the end and are trimmed at the front.

Facts stay as written: versions, paths, process names, the reason a person typed, and sentences
composed by another subsystem (a GitSync row's detail, a module's failure, a self-updater's
selection detail). Step names, outcomes and statuses are shown through `reboot.step.*`,
`reboot.outcome.*` and `reboot.status.*`, and an unknown value is shown by name.

🚨 The `stalled: ` prefix in `failure` is a machine marker. `EndedAsStalled`, and the watchdog
through it, reads it back. It is composed in English exactly as before and never passes through
the catalog. The German viewer reads `festgefahren:` from the keyed form beside it.

## The five steps

The executor is `InstanceRebootExecutor`, running on the request node's own hub, so exactly one
process drives a request.

1. **Sync.** Every GitSynced MODULE source is imported at its branch HEAD
   (`ModuleSourceUpdate.UpdateAll`). The executor makes the same call as `git_hub_sync op=update`
   (`UpdateToLatestFromGitHub`), once per source, as System, and follows each import's activity to
   its end. The seal is consulted exactly as it is for a person's update: a held source lands
   nothing and the hold is named. Each module is judged alone by its manifest hash. A floor above
   the running platform, or a dependency the loaded build does not meet, is declined by name
   (`ModuleSyncDecision`). A source counts as a module source when its config has recorded a
   module-bearing tree. Content-only spaces are not re-imported, and the step lists them as
   skipped, with the reason. 🚨 Each import is bounded by `ModuleSourceUpdate.ActivityBudget`
   **from the moment its activity is created** (MeshWeaver#6625). `RunActivity` emits the activity
   path only when the run is terminal, and the budget used to be composed after that emission, so
   it bounded nothing: an import that never finished held the Sync step — and the request,
   non-terminal, which suppresses every pod's self-reboot — indefinitely. Observed: one instance
   held in `Preparing` at the Sync step; another held a reboot request open for ~21 h, but its step
   state could not be read, so the Sync stall is the mechanism that FITS it, not one measured
   there. Now the row reads `Failed` — *"did not finish within … its outcome is NOT established"* —
   the import's cancellation is requested through the Activity Control Plane (as System, at the
   write), and the reboot goes on to the next step. Because that import may still be running, no
   later source's import is started behind it in the same pass: those rows read `Skipped`, naming
   the import they wait behind, since two sources can target one Space. The same holds for every
   other way the watch on an import's activity can end without a terminal status once that activity
   exists (MeshWeaver#6753): a watch that faults (its gate query, or the point read meeting a
   routing error), throws while it is being built, or ends early proves nothing about the import,
   so the row reads `Failed` with *"… may still be running … its outcome is NOT established"* and
   the later sources read `Skipped`. The step's summary therefore counts skipped rows without
   naming one reason for all of them — each sync line says why its source was skipped. A
   non-positive budget is refused before any source is listed. Only a failure from before the activity exists lets the pass go on to the next
   source, because then no import is running. Why an import hangs is the import's own
   defect and is not answered by this bound (`AHungImportCannotHoldTheRebootTest`).
2. **Modules.** The executor lands the newest COMPATIBLE published version of every installed
   module through `ModuleReloadExecutor.ResolveAndLand`, which is the module reload's own
   resolve-and-land and is not forked. Compatibility means the declared floor is at or below the
   running platform, never a seal. A floor above it is a decline, named on the step. A decline is
   not red, because it is the right answer; any other failure to land is red. Nothing is activated
   at this step, because the restart activates it.
3. **Image.** The executor picks the newest platform image that this instance's own update policy
   admits (`IInstanceRebootActivation.SelectImage`, the self-updater's own selection: the
   registry's tags, the policy's channel and pattern, and the release-availability walk). A held
   release is never forced. The step then reads `Skipped`, names the hold, and the reboot
   restarts on the running image. A **wedged** reboot leaves the combo (dependent-suite) verdict
   out of the decision and says so on the step.
4. **Restart.** The executor stamps `restartRequestedAt`, then makes ONE roll to the target, with
   the database migration first, or one restart on the running image when there is no target.
   This goes through the self-updater's one path: a self-patch, or a hand-over to the control lane
   (`self-update-available` or `self-update-restart-pending`). The roll floor does not defer it.
   If no roll or restart could be scheduled, the reboot is `Failed`, with the step naming why.
   Two refusals come before anything is issued:
   - a disruptive rollout strategy on the portal Deployment;
   - a control instance that cannot self-patch, which would otherwise hand its restart to itself.

   Both are described under "Operating lessons" below.
5. **Verify.** Every process that booted after the stamp runs the registered checks
   (`InstanceRebootAgent`, `IInstanceRebootCheck`) and reports under its own key:
   - `health:nodetype_bake` waits for this process's bake to settle and is red when any CRITICAL
     NodeType (`Reboot:CriticalNamespaces`, default `Hosting`) regressed, is content-broken,
     failed with no baseline, or was not evaluated. Failures elsewhere are named, not red.
   - `health:pending_module_activation` is red while a landed module still waits for activation,
     or the state is undetermined.
   - `health:content-types` is red when a NodeType's content degraded to untyped on this process.
   - `health:singletons-resumed` is red when a configured singleton (the PR babysitter, the PR
     review sweep) has not stamped a pass newer than the restart within its budget.
   - `smoke:thread-start` (registered by the AI module in MeshWeaver.Plugins) starts a thread and
     is red when the start throws.

   A counted process that measured NO smoke check makes the verdict red. Without that rule, the
   incident's own failure shape would pass unmeasured. A health reading that is not registered on
   the host is `NotMeasured`: it is named in the verdict and counted neither green nor red.

A red step before the restart does NOT stop the reboot, because bringing the instance back is the
point. It does make the request `Failed`, naming every red step. Only the restart failing ends the
reboot early, because nothing would boot to verify.

How the restart fails decides whether that is final:

| The restart request… | Activation kind | The reboot |
|---|---|---|
| was decided against: no restart path on this install, a release held or deferred, a failed migration | `Unavailable` | `Failed` — final |
| crashed: the restart call threw, a hand-over to the control lane failed, a registry check could not be made | `Faulted` | `Faulted` — not terminal |

A transient failure is never final. A `Faulted` reboot is re-armed by the plugin catalog's reconcile
pass (`InstanceReboot.RetryFaulted`, next to the module-reload retry and with the same backoff:
the pass interval doubled per attempt). The re-arm keeps steps 1–3 and clears only the restart
stamp, so the executor asks for the ONE restart again; the one-restart rule holds per attempt. An
activation outcome the reboot lane does not name is reported `Unavailable` and logged by name.

## A request that stops moving

🚨 A reboot request that never reaches a terminal state used to suppress every pod's self-reboot for
as long as it existed (MeshWeaver#6625: one instance held a request open for ~21 h while its
critical NodeTypes stayed in compile Error). A request now counts as "in progress" only while it is
**progressing**, judged by its own record (`InstanceReboot.Stalled`, pure):

- its **last progress** (`InstanceReboot.LastProgressAt`) is the latest instant it carries:
  `progressAt`, `requestedAt`, each step's `at`, `restartRequestedAt`, each replica's `reportedAt`.
  A `Faulted` request is waiting for its scheduled re-arm, so its retry's due time counts too;
- it is **stalled** when it is not terminal and that last progress is older than its declared
  `executionWindow`. A request filed before the field existed is judged by the reader's
  `Reboot:ExecutionWindow`.

Two readers act on that, and neither runs a timer of its own:

- **The watchdog** reads each open request from its own node stream (never a stale listing deciding
  a reboot). 🚨 A stalled request is never simply ignored. It would stay eligible for the
  faulted-restart re-arm, and then the original and its replacement could both roll. The watchdog
  asks the request's OWNER to end it (the same `requestedEndIfStalledAt` question as below) and
  waits, within the read budget, for the owner's answer. Only an owner-confirmed `Failed` releases
  the self-reboot, and that is never silent: a `Critical` line names the request, its trigger and
  why it stalled, once per request per process. A request that something else ended first (the
  reconcile pass's question, another process's watchdog) is named the same way. Its reason is read
  back from the persisted `failure` marker (`InstanceReboot.EndedAsStalled`), for ends within
  `WatchdogRepeatWindow`, and the pass lists it in `EndedStalled`. A request the owner kept open, or an answer that did
  not come in time, still holds the self-reboot back. The rate limit's other two rules still hold.
  The read also activates the request's own hub, which resumes its executor if the process that
  drove it is gone.
- **The plugin catalog's reconcile pass** asks the OWNER of every request that looks stalled to end
  it (`InstanceReboot.FailStalled`, which writes the `requestedEndIfStalledAt` control field). It
  runs right after the faulted-restart re-arm, so a `Faulted` request due for its retry is retried,
  not ended. 🚨 The decision is never made on the reconcile pass's own copy of the node. A
  cross-hub write is a merge patch, and the owner guards only the keys it carries, so a replica
  report landing between that evaluation and the commit would not stop a `Failed` from landing.
  The request's own hub answers instead (`InstanceReboot.EndIfStalled`): in ONE update, against its
  own current state, it ends the request as `Failed` naming the stall, or leaves it open. Either
  way it clears the field. That answer runs on its own subscription, so a step that never returns
  cannot hold it back.
- **A terminal request is final.** An executor write that meets one changes nothing AND emits
  nothing, so the step chain behind it ends there. A late Sync result cannot start the module
  landing, and a request ended before the restart stamp cannot go on to ask for the restart.

The common ways a request stops moving, each now ended by name: the restart handed to the control
lane never happened, so no process booted after the stamp and nothing reported (`AwaitingRestart`);
a `Faulted` restart that nothing re-armed (the reconcile safety net is off); a step that never
returned. The Sync step's own per-import bound is described under step 1.

The window is longer than any single wait: one source import is bounded at 10 minutes, and the
verification after a restart waits at most `SingletonResumeBudget` (35 minutes) beside the bake
settle (15 minutes). The Sync step imports its sources one after another, so a long batch could
exceed the window as a whole. Each source's row is therefore recorded on the request as progress
the moment that import ends, before the next one starts (`ModuleSourceUpdate.UpdateAllReporting`).
A batch of many healthy imports is progress all the way through. The reboot's answer to each row
also says whether the batch may go on: a request that has ended starts no further import, so
nothing is imported beside its replacement, and the remaining sources read `Skipped`, saying so. A
fault from that reporting reaches the reboot. It is never folded into the "could not be listed"
row. The Modules step works the same way (`ModuleReloadExecutor.ResolveAndLandReporting`): each
module is recorded as progress the moment its reload ends, and a request that has ended reloads
no further module.

## Who can start one

| surface | authorisation | how |
|---|---|---|
| MCP `reboot_instance` (MeshWeaver.Plugins `McpMeshPlugin`) | `IsGlobalAdmin` | `MeshOperations.RebootInstance(reason, wedged)`. Returns `{status, path}` at once; read the node for the outcome |
| the **Reboot** button on a deployment's page (MeshWeaver.Plugins `Hosting/Deployment`) | `IsGlobalAdmin`; the click is the signature | files a `Hosting/InstanceAction` of kind `Reboot`. For this instance, the action files the request in-process; for another instance, it goes through the control lane |
| the control lane (`ControlLaneOperation.Reboot`, `RebootOperation`) | the control instance signs with the deployment's own key | the plan is fixed, with ONE step (`RebootOperation.PlanFor`), so its digest is bound without a dry run, and the requester is the approver. The lane run ends when the request is FILED on the target, and the reboot then reports on its own node there |
| the instance's own watchdog | none; rate-limited and alarmed | see below |

## Self-trigger: the watchdog

`RebootWatchdog`, armed on every process's mesh hub, evaluates once per `Reboot:WatchdogInterval`
(default 1 minute). It lives exactly as long as that hub (#7496): it takes no pass once the hub
leaves `Started`, and a pass that meets the hub's disposed service scope ENDS the loop with one
Debug line — the container cannot come back, so there is nothing to retry and nothing to alarm.
Any other fault of a pass is a Warning and the next pass runs. (It used to resolve its services
outside the per-pass catch, so a pod going down logged `[RebootWatchdog] stopped` at Error.) The predicate is explicit and pure (`RebootWatchdogRules.Evaluate`), and it
holds when EITHER leg holds:

- **thread-start leg:** at least `ThreadStartFaultThreshold` (default 3) thread starts failed with a
  load or binding fault within `ThreadStartWindow` (default 15 minutes). Load and binding faults are
  `MissingMemberException` (method or field), `TypeLoadException`, `BadImageFormatException`, and a
  `FileNotFoundException` or `FileLoadException` for an ASSEMBLY, unwrapped from aggregate,
  invocation, type-initialisation and `InvalidOperationException` wrappers (dependency injection and
  Rx operators surface a binding fault inside one). A wrapper is only looked through, never counted
  itself: an `InvalidOperationException` with no load or binding fault inside is not a wedge signal. A component reports these faults through
  `hub.ReportWedgeFault(WedgeSignalKinds.ThreadStart, source, exception)`. Anything else is
  dropped at the sink: a model error or a refusal is never evidence of a wedge.
- **compile leg:** a critical NodeType is in compile Error on EVERY pass for at least
  `CompileErrorFor` (default 20 minutes). The reading is the shared `compilationStatus` field,
  queried as System. A pass that cannot read RESETS the leg, because no evidence is not evidence of a
  wedge.

The rate limit is `RebootWatchdogRules.Admit`. It refuses to fire in three cases:

- while any reboot is still open. A stalled one is first ended by its owner (see "A request that
  stops moving"); until then it counts as open;
- when a self-reboot was taken within `WatchdogMinInterval` (default 6 hours). The process also
  remembers its own last firing, so a pass right after a firing never fires again;
- when the SAME evidence, by fingerprint, already survived a self-reboot within
  `WatchdogRepeatWindow` (default 24 hours). A reboot that did not clear it will not clear it
  twice; a person must act.

Every firing and every refused firing logs a `Critical` line, `[RebootWatchdog] …`, naming the
evidence. Refused firings are logged once for each distinct evidence and reason per process.
Setting `Reboot:WatchdogEnabled` to `false` turns the self-trigger off.

## Configuration (`InstanceRebootOptions`)

| key | default |
|---|---|
| `CriticalNamespaces` | `Hosting` |
| `BakeSettleBudget` / `CheckBudget` | 15 min / 5 min |
| `WatchdogEnabled` / `WatchdogInterval` | true / 1 min |
| `ThreadStartFaultThreshold` / `ThreadStartWindow` | 3 / 15 min |
| `CompileErrorFor` | 20 min |
| `WatchdogMinInterval` / `WatchdogRepeatWindow` | 6 h / 24 h |
| `ExecutionWindow` | 3 h, stamped on each request when it is filed |

## Operating lessons the reboot must respect

These lessons come from the control-instance recovery attempts on the day this was built:

- **Roll only to an image the instance can pull.** A roll to a version tag that was never minted
  fails with `ImagePullBackOff`. The Image step picks ONLY from the tags the registry itself lists
  (the self-updater's `ListTags`): a version tag when it was promoted, otherwise whatever tag the
  registry actually holds, such as a pair tag like `260b3c4-p1e182f9`. A tag that is not in the
  listing is never chosen. Nothing re-probes the registry between the listing and the patch.
- **A path that does not depend on the wedged instance signing anything.** A wedged control
  instance may sign with a stale key, for example from a stale adopted Hosting build. The
  in-process reboot, filed through the instance's own MCP `reboot_instance` or by its watchdog,
  signs nothing. Whether its restart avoids a hand-over depends on whether the instance can
  self-patch:
  - When it can self-patch (`SelfUpdate:CanPatch` and an updater that can patch), the restart goes
    through the Kubernetes API under the instance's own service account. That path signs nothing.
  - When it cannot, an ordinary instance hands the restart to the control lane, and that
    hand-over is signed.
  - 🚨 **A CONTROL instance never hands its own restart to itself.** Its hand-over route is its
    own inbox (`SelfUpdateHandover.Route.Local`), so the control plane that would execute the
    hand-over is the one being rebooted. A control instance that cannot self-patch is therefore
    **refused** by name (`Refused: this instance IS the control instance …`), and nothing is
    handed over. Self-patch through its own service account is REQUIRED for the control instance.
  The Restart step names which path was taken: `Rolled`/`Restarted` = self-patch, `HandedOver` =
  control lane, `Refused` = neither, with the reason. The control-lane `Reboot` operation is the
  route for a HEALTHY control instance rebooting another instance.
- **Keep serving until the new pods are Ready.** Before a self-patched roll or restart, the
  Restart step reads the portal Deployment's `spec.strategy` and `spec.replicas`
  (`IDeploymentUpdater.ReadRolloutStrategyAsync`, with the same GET and the same service account
  as the last-rolled read). It rolls ONLY when `RolloutStrategyReading.NonDisruptiveRefusal` is
  null, which needs:
  - a RollingUpdate whose `maxUnavailable` resolves to 0;
  - a `maxSurge` that resolves to at least 1 for the declared replicas.

  Kubernetes rounds a percentage down for maxUnavailable and up for maxSurge, and an unset value
  is 25%. `Recreate`, the 25%/25% default on more than three replicas, a zero surge, an
  unresolvable value and an UNREADABLE strategy are each refused by name, and nothing is rolled.
  The reboot itself never deletes a pod.
- **Verify that the singletons resumed.** A health check reported "Healthy" while the last pass of a
  singleton was 49 minutes old, so "healthy" is not evidence. `health:singletons-resumed`
  (`SingletonsResumedRebootCheck`) waits, on each node's own stream and for at most
  `SingletonResumeBudget` (default 35 min), until every configured pass stamps an instant NEWER
  than the restart:
  - the PR babysitter: `Hosting/Babysitter`.`lastRunAt`;
  - the PR review sweep: `Hosting/Triage/Status`.`lastPrSweepAt`.

  The check is red when a pass did not land in time, naming the singleton and its stale last pass.
  A singleton whose node this instance does not have is reported not measured, by name. The list
  is `InstanceRebootOptions.SingletonPasses`. The checks of one process run side by side, so this
  wait does not delay the others.

## What is NOT established

- **Without the smoke check, every reboot is `Failed` at Verify — by design.** The thread-start
  smoke check ships in MeshWeaver.Plugins (the AI module). On a host where it is not registered yet,
  every counted process reads "no smoke check was measured (none registered on this host)", and
  the request ends `Failed` even when everything else is green. That is the instrument missing, not
  the instance broken: the rest of the verdict says which readings passed. The two halves therefore
  merge in order — this core half, then the MeshWeaver.Plugins half — and a green reboot is
  meaningful only once both are rolled.
- **No real cross-process run.** The scenario tests (`Memex.Portal.Shared.Test` → `InstanceReboot*`)
  run the real registry client, landing, reconciler, request node, executor, agent and
  self-updater in one monolith process. They simulate the process that boots after the restart,
  the generation it loaded, and the thread-start smoke check, which is a stand-in that throws the
  incident's exception when the old generation is bound. No Kubernetes roll, no multi-replica
  Orleans cluster and no real GitSync import were exercised.
- **"Newest promoted image"** is read as "the newest image this instance's update policy admits and
  its availability gate clears". On the `Stable` channel that is the newest promotion tag. On
  `Continuous` it is the newest continuous build the record's pattern admits.
- **A roll handed to the control lane** is subject to the control plane's own handling of
  `self-update-available`: approval on a gated record, and its dedupe. Until that roll lands, the
  reboot waits in `AwaitingRestart` and is not retried. A roll that waits for approval longer than
  the request's execution window ends the request as `Failed` (stalled); a roll approved after that
  still happens, but its verification is not recorded on the ended request.
- **"Executor gone" is not a separate leg.** The executor runs on the request node's own hub, and
  any read of the request re-activates it, so the watchdog's own read resumes an orphaned request.
  An executor that is resumed and still makes no progress is caught by the execution window.
- **The build-instance request in MeshWeaver#6625 was never read**, so which of the shapes above held
  it open there is not established.
- **The compile leg reads a shared field.** The `compilationStatus` is one field over every replica's
  compile, so the leg does not see a failure on a single replica only.
- **Two replicas' watchdogs** could both fire within the seconds the request listing trails the
  store. Each process's own last-firing memory closes this only for that process.
- **The rollout check covers the SELF-PATCH path only.** When an ordinary instance hands its roll
  to the control lane, the control plane's `Roll`/`Restart` executes it, and this check does not
  run there. Asserting the strategy on that path is the control plane's job and is not built here.
- **A singleton whose node is absent is not measured, not red.** On a control instance whose
  babysitter node is missing altogether, the check therefore names it but does not fail.
