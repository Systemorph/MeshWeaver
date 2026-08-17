---
nodeType: Markdown
name: Release Availability Gates
category: Architecture
description: One predicate — is every package available for the target release? — and the release marker that makes a release's framework identity knowable outside its own image.
icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#0d47a1'/><path d='M12 3l7 4v6c0 4-3 6-7 8-4-2-7-4-7-8V7z' fill='none' stroke='white' stroke-width='1.8'/></svg>"
---

# Release Availability Gates

A release is not safe to act on just because its version is newer. Two questions have the same
answer, and until they were asked, both were answered by hand:

- **Deploying** — before an environment is rolled to a release, every package that environment
  deploys must have a usable artifact *for that release* (#1754).
- **Building** — before a repo builds against a released framework, every upstream it depends on
  must have published *for that framework* (#1755).

Both reduce to one predicate: **given a target release, and given a set of packages, is every one
of them available?** It is stated exactly once, in
`MeshWeaver.PluginCatalog.ReleaseAvailability.IsUpdatable`, and read by every caller — because a
rule only one path honours is not a rule.

## "Available" has exactly two forms

| Kind | Gate | Why this one |
|---|---|---|
| **Content package** | a SEALED bake under the target's framework identity — `prebuilt-bundles/<identity>/<source>/` with its `_complete` sentinel | Absent, the instance Roslyn-compiles that content at boot: the regression the [CI content bake](../CiContentBake) exists to prevent, and a type that fails to compile parks its hub for the whole activation budget |
| **Compiled module** | its `MinMeshVersion` FLOOR, satisfied by the target version | A module binds by simple name and its contract is API compatibility, so the floor expresses it. MVID has been diagnostic-only for modules since the bundle lane landed — see [Modules](../Modules) |

The strict-MVID rule stays where it belongs, on content bundles, in
`PrebuiltAssemblySeeder.DeclineReason`. Applying it to modules would forbid every ex-post Store
install across platform versions.

## The release marker — how a release's identity becomes knowable at all

A framework identity is a property of the **binaries**: the image resolves it from its own surface
manifest, so nothing outside that image can compute it from a tag or a commit. Rather than guess,
the publisher **records** it. `publish-bake-bundles.sh` writes, beside the bundles:

```
<base>/prebuilt-bundles/_releases/<platform-version>   → a file holding that release's identity
<base>/prebuilt-bundles/<identity>/<source>/<bundle>.zip
<base>/prebuilt-bundles/<identity>/<source>/_complete   ← written strictly LAST
```

The marker's name is the platform version the self-updater compares; its content is the framework
identity. A reader therefore needs one stat and one read — no listing, no ACR archaeology.

Two properties are load-bearing:

- **It is written on every run, outside the already-published skip.** The bundle publication skips
  when content × framework both match, and the API-surface identity is breaking-change-keyed, so an
  ordinary release re-resolves the *same* identity and skips the upload. If the marker rode along
  with the bundles, the second and every later release of a surface generation would have no marker
  — and every environment would be held on a release that is in fact perfectly fine.
- **A missing marker means one precise thing**: that release published no platform content bake. So
  the honest answer is *cannot determine*, which is a HOLD.

## Fail safe, fail loud, fail distinguishable

> 🚨 "Cannot determine" is not "clear to proceed."

`PackageAvailabilityKind` keeps the three answers apart on purpose:

- `ContentBakeMissing` / `ModuleFloorExceedsTarget` — we looked, and this package cannot survive the
  release. Actionable: re-bake, or fix the floor.
- `Indeterminate` — we could **not** look: the catalogue was unreachable, or the release has no
  resolvable identity. An availability failure is never dressed up as a compatibility verdict; an
  operator must be able to tell an outage from an incompatible release.
- `UpdatabilityVerdict.NotEnforced` — the gate does not **apply**: this deployment consumes no CI
  bakes at all, so it already compiles at every boot and holding it could only freeze it forever.
  Deliberately not the same as passing — it carries a reason the caller logs and surfaces, so
  "nothing is gating this environment" is visible rather than inferred from a green tick.

## Both halves are REGRESSION checks, not absolute ones

A gate that holds an environment forever is a worse outage than the one it prevents. So neither
half asks "is this package perfect for the target"; both ask "would this roll take away something
that works today".

- **Content**: a package is treated as content-bearing exactly when it has a sealed bake under the
  identity the instance is running *now* — i.e. when its bytes are being adopted today. A package
  with no compilable NodeTypes produces no bundle ever, so demanding one would hold its environment
  forever.
- **Modules**: a floor is passed to the predicate only when the running platform already satisfies
  it. SemVer puts `3.0.0-rc4.ci.4049` **below** `3.0.0`, so a module declaring `minMeshVersion:
  3.0.0` is below floor on every `-rc` platform, including the one prod runs. Judged absolutely it
  would block every release; judged as a regression it fires exactly where it should — on a
  rollback below a module's declared floor.

## The verdict as a service

`ReleaseAvailabilityService` (memex) answers `IsUpdatable(targetVersion)` reactively — the
file-system leaves run on the I/O pool, never on a hub action block — and it is exposed for the
paths that roll a version from outside the portal:

```
GET /api/plugins/is-updatable?version=<release>
Authorization: Bearer mwi_…
```

```json
{
  "version": "3.0.0-rc4.ci.4049",
  "isUpdatable": false,
  "enforced": true,
  "indeterminate": false,
  "holdReason": "Store: no sealed content bake for framework identity s3779… …",
  "packages": [ { "package": "Store", "status": "ContentBakeMissing", "reason": "…" } ]
}
```

Auth is the instance key — the same `mwi_` gate as the bundle routes, failing closed, because the
response is deployment inventory.

**Scope, stated rather than implied:** an instance answers for **itself**. The registry records that
an instance exists (`InstanceAutoRegistrationService`) but not what it has installed, so no third
party can answer for it today; each environment is asked at its own URL. When per-environment
composition lands, the *declared* package set becomes answerable centrally, and
`ReleaseAvailabilityService` is the one place that changes — the predicate above does not.

**The consequence, designed for deliberately:** this makes the registry a dependency of an
environment's ability to update. When its catalogue cannot be read the answer is `Indeterminate` —
a hold, with its own named reason — never a pass, and never a compatibility verdict.

## The deployment gate — all three roll paths read the one verdict

A version reaches an environment three ways, and a gate honoured by only one of them is not a gate.

| Path | How it consults the verdict | What a refusal looks like |
|---|---|---|
| **The self-update poll** | `SelfUpdateHostedService` calls the service in-process after `VersionSelect` picks a target and before `KubernetesDeploymentUpdater` patches anything | the roll does not happen; the hold is written to `Admin/UpdatePolicy` (`HeldTag`/`HeldReason`/`HeldAt`) and shown on the Updates tab |
| **CD's promote** | `main-cd`'s `publish-bake` job asserts, right after publishing, that the release it armed resolves an identity and carries a sealed platform bake (`.github/scripts/check-release-availability.sh`) | the CD run goes **red**, naming what is missing — never a grey skip, which renders identically to a pass |
| **A manual roll** | the operator asks the target portal `GET /api/plugins/is-updatable?version=…` before `kubectl set image`; the in-product **Apply update now** button consults the same service | the button reports the hold and patches nothing |

### A hold is a state, not a silence

The refusal is written where update state already lives, so it renders on the surfaces an operator
and a user already look at:

- **About tab** (any user): `⏸️ Update held — a package this deployment runs is not available for
  that build`, deliberately distinct from `⬆️ Update available`. An install that has *refused* a
  build must not look like one that is about to take it.
- **Updates tab** (platform admin): the held tag, the reason naming the package, and when it was
  held — plus a different sentence when the hold is `Indeterminate`, because "we could not check"
  and "this package cannot survive the release" have different fixes.

### The hold is re-evaluated every tick, never persisted as a decision

The poller re-asks on every poll and on every green-build event, and clears the hold the moment the
verdict flips. So the missing bake being published is the whole remedy — nothing has to be un-stuck
by hand, and no operator has to remember that an environment was held. That is what makes refusing
safe to do at all.

### The gate never gates itself out of existence

Recording the hold is best-effort and can never block the roll (the same rule the availability
bookkeeping has carried since the 37-hour stall of #1020): the DECISION comes from the verdict, and
a gate that could not run resolves to a hold with its own reason rather than to an exception that
kills the tick. An install with no gate registered at all logs that fact and rolls — said out loud,
never inferred from silence.

## See also

- [CI Content Bake](../CiContentBake) — where the sealed bundles and the framework identity come from
- [The Continuous Delivery Contract](../ContinuousDeliveryContract) — the publication this gate reads
- [Release & Self-Update Strategy](../ReleaseStrategy) — the poll, the policy node, the roll
- [Modules](../Modules) — the `MinMeshVersion` floor and why modules are not MVID-gated
