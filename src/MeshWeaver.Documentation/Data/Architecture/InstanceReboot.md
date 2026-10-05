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
   skipped, with the reason.
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

## Who can start one

| surface | authorisation | how |
|---|---|---|
| MCP `reboot_instance` (MeshWeaver.Plugins `McpMeshPlugin`) | `IsGlobalAdmin` | `MeshOperations.RebootInstance(reason, wedged)`. Returns `{status, path}` at once; read the node for the outcome |
| the **Reboot** button on a deployment's page (MeshWeaver.Plugins `Hosting/Deployment`) | `IsGlobalAdmin`; the click is the signature | files a `Hosting/InstanceAction` of kind `Reboot`. For this instance, the action files the request in-process; for another instance, it goes through the control lane |
| the control lane (`ControlLaneOperation.Reboot`, `RebootOperation`) | the control instance signs with the deployment's own key | the plan is fixed, with ONE step (`RebootOperation.PlanFor`), so its digest is bound without a dry run, and the requester is the approver. The lane run ends when the request is FILED on the target, and the reboot then reports on its own node there |
| the instance's own watchdog | none; rate-limited and alarmed | see below |

## Self-trigger: the watchdog

`RebootWatchdog`, armed on every process's mesh hub, evaluates once per `Reboot:WatchdogInterval`
(default 1 minute). The predicate is explicit and pure (`RebootWatchdogRules.Evaluate`), and it
holds when EITHER leg holds:

- **thread-start leg:** at least `ThreadStartFaultThreshold` (default 3) thread starts failed with a
  load or binding fault within `ThreadStartWindow` (default 15 minutes). Load and binding faults are
  `MissingMemberException` (method or field), `TypeLoadException`, `BadImageFormatException`, and a
  `FileNotFoundException` or `FileLoadException` for an ASSEMBLY, unwrapped from aggregate,
  invocation and type-initialisation wrappers. A component reports these faults through
  `hub.ReportWedgeFault(WedgeSignalKinds.ThreadStart, source, exception)`. Anything else is
  dropped at the sink: a model error or a refusal is never evidence of a wedge.
- **compile leg:** a critical NodeType is in compile Error on EVERY pass for at least
  `CompileErrorFor` (default 20 minutes). The reading is the shared `compilationStatus` field,
  queried as System. A pass that cannot read RESETS the leg, because no evidence is not evidence of a
  wedge.

The rate limit is `RebootWatchdogRules.Admit`. It refuses to fire in three cases:

- while any reboot is still open;
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
  reboot waits in `AwaitingRestart` and is not retried.
- **The compile leg reads a shared field.** The `compilationStatus` is one field over every replica's
  compile, so the leg does not see a failure on a single replica only.
- **Two replicas' watchdogs** could both fire within the seconds the request listing trails the
  store. Each process's own last-firing memory closes this only for that process.
- **The rollout check covers the SELF-PATCH path only.** When an ordinary instance hands its roll
  to the control lane, the control plane's `Roll`/`Restart` executes it, and this check does not
  run there. Asserting the strategy on that path is the control plane's job and is not built here.
- **A singleton whose node is absent is not measured, not red.** On a control instance whose
  babysitter node is missing altogether, the check therefore names it but does not fail.
