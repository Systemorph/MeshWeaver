---
Name: The Self-Update Schema Wall
Category: Architecture
Description: Why every schema-bumping release is un-takeable by self-update, why the resulting wedge is invisible from outside, and the three conditions a tag must clear before it is a safe helm target.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 9.3h18"/><path d="M3 14.7h18"/><path d="M9 4v5.3"/><path d="M15 9.3v5.4"/><path d="M9 14.7V20"/></svg>
---

# The Self-Update Schema Wall

**A pull-based self-update carries the IMAGE and nothing else. So an instance rolls itself forward
release after release until it meets the first one that bumps the database schema — and then it
stops there, still serving, with nothing on the outside to say it stopped.**

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
| The migration is | a run-once `Job` the chart renders per Helm revision. Only `helm upgrade` mints one; nothing in the continuous path ever runs it |
| The portal checks | `DbVersionGate` reads `admin.mesh_nodes.db_version` **once** at startup and, if it is below the `ExpectedDbVersion` compiled into the build, logs `Critical` and stops the application |
| So a schema-bumping release is | image-forward, schema-behind — the new pod fails closed and exits, and the rollout never completes |

CD *does* build and push a correctly tagged `memex-migration` image on every run, tag for tag with
`memex-portal-ai`. That is not the gap. The gap is that no continuous path ever runs it: there is no
migration leg for self-update to get wrong, because there is no migration leg at all.

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
  self-updating until the *next* schema bump, where it stops again. The remedy is per-occurrence by
  construction; it does not change the property.
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
| 3 | `Plugins: bake + seal the publication for this identity` is **GREEN** on that tag's CD run | The seal is what publishes the plugin modules for the platform identity. Its absence is invisible in the registry |

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

## The durable remedy is an OPEN decision

**Nothing here has been decided, and this page deliberately does not pick.** The options on the
table, with what each one costs:

| Option | Shape | What it trades |
|---|---|---|
| **(a) Self-update triggers the helm run at a schema boundary** | The instance detects that its target needs a schema it does not have and drives the migration itself | Closes the gap end-to-end. Gives the in-pod updater a much larger blast radius — it would have to hold a credential or a workflow trigger that can mint a Job, which is exactly the surface the current one-workload design keeps small |
| **(b) Schema-bumping releases are FLAGGED, and the instance holds** | The release advertises the schema it needs; an instance that cannot satisfy it declines to roll and asks for the helm run instead of rolling into a wedge | Turns a silent stall into an explicit, reported hold — the fleet-visible state the current failure lacks. Does not remove the operator step; needs the expected version to become a published property of a release rather than a constant compiled into an image |
| **(c) Status quo — schema bumps are always operator-run** | Keep the property, make it legible and alarmed | Cheapest and safest. Leaves the control instance without a self-service path, and leaves "did anyone notice?" as a monitoring problem rather than a mechanism one |

Two constraints any answer has to respect, both already standing directives:

- **Rolls go through CD.** The first remedy proposed on #3207 — running `helm-release.yml` against a
  hand-picked `image` — was **withdrawn** by its own author for exactly this reason: a hand-pinned
  tag is what the roll-via-CD directive exists to prevent, and a portal serving a consistently old
  build is a safe state, not an incident.
- **Fail-closed stays.** `DbVersionGate` refusing to serve ahead of its schema is the behaviour that
  keeps a half-migrated database from being written to. No option may weaken it; the argument is
  only about who runs the migration and when.

The decision is the maintainer's. Until it is made, the operative facts are: schema-bumping releases
are un-takeable by self-update, an instance behind the wall is serving and safe, and a target chosen
to carry one across must clear all three conditions above.

## See also

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
