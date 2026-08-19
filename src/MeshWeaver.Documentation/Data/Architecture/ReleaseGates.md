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

## 🚨 The rule: NOBODY REBUILDS AN UPSTREAM FROM SCRATCH

Everything below is machinery in service of one directive, so it is stated first and plainly:

> **A repo builds only what it OWNS. Every dependency is consumed as a RELEASED ARTIFACT — never
> rebuilt, never compiled from source, never staged out of a source checkout.**
>
> **And it builds only once ALL of its dependencies have released.** Not "usually", not "when
> convenient": a repo woken before its upstreams are published EXITS without building and is woken
> again by the upstream's publication.

Two consequences people get wrong, both of which look harmless in a green pipeline:

- **Checking out an upstream's SOURCE to build against it is a rebuild.** It does not matter that
  the checkout is cheap or that the mesh compiles it lazily — the dependent is now producing its
  own copy of the upstream, from a commit nobody released, against a framework nobody gated. Two
  repos then ship different bytes for the same package and the difference surfaces at a customer's
  next boot, not in CI. Education's e2e is the live example: it checks out
  `Systemorph/MeshWeaver.Plugins` and lets the mesh Roslyn-compile it (17–34 min per run), which is
  a full rebuild of an upstream wearing the costume of a test fixture.
- **"It passed" is not evidence of the ordering.** A dependent that builds before its upstream has
  released usually still goes green — it simply gated against the PREVIOUS release. The gate exists
  because that outcome is indistinguishable from a correct one until the mismatch reaches a pod.

**Binaries travel exactly one way: through the registry.** A released DLL is fetched from
`memex.meshweaver.cloud/api/plugins/bundles/…`, never from a sibling checkout, a build artifact
passed between workflows, or a rebuilt `bin/`. The registry is where a release becomes a thing that
exists for anyone other than the run that produced it — one credential model, one entitlement
check, one set of bytes. See [Plugin Registry](../PluginRegistry).

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

## The build gate — exit, don't wait

A repo must not build against a released framework until every upstream it depends on has published
for that framework. Otherwise it compiles and gates its content against an upstream that no longer
matches: the publication comes out clean and the verdict means nothing.

The gate lives at the receiving end of the cascade, in the reusable `node-repo-publish-bake`
workflow every node repo calls. A caller declares its upstreams:

```yaml
with:
  bake-source: education
  upstream-sources: plugins          # exit unless `plugins` is published for the target identity
  dependent-repos: Systemorph/MeshWeaver.Reinsurance
```

Before staging or baking anything the job resolves the identity it is about to target — from the
**image**, with `mw-plugin-test --print-framework-identity`, because that is the only place an
identity exists — and asks the same availability question the deployment gate asks, through the same
script (`.github/scripts/check-release-availability.sh`).

### Not ready ⇒ EXIT. Not a poll, not a sleep, not a bounded re-check.

The job ends immediately: no runner burned, no partial build, no false green. It is re-triggered by
the **event** its upstream fires when it publishes. That is what turns the platform's concurrent
fan-out into a correct topological order with no central scheduler:

```
platform ──▶ plugins (no upstreams)          builds, publishes
               ├──▶ education   (had exited) builds, publishes
               │      └──▶ reinsurance
               └──▶ socialmedia (had exited) builds, publishes
```

🚨 **The exit is RED, deliberately.** GitHub renders a skipped job with the same tick as a passed
one, so "upstreams not ready, did not build" must never be a grey skip or a silent success. A red
here is also simply *true*: this repo has not been rebuilt for this release yet.

### The edge that makes it work: a satellite wakes its own dependents

Until now only the **platform** dispatched. A downstream repo that exited was waiting for an event
nobody fired, so the very first satellite→satellite dependency would have deadlocked on first use.
So `node-repo-publish-bake` now fires `meshweaver-upstream-published` — carrying
`{source, identity, version, sha}` — to each repo in `dependent-repos`, **after** the publication it
announces has sealed.

Two rules keep that edge honest:

- **Declaring `dependent-repos` declares an obligation**, so its token is asserted in preflight and
  a missing one fails RED. Without the token those repos never wake, and nothing anywhere would be
  red about it.
- **A failed dispatch fails the job**, unlike the platform's reporter-class notification. A lost
  wake-up leaves a repo that already exited with nothing to re-trigger it — the terminal-exit
  failure this design must not have. The publication is already sealed and idempotent, so re-running
  is safe.

Receivers subscribe to both event types:

```yaml
on:
  repository_dispatch:
    types: [meshweaver-framework-released, meshweaver-upstream-published]
```

### Build only what you own — the half that is not done

The directive is that a repo builds only its own content and consumes upstreams as **pre-built
released artifacts** — dissolving the staging machinery (`stage-repo`/`stage-modules`) in every
satellite. `upstream-sources` above is the half of that which exists: the availability gate is
precisely the exit condition such a build needs.

The other half is blocked on the artifact's shape, and it is worth stating exactly:

> A published bundle carries **compiled assembly bytes keyed by node path**, plus a manifest
> (`BundleWriter.Write`). It does **not** carry the upstream's node definitions. But a satellite
> needs those definitions, not just the bytes: its package roots are typed by an upstream type
> (`nodeType: Store/Plugin`), and its NodeTypes' `sources` queries reach into upstream packages
> (`@Edu/…`). Seeding an assembly only stamps a node that already exists — with no upstream nodes in
> the tree there is nothing to stamp, and the roots do not bind.

