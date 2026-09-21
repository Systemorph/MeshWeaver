---
Name: The Self-Update Schema Wall
Category: Architecture
Description: Why every schema-bumping release is un-takeable by self-update, why the resulting wedge is invisible from outside, and the three conditions a tag must clear before it is a safe helm target.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 9.3h18"/><path d="M3 14.7h18"/><path d="M9 4v5.3"/><path d="M15 9.3v5.4"/><path d="M9 14.7V20"/></svg>
---

# The Self-Update Schema Wall

> ✅ **Rule change, 2026-09-07 (maintainer) — [Module Adoption Policy](../ModuleAdoptionPolicy), implemented by [#3648](https://github.com/Systemorph/MeshWeaver/issues/3648), [#3649](https://github.com/Systemorph/MeshWeaver/issues/3649), [#3650](https://github.com/Systemorph/MeshWeaver/issues/3650) and [#3651](https://github.com/Systemorph/MeshWeaver/issues/3651).** This page describes the mechanism as it runs after those changes: a declared floor is advisory, a refused generation falls back to the previous one, a new build is adopted eagerly, and a platform roll is held only by a module that provably cannot load on the target.

**A roll that carries the IMAGE and nothing else meets the first release that bumps the database
schema and stops there — still serving, at the OLD build, with nothing on the outside to say it
stopped.** The self-updater no longer makes that roll: it runs the migration first, or refuses. An
operator `Roll` still does, and so does any install whose migration leg cannot run — and the failure
is the same one every time, which is why the property outlived the mechanism that caused it.

This page is about the *property*, not the incident: which releases an instance can take by itself,
which it cannot, and how to pick a target when an operator has to carry one across. The
runbook-level mechanics — the exit code that lies, the rollout strategy that saves the service, the
guard that keeps the updater honest — are on
[Deployment — AKS](/Doc/Architecture/DeploymentAKS) → "Migration under self-update", and are not
repeated here.

## The mechanism, in four lines

| | |
|---|---|
| The updater patches | `memex-portal-deployment` (container `memex-portal`) — **one** workload, and it says so in its own success line |
| The migration is | a run-once `Job`. The chart renders one per Helm revision; the self-updater mints its own, `memex-migration-su-<tag>`, from the same ConfigMap and Secret — see [Database Migration Procedure](../DatabaseMigrationProcedure) |
| The portal checks | `DbVersionGate` reads `admin.mesh_nodes.db_version` **once** at startup and, if it is below the `ExpectedDbVersion` compiled into the build, logs `Critical` and stops the application |
| So a roll that moves the image without the schema is | image-forward, schema-behind — the new pod fails closed and exits, and the rollout never completes |

CD *does* build and push a correctly tagged `memex-migration` image on every run, tag for tag with
`memex-portal-ai`. That was never the gap. **The gap was that no continuous path ran it** — there was
no migration leg for self-update to get wrong, because there was no migration leg at all. The
self-updater grew one (`IDeploymentUpdater.RunMigrationAsync`, the `memex-migration-su-<tag>` Job),
and the paragraphs below are about what remains: the two states in which that leg cannot run, and the
one actor that still patches images with no leg at all.

## Why it is invisible

The same rollout policy that protects the service is what hides the failure.

With `maxUnavailable: 0` and `maxSurge: 1`, the previous ReplicaSet keeps serving while the new one
never reaches Ready. The Deployment then reports:

```text
Available=True     MinimumReplicasAvailable
Progressing=False  ProgressDeadlineExceeded
```

Every outside-in probe — the portal answers, users work, `/alive` is green — reports health, because
the instance *is* healthy. It is healthy at the **old build**.

On `memex`, 2026-09-03 ([#3207](https://github.com/Systemorph/MeshWeaver/issues/3207)), three
self-updates wedged this way between 07:20Z and 10:27Z (`ci.7647`, `ci.7651`, `ci.7658`), the last
one on a 5-minute back-off at 14 restarts, each attempt writing a ~685 MB core dump. Nothing
alerted. It surfaced only because an unrelated config-drift audit went looking at the ReplicaSets.

> 🚨 **"The portal is serving" and "the portal took the release" are different questions, and health
> only answers the first.** The signal that discriminates is the **running image tag** — read it back
> off the Deployment and compare it with the newest promoted tag — plus a rollout sitting at
> `ProgressDeadlineExceeded`. That is the same rule as
> [verify the IMAGE, never the tick](/Doc/Architecture/ContinuousDeliveryContract), pointed at the
> cluster instead of at the pipeline.

## Why it is structural, not a bad build

None of `ci.7647`, `ci.7651`, `ci.7658` is defective. Any release that raises `ExpectedDbVersion`
produces exactly this, and every release that does not raise it is taken normally. The wall is a
property of the update mechanism, not of a build.

Two consequences follow, and both are easy to miss:

- **Clearing one instance at one tag clears TODAY, and nothing else.** An out-of-band `helm upgrade`
  runs the Job, the schema advances, the already-attempted portal starts — and the instance resumes
  self-updating until the *next* schema bump. That cure is per-occurrence by construction and does not
  change the property; what does change it is the instance owning a migration leg of its own, which is
  the section below. Where the leg cannot run, the per-occurrence cure is still the cure — the
  difference is that the instance now says so before rolling rather than after.
- **An instance that has not hit the wall is not configured differently — it has not arrived yet.**
  `memex-cloud` served `ci.7621` and was healthy on the same day, for the single reason that no build
  it had selected needed schema 55. Its next selection past `ci.7647` meets the wall identically.
  Every install stands on the same wall; only the arrival time differs.

So the question worth asking of the fleet is not "is anything wedged?" but **"for each instance, is
its `db_version` below the expected version of the newest release it would select?"** — the instances
that answer yes are already behind the wall whether or not they have noticed.

## The control instance stands on it too

Since [#3185](https://github.com/Systemorph/MeshWeaver/pull/3185) the release wave belongs to
memex: every publishing pipeline ends with one call to the control instance, which **registers** the
release as a durable node and **publishes** `meshweaver-framework-released` /
`meshweaver-upstream-published` to the subscribed repositories. The whole shape is in
[Deployment](/Doc/Architecture/Deployment) → "How a release reaches the fleet" and
[The Release Event Bus](/Doc/Architecture/ReleaseEventBus).

Be precise about what the wall does to that, because the alarming reading is the wrong one:

- **The control plane does not stop.** The wedge freezes the *image*; the old pods keep serving, so
  the inbox keeps accepting, registering and broadcasting throughout.
- **What it does do** is let the control instance fall arbitrarily far behind the platform it
  announces. Every subsequent release it registers and broadcasts to the fleet is one it cannot take
  itself, and any change to the control plane's own machinery — an inbox watcher, the broadcaster,
  a schema-backed registration table — that ships in a schema-bumping release cannot reach the
  instance that runs it without an operator.

That is the structural gap in the release-wave story: the one instance the fleet's delivery
coordination depends on has no self-service path across a schema boundary. Tracked alongside
[#3207](https://github.com/Systemorph/MeshWeaver/issues/3207);
the wave itself is `Systemorph/MeshWeaver.Plugins#1241` (merged) and `Systemorph/Memex#173` (open).

## What makes a tag a safe target

Whenever an operator does carry an instance across the wall, **picking the target is a separate
problem from running the upgrade** — and the checks that sound sufficient are not.

A safe target clears all three of these, each **measured**, none inferable from the tag:

| # | Condition | How you establish it |
|---|---|---|
| 1 | It carries the code fix you need | **By ancestry** — is the fix's merge commit an ancestor of the tag's build commit? Tag ordering is not ancestry, and a higher `ci.<N>` is not evidence |
| 2 | `memex-migration` exists at the **same** tag | `helm-release.yml`'s own `--set` block insists on it, in its own words: *a migration from a different build than the code that will run against it is how a schema lands half-applied* |
| 3 | `Plugins: bake + seal the publication for this identity` is **GREEN** on that tag's CD run | The seal is what publishes the plugin modules for the platform identity — the builds the roll adopts — and, since MeshWeaver#3651, the `platform-surface.json` the release gate links every *other* landed module against. Its absence is invisible in the registry. (An unsealed **content** bake is no longer a reason the gate holds: the instance compiles at boot and the tab says so — but a seal that is missing its *module* builds leaves the instance on its landed generations, and whether those load is what the surface decides) |

Condition 3 is the one that gets skipped, because two other checks sound like it and are green
without it. On 2026-09-03, CD runs `33746020109` (`ci.7669`) and `33749847612` (`ci.7674`) both
recorded:

```text
Promote: tag the full set (all-or-nothing)                 success
Verify every image shipped                                 success
Plugins: bake + seal the publication for this identity     FAILURE
  └─ Register the publication with memex                   skipped
```

Both seals FATALed on the one-producer guard (#3175). Both tags are therefore **platform-present,
plugin-modules-absent** for their framework identity — precisely the half-broken state the standing
availability rule forbids: *all plugins deployed to an instance must be available for the correct
platform version; if not, nothing goes*
([Release Availability Gates](/Doc/Architecture/ReleaseGates)). A session preparing the remedy above
named `ci.7674` as verified having confirmed that three platform images existed, and retracted it
before anyone acted. **Image count was never the right question.**

Note the skipped step in that transcript: when the seal fails, the publication is never registered
with memex, so the availability predicate has no record to read. The seal and the availability
check are one fact seen from two sides, which is why one call answers condition 3 for a live
instance — ask the portal, per
[Deployment — AKS](/Doc/Architecture/DeploymentAKS) → "Self-update ops":

```bash
curl -s -H "Authorization: Bearer $MWI_KEY" \
  "https://<portal>/api/plugins/is-updatable?version=<tag>" | jq
```

`isUpdatable: false` names the blocking packages; `indeterminate: true` means the check could not
run and is **not** clearance. Why a green Promote can sit above an unsealed publication at all is
[Bake Identity Mismatch](/Doc/Architecture/BakeIdentityMismatch) and
[The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) → "A promoted tag is
not a deployable tag".

## The durable remedy: option (a), and it is now the self-updater's own leg

Of the three options this page used to list as open, **(a) was taken** — the instance runs the
migration itself, from inside the pod, as a `Job` it mints (`memex-migration-su-<tag>`) rather than a
credential or a workflow trigger it holds. The blast radius the option was feared for stayed small:
one extra RBAC rule (`batch/jobs create,get,list,delete` on `memex-portal-sa`) instead of a cluster
credential. The routine is [Database Migration Procedure](../DatabaseMigrationProcedure); this page
keeps only the part that is still a WALL.

**What is still a wall is not the mechanism but the two states in which it cannot run — and, since
[#4764](https://github.com/Systemorph/MeshWeaver/issues/4764), the poller no longer patches blind in
either.** Only `MigrationRunOutcome.Completed` proves the schema moved, so:

| State | What the poller does | Where it says so |
|---|---|---|
| `Forbidden` — 403 on the Job POST; the install has not been `helm upgrade`d since the RBAC rule landed | **REFUSES the roll** (`SelfUpdateOutcome.MigrationUnavailable`) | `lastCheckVerdict` on `Admin/UpdatePolicy`, naming the missing permission and the `helm upgrade` that grants it **and** runs the migration; `LogCritical` |
| `NotSupported` — the installed `MeshWeaver.SelfUpdate.Aks` generation predates the seam, so no migration is possible at all | **rolls**, and records that it rolled blind (`applied update … UNMIGRATED — …`) | the same field, naming the module to update; `LogWarning` |

🚨 **Both of the portal's own routes are held to that rule, and for a while only one of them was.**
There are two places in the portal that patch the image: the poller, and the Updates tab's manual
**Apply** button. The button honoured the release-availability gate, the combo gate and the
control-lane route — and had no migration step at all, so an admin click made exactly the image-only
roll the poller had stopped making, and left no verdict anywhere to inspect afterwards. That is what a
per-route `switch` statement costs. The decision is now ONE predicate
(`SelfUpdateVerdict.MayPatchAfter`) that both routes read, with a test that drives every outcome
through the poller and asserts its behaviour equals the predicate — so an outcome added to the enum
cannot be classified one way in one route and another in the other.

The asymmetry is deliberate and is the whole judgement. A 403 is a state an operator clears with the
very command that also moves the schema, so refusing asks for nothing that was not already owed. "This
install can never migrate" is not like that: refusing there would freeze the install for ever, and
silently, which is the worse failure shape
([#2553](https://github.com/Systemorph/MeshWeaver/issues/2553)) — so the roll goes, and what changes is
that it stops being indistinguishable from a migrated one.

> 🚨 **Why this is a blanket rule and not a version comparison.** The natural formulation — *refuse
> when the target's expected `db_version` exceeds the database's* — is not available to a portal, and
> that is structural: `DbVersionGate.ExpectedDbVersion` is a constant compiled INTO each build, so the
> pod running the OLD image cannot read the NEW image's number. **Running the migration IS that
> comparison, executed rather than computed**, which is why "could not run it" and "do not know" are
> the same fact here. Making the expected version a published property of a release — option (b)
> below — is what would let a refusal name two numbers instead of one missing capability.

### Measured, 2026-09-19 (memex-cloud)

Image `3.0.0-ci.8411` (commit `c84c6c05`, predating `DbVersion.Latest = 56`). A `Roll` to
`3.0.0-ci.8955` — the same single `set image` write the self-updater makes:

```text
pod memex-portal-deployment-65468bfccf-68g7f   0/1  CrashLoopBackOff   restarts=2 by 05:06Z
crit: Memex.Portal.Distributed.DbVersionGate[0]
crit: Memex.Portal.Shared.MemexConfiguration[0]  Startup was cancelled … Exiting with code 1 after 3319 ms
Deployment: generation 1402, desired 4, ready 4 (all on the OLD set), updated 1, unavailable 1
```

No migration Job in the namespace. Nothing converged, nothing rolled back, and the record still said
the roll was made. Note what the numbers say: the instance was *serving*, on four healthy old pods, at
full capacity — which is why no outside-in probe and no availability metric could see it.

## What is still open — and it is not in this repo

**The actor that produced the 2026-09-19 reading was not the self-updater; it was an operator `Roll`
`Hosting/InstanceAction`**, which patches the image and nothing else. So the remaining gap is the
Plugins/control-plane half, and it has a precise shape:

- **(b1) The record-driven `Reconcile` already runs the migration — make the `Roll` plan include it.**
  A `Reconcile` re-applies the chart at the image the Deployment carries, and a `helm upgrade`
  renders `memex-migration-<revision>`, which is why "`Roll`, then `Reconcile`" is the operator
  workaround the measurement used. The fix is for the `Roll` plan to *contain* that step — migrate at
  the target tag, wait for `Database migration completed. Version: N`, then set the image — so the
  operator route has the same ordering the in-pod route has had since the seam landed. Until it does,
  a `Roll` across a schema bump is `Roll` + `Reconcile`, in that order, by hand.

  **The operator half is `hosting-migrate`** (`deploy/aks/operator/bin/`, 2026-09-21): it reads the
  migration Job the release itself rendered (`helm get manifest` — wait-for-postgres, rehearsal,
  budget, envFrom and pull Secret all kept), moves only the migration containers to the target tag,
  runs it as its own Job `memex-migration-roll-<tag>`, and exits non-zero unless the Job
  **succeeded** — so the `Roll` plan's `set image` sits behind a migration that demonstrably ran,
  outside the portal. Idempotent per tag: a succeeded Job is reported, a failed one is deleted and
  run again. The `Roll` plan calling it is the Plugins half; until an operator image carrying the
  command is on `hosting-operator:main`, that plan step would stop with *command not found* — before
  the image moves, which is the safe side.
- **(b2) The `Deployments/<name>` record must carry the verdict.** The in-pod path reports on
  `Admin/UpdatePolicy`; an operator `Roll` reports on the instance record, and a plan step that
  patched an image the new pods then refuse must land there as a failure rather than as a completed
  action. This is the same ask as #4764's second one, one level up.
- **(b3) Publish the expected schema version with the release.** The original option (b): make
  `ExpectedDbVersion` readable from outside an image — a release-marker field — and both halves can
  refuse (or clear) by comparing two numbers, which is strictly better than refusing on a missing
  capability. It also lets the fleet answer the question at the end of "Why it is structural" without
  rolling anything.

Two constraints any answer has to respect, both standing directives:

- **Rolls go through CD.** The first remedy proposed on #3207 — running `helm-release.yml` against a
  hand-picked `image` — was **withdrawn** by its own author for exactly this reason: a hand-pinned
  tag is what the roll-via-CD directive exists to prevent, and a portal serving a consistently old
  build is a safe state, not an incident.
- **Fail-closed stays.** `DbVersionGate` refusing to serve ahead of its schema is the behaviour that
  keeps a half-migrated database from being written to. No option may weaken it; the argument is
  only about who runs the migration and when.

## See also

- [Database Migration Procedure](/Doc/Architecture/DatabaseMigrationProcedure) — the routine: who mints
  the Job, the one-time grant, the outcome table the poller decides on, and the recovery
- [Deployment — AKS](/Doc/Architecture/DeploymentAKS) — "Migration under self-update": the runbook
  detail, the `exitCode 139` that is really SIGABRT, and the guard that keeps the updater to one
  workload
- [The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — what a promote
  guarantees, what it does not, and why the reconciler does not heal a failed bake publication
- [Release Availability Gates](/Doc/Architecture/ReleaseGates) — the one predicate, and the release
  marker that makes a release's framework identity knowable from outside its own image
- [Bake Identity Mismatch](/Doc/Architecture/BakeIdentityMismatch) — how a fully green CD run can
  publish a bake no portal will ever adopt
- [Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) — update policies, channels,
  and what an install selects
- [The Release Event Bus](/Doc/Architecture/ReleaseEventBus) — the event is a wake-up, the mesh is
  the truth