So dropping staging requires the published artifact to carry the upstream's node definitions
alongside its assemblies. Until it does, `stage-repo`/`stage-modules` stay, marked transitional; the
gate already refuses to build when the upstream artifact is missing, which is the behaviour that
does not change when the artifact grows.

### What closing it looks like

The gate (does the upstream exist?) is done. What is missing is the FETCH — a way for a build to
take the released bytes rather than remake them.

#### 🚨 A publisher NEVER knows its dependents — a release is a BROADCAST

State this before the mechanics, because the obvious implementation is the wrong one. It is
tempting to let each repo name the repos it must wake (`dependent-repos`), and that list is exactly
what must not exist:

> **A repo declares its UPSTREAMS. It never declares its DOWNSTREAMS.** A release is PUBLISHED —
> announced once, to everyone — and whoever depends on it reacts. Publishing is not addressed.

Why the addressed form is a trap, in the order these bite:

- **A dependents list is edited in the wrong repo.** Adding a satellite means changing its
  UPSTREAM's configuration — so the person who knows about the new dependency is not the person who
  owns the file that has to change.
- **It rots silently, and silence is the failure mode.** A missing entry is a downstream that never
  rebuilds for a release. Nothing goes red: the release is green, the publisher is green, and the
  omitted repo simply keeps shipping against the previous framework until something notices at a
  customer's boot.
- **It duplicates a graph we already have.** Every package root already declares `requires`, and
  `PackageGraph.Levels` already derives leaves-first order from those declarations. A YAML list of
  dependents is a SECOND copy of that graph, maintained by hand, free to disagree with the first.

#### memex is the release bus, and it holds the graph

The registry is the one place that already knows both halves — what has been released, and who
depends on what:

- **The graph** is `requires` on each package root, which memex holds for every installed package;
  `PackageGraph.Levels` / `DependencyCheck.For` already answer "who depends on this" and "in what
  order" from it. Nobody re-declares it in CI.
- **The publication** is the release landing in the registry — the same
  `POST /api/plugins/bundles/{plugin}` hop the module lane already uses. Publishing IS the
  broadcast; there is no separate announcement to keep in step with it.
- **The subscribers** register themselves. A CI process registers with the registry exactly as an
  instance does and is issued an `mwi_` key (`InstanceRegistryAuthenticator`); being woken is then a
  property of being registered and depending on the thing, not of appearing in someone's list.

So the cascade reads: a publisher announces to memex → memex resolves the dependents from the graph
it already holds → the registered builds for those packages are woken → each checks its own
upstreams are released and builds. The publisher names nobody.

#### The pieces, in the order they unblock each other

1. **The artifact carries node definitions.** `BundleWriter.Write` gains the upstream's node repo
   beside the assemblies, so a consumer gets both the bytes and the definitions its roots are typed
   by. Blocking: every other piece is inert without it.
2. **A CLI fetches it.** The inverse of the `module-pack` tool CI already runs
   (`MeshWeaver.Plugin.Build`): a command that pulls a released package from the registry into a
   local directory, so a satellite replaces its `stage-repo`/checkout with one fetch of what was
   actually released. The mechanism belongs IN THE TECH, not in each repo's YAML — five hand-rolled
   fetches would drift, and the fetch must make the same floor/identity checks the seeder makes.
3. **CI registers as a consumer.** No new entitlement path: a build is a registered reader, and what
   it may read stays governed by its `PluginGrant`. This registration is also what makes it
   addressable by the broadcast.
4. **The instance is issued a blob access token.** Serving every byte through the portal is right
   for an install and wrong for a build fleet pulling whole packages. ⚠️ This deliberately REVERSES
   today's stance in `PluginBundleEndpoints` ("the portal serves the bytes rather than handing out
   storage access"), whose objection is that a scoped SAS is a second entitlement path to keep
   honest. That objection is answered by issuing the token AS the entitlement — minted per
   registered instance, expiring, revoked with the grant — not by adding a credential beside it.

Until these land, a satellite still stages from source, and that staging is the one sanctioned
exception to the rule at the top of this page — marked transitional everywhere it appears so it is
never mistaken for the intended shape. The same applies to the `dependent-repos` input on
`node-repo-publish-bake` and to CD's `BAKE_SUBSCRIBER_REPOS`: both are ADDRESSED notification, both
are transitional scaffolding for a broadcast that does not exist yet, and **neither may be extended
to new repos.** Wiring one more dependent into a list is not progress toward this design; it is one
more copy of the graph to keep honest.

#### Why this is also the cure for the recompile leak

`NodeTypeRecompileAlcLeakTest` measures live hubs climbing (~+5 per recompile) — accumulation that
looks like a leak to hunt. The recompiles it counts are the symptom: an instance Roslyn-compiles
content at boot precisely when no sealed bake is available for its framework, which is the state
this page's gate exists to prevent. Every path that ends in "somebody rebuilt what should have been
fetched" ends in another ALC. Fixing the distribution — released artifacts, fetched, never
rebuilt — removes the recompiles rather than making each one cheaper.

## See also

- [CI Content Bake](../CiContentBake) — where the sealed bundles and the framework identity come from
- [The Continuous Delivery Contract](../ContinuousDeliveryContract) — the publication this gate reads
- [Release & Self-Update Strategy](../ReleaseStrategy) — the poll, the policy node, the roll
- [Modules](../Modules) — the `MinMeshVersion` floor and why modules are not MVID-gated
