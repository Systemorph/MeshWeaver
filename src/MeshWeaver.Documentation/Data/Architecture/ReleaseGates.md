---
nodeType: Markdown
name: Release Availability Gates
category: Architecture
description: One predicate — is every package available for the target release? — and the release marker that makes a release's framework identity knowable outside its own image.
icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#0d47a1'/><path d='M12 3l7 4v6c0 4-3 6-7 8-4-2-7-4-7-8V7z' fill='none' stroke='white' stroke-width='1.8'/></svg>"
---

# Release Availability Gates

> 🚨 **Rule change, 2026-09-07 (maintainer) — see [Module Adoption Policy](@/Doc/Architecture/ModuleAdoptionPolicy).** The module floor is no longer a release gate, as a regression check or otherwise; a missing content bake is reported as a boot compile, not held; the only module-lane hold is measured loadability on the target. The mechanism described below is what runs until [#3648](https://github.com/Systemorph/MeshWeaver/issues/3648) and [#3651](https://github.com/Systemorph/MeshWeaver/issues/3651) lands; this page is rewritten by that change.

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

## 🚨 What holds a roll: a module that provably cannot load there — and nothing declared

> **Maintainer, 2026-09-07** (the Module Adoption Policy page (`Doc/Architecture/ModuleAdoptionPolicy`), implemented by
> MeshWeaver#3648 and MeshWeaver#3651): *if no plugin version is shipped, we use the old one. We
> make it load despite a newer dependency on the platform or on another module. As soon as a new
> module version ships, we start using it.*

On that day every production portal sat on its morning build (`3.0.0-ci.8009`) for the whole day.
Two rules held it, one after the other, and both were about something *declared* rather than
*measured*: the installed modules' `minMeshVersion` floors declined all eleven candidates ("77
plugins required … every one declined"), and behind them the satellites' bakes for the new identity
did not exist yet — although a boot compile of those courses succeeds on every pull request and every
candidate would have loaded. The predicate is now:

| Lane | What decides | What is reported, never decided |
|---|---|---|
| **Compiled module** | **Measured.** A module the target publishes a build of will be adopted at the roll — nothing to check. A module with no such build keeps its LANDED generation across the roll, so that generation's bytes are linked against the target's type surface — `platform-surface.json`, written by the bake inside the target image, read by [the link gate](../ModulePlatformLinkGate). **`Unlinkable` ⇒ `ModuleUnloadable`, THE hold**, naming the module and the missing type | `Linkable` clears. `Indeterminate` — no surface published, unreadable bytes — is an advisory: *"whether its landed module … loads could not be determined"*. The declared `MinMeshVersion` floor is an advisory too (MeshWeaver#3648) |
| **Content package** | Nothing, by default. A missing sealed bake under the target's identity means the instance Roslyn-compiles that content at boot — the same code path every pull request of it already proved green | `ContentBakeMissing` is a COST: *"would recompile at boot on …: education, crm"* (`UpdatabilityVerdict.BootCompiles`). `Modules:RequirePrebuilt` — the opt-in strict mode in which the seeder refuses a boot compile — keeps it the hold it used to be |
| **The sealed SET** | Still a hold: two builds of one module under one identity, or a bundle built against a build the identity does not carry (`SealedSetInconsistent`, MeshWeaver#3175). A torn publication is refused whole | — |

The strict-MVID rule stays where it belongs, on content bundles, in
`PrebuiltAssemblySeeder.DeclineReason`. Applying it to modules would forbid every ex-post Store
install across platform versions — and the roll gate does not apply it to modules either: it asks
whether the *bytes* link, which is the question MVID equality only approximates.

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

`PackageAvailabilityKind` keeps the answers apart on purpose:

- `ModuleUnloadable` / `SealedSetInconsistent` — we looked, and this package cannot survive the
  release. Actionable: publish a build of the module for this platform (or uninstall it); re-seal
  the torn set.
- `ContentBakeMissing` — we looked, and the instance will compile this package at boot. A cost the
  verdict names (`PackageAvailability.IsAdvisory`), not a hold — except under
  `Modules:RequirePrebuilt`, where it is one.
- `ModuleFloorExceedsTarget` — retired (MeshWeaver#3648). Nothing produces it; the declared floor
  is an advisory line beside the verdict.
- `Indeterminate` — we could **not** look at the STORE: the catalogue was unreachable, or the
  release has no resolvable identity. An availability failure is never dressed up as a
  compatibility verdict; an operator must be able to tell an outage from an incompatible release.
  🚨 The one indeterminate that does NOT hold is the *link check's* own — a release with no
  `platform-surface.json` is a publication that predates MeshWeaver#3651, and holding every roll on
  it would freeze the fleet exactly as the floors did. It is reported on the advisories instead.
- `UpdatabilityVerdict.NotEnforced` — the gate does not **apply**: this deployment consumes no CI
  bakes at all, so it already compiles at every boot and holding it could only freeze it forever.
  Deliberately not the same as passing — it carries a reason the caller logs and surfaces, so
  "nothing is gating this environment" is visible rather than inferred from a green tick.

## Both halves ask "would this roll take away something that works today"

A gate that holds an environment forever is a worse outage than the one it prevents. So neither
half asks "is this package perfect for the target"; both ask whether the roll would take away
something that works today — and since MeshWeaver#3651 only the module half can answer "yes".

- **Content**: a package is treated as content-bearing exactly when it has **ever** been sealed
  under **any** framework identity the published root holds. A package with no compilable NodeTypes
  produces no bundle ever, so naming one would be a lie — but the evidence must not come from the
  artifact store the gate is about to judge. 🚨 It once did (it asked the identity running *now*),
  and that made the denominator erode exactly where the gate was needed: a package whose bake broke
  left the set of packages the gate asks about, so every later roll was silent about precisely the
  package that had regressed. See [The Release Gate's Denominator](../ReleaseGateDenominator). What
  the denominator produces is the **named cost** on every verdict; what it used to produce was the
  hold, which `Modules:RequirePrebuilt` alone still asks for.
- **Modules**: the landed generation is linked against the target's published surface. A module
  that links keeps working; a module the target publishes a build of will be adopted; a module that
  does not link would throw `TypeLoadException` on its first render there — *that* is the roll
  taking away something that works today, and it is the one thing this gate holds on. The declared
  floor is not consulted: SemVer puts `3.0.0-ci.8009` **below** `3.0.0-rc8`, so every `rc` floor
  held every `ci` candidate on 2026-09-07 while every one of them would have loaded (MeshWeaver#3648).

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
  "version": "3.0.0-ci.8100",
  "isUpdatable": false,
  "enforced": true,
  "indeterminate": false,
  "holdReason": "SocialMedia: its landed module MeshWeaver.Social cannot load on 3.0.0-ci.8100 (framework identity s5b8b…): … references MeshWeaver.Mesh.CodeOutputCurrency (MeshWeaver.Mesh.Contract), which the target does not carry …",
  "advisories": [ "would recompile at boot on 3.0.0-ci.8100 (no sealed content bake for framework identity s5b8b…): AgenticPrimer, Crm" ],
  "bootCompiles": [ "AgenticPrimer", "Crm" ],
  "packages": [
    { "package": "SocialMedia", "status": "ModuleUnloadable", "reason": "…", "advisory": false },
    { "package": "AgenticPrimer", "status": "ContentBakeMissing", "reason": "…", "advisory": true }
  ]
}
```

`advisory: true` marks a status that names a cost the roll accepts rather than a hold;
`advisories` and `bootCompiles` carry what the verdict said without deciding on it
(MeshWeaver#3651), and the roll-target endpoint carries the same two fields for the release it
selected.

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
  and "this package cannot survive the release" have different fixes. And, held or not, **what
  the gate reported about the tag without holding it** (`Admin/UpdatePolicy` → `AdvisoriesTag` /
  `Advisories`, MeshWeaver#3651): the packages the roll recompiles at boot, a landed module whose
  loadability there could not be measured, a declared floor the target does not rank above. A roll
  that compiles content at boot is a state an operator can see on the tab, not a fact that lives
  only in a pod log.

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
```

(There is no `dependent-repos` any more — a publisher does not name its readers. A repo that exits
here comes back on its own `schedule`; see below.)

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

### The edge that makes it work: the exited repo RETRIES on its schedule

A repo that exits because an upstream is not published yet must have something bring it back.

There used to be a dispatch for that: `node-repo-publish-bake` fired `meshweaver-upstream-published`
to each repo in `dependent-repos`, using a token with write access to every one of them. **Both are
removed.** The publisher no longer knows its dependents, and no cross-repo credential exists.

What brings the exited repo back is its own **`schedule`** trigger: it re-reads the released image
from the registry and rebuilds once the upstream has published. Pull, not push.

```yaml
on:
  repository_dispatch:
    types: [meshweaver-framework-released]
  schedule:
    - cron: "17,47 * * * *"      # 🚨 without this the repo NEVER retries
```

> 🚨 **The schedule is not optional decoration.** A repo without it exits waiting for an event that
> no longer exists and never rebuilds — silently, with everything green. That is the same
> terminal-exit failure the dispatch was invented to prevent; the cure is now the schedule, and it
> is the one thing to check when a source's bundles are the ones missing from an instance's
> `heldReason`.

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
tempting to let each repo name the repos it must wake, and such a list is exactly
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
never mistaken for the intended shape. The same applied to the removed `dependent-repos` input on
`node-repo-publish-bake` and to CD's `BAKE_SUBSCRIBER_REPOS`. **Both are now GONE** — they were
ADDRESSED notification where the design calls for a broadcast, and both needed a cross-repo write
credential. Neither may be reintroduced: wiring a dependent into a list is not progress toward this
design, it is one more copy of a graph memex already holds, whose missing entry fails silently.

#### Why this is also the cure for the recompile leak

`NodeTypeRecompileAlcLeakTest` measures live hubs climbing (~+5 per recompile) — accumulation that
looks like a leak to hunt. The recompiles it counts are the symptom: an instance Roslyn-compiles
content at boot precisely when no sealed bake is available for its framework, which is the state
this page's gate exists to prevent. Every path that ends in "somebody rebuilt what should have been
fetched" ends in another ALC. Fixing the distribution — released artifacts, fetched, never
rebuilt — removes the recompiles rather than making each one cheaper.

## 🚨 How a fleet goes stale while every check is green (2026-08-22)

memex.meshweaver.cloud sat on a day-old release and would not move. Nothing was red: CI green, CD
green, bakes green, the portal healthy. The instance was doing exactly what this page asks of it —
and could never stop.

Read `Admin/UpdatePolicy` on the instance first; the hold is recorded there, not in CI:

```
heldTag    : 3.0.0-rc7.ci.4928
heldReason : no sealed content bake for framework identity se3bf749… — the bundle 'Store'
             is not published for release 3.0.0-rc7.ci.4928, so this instance would
             recompile it at boot
```

**Three defects compounded, and each is invisible alone.**

### 1. The instance froze instead of falling back

The poller picked the NEWEST tag and stopped. Unbaked ⇒ hold. The next platform build produced
another unbaked tag, the next bake published yet another identity, and the two never met. The head
is always the release *least* likely to be baked, and it blocked every release behind it that was.
A sister instance had rolled cleanly to `ci.4908` — memex should have taken that and did not.

Fixed by selecting the newest **baked** release: walk candidates newest-first and take the first
the gate accepts. Never backwards, never into a boot storm, but a not-yet-baked head no longer
freezes the fleet.

### 2. Nothing ever baked for the released identity

A bake follows the release only on `repository_dispatch`/`schedule`; a `main` push bakes the PIN,
whose identity is already published. The dispatch needed a cross-repo write credential that was
never provisioned, so for its whole life it printed "NOT CONFIGURED" and did nothing.

Fixed by deleting the dispatch instead of provisioning it: a release is a FACT about the registry,
so each repo's SCHEDULED run reads it and rebuilds. Pull, not push — no credential, no
hand-maintained dependents list.

> ⚠️ **A repo without the `schedule` trigger cannot participate at all.** It bakes only against its
> pin, on its own pushes, forever. Check this first when one source's bundles are the ones missing.

### 3. The bake and the roll aimed at different images

The bake resolves `mw-plugin-test:latest`; the instance rolls to `memex-portal-ai:<tag>`. Same
commit ⇒ same identity, but `:latest` moves, so a busy trunk makes them agree only by luck.

### What to check, in order

1. `Admin/UpdatePolicy` on the instance — `heldTag`/`heldReason` name the identity and the bundles.
2. Which SOURCE owns the missing bundles (`plugins`, `education`, …) — then whether that repo has a
   `schedule` trigger and when it last baked.
3. The identity its last bake published (`bake published: identity=…` in the job log) against the
   identity in `heldReason`. **Different values are the whole bug**; equal values mean look elsewhere.

### 🚨 DO NOT hand-roll while self-update is armed — it overrides you within minutes

Measured 2026-08-22, after several confusing "failed rolls": every `kubectl set image` was reverted
by the instance itself. The deployment was set to `ci.4908`; minutes later it carried `ci.4964`,
because `SelfUpdateHostedService` patches its own Deployment toward the newest tag. Each attempt
therefore rolled to a DIFFERENT image than the one chosen, and the failures analysed afterwards
belonged to an image nobody had picked.

The tell is cheap and worth taking first:

```
kubectl -n <ns> get deploy memex-portal-deployment \
  -o jsonpath='{.spec.template.spec.containers[0].image}'
```

If that is not what you set, the updater is driving. **Pause it before touching the image** — set
`Admin/UpdatePolicy` → `policy: None` on the instance — and restore the policy afterwards.

🚨 **A roll that fails readiness is SAFE and must not be repeated.** The old ReplicaSet keeps
serving, so nothing is down; re-rolling the same image just repeats the refusal. Read the new pod's
health checks and fix what they name. Rolling and reverting in a loop teaches nothing and churns a
production database.

### Extraction has TWO halves: out of the image, and preInstalled

Removing a module from the image takes away the guarantee that every deployment has it. Marking the
package `preInstalled` is what puts that guarantee back — it is what makes the catalog seed it into
`{source}/_DefaultInstallLedger`, and that ledger's entries are exactly the packages carrying the
flag.

Ship only the first half and the readiness gate reports it — but **which status it reports is the
whole design**, because the two situations `Modules:Required` used to conflate have opposite
remedies:

| what happened | verdict | `required_modules` | effect |
|---|---|---|---|
| The module is loaded, or resolves from this deployment | `Present` | Healthy | — |
| `Modules:Assemblies` **claims** the pack and the image does not carry it | `Absent` | **Unhealthy** | Readiness fails → the rollout STALLS and the old pods keep serving |
| Required but the image never claimed it — i.e. store-delivered | `ExpectedLater` | **Degraded**, named | The rollout completes; the missing feature is reported, per module, with its exact sub-state |

🚨 **Degraded here is not leniency, and Unhealthy is not a bigger hammer.** The gate exists to stop a
build that silently dropped a feature, and stalling only helps when the PREVIOUS pods have what the
new ones lack — which is true for a pack the image was supposed to ship, and false for a
store-delivered module. Reporting the second as Unhealthy wedged both prod rollouts on 2026-08-23:
the module can only arrive from the registry, the registry is itself a portal downstream of the same
rollout, and the only remedy anyone found was blanking `Modules__Required__0..4` on the live
deployment. A gate whose one escape hatch is deleting its own declaration is the skip-trapdoor with
its polarity flipped — it fails on no evidence instead of passing on it.

The evidence that separates them was already on disk: **`Modules:Assemblies` is the image's own
claim about what it carries.** `RequiredModuleStatus.Classify` asks that question, and an
`ExpectedLater` module always names which of four states it is in, so an operator never has to
guess: not installed (install the package), landed and awaiting the restart, landing incomplete
(re-install — no restart will fix it), or held above the platform floor (the platform update that
satisfies it is itself the restart that loads it). Both buckets ship in the probe's `data` payload
(`missing`, `expected`, `detail`) whatever the status, so a fleet sweep can enumerate them without
parsing prose.

⚠️ **Provisioning a package is NOT installing its module.** Provisioning creates the node partition;
landing the DLL is a separate step that the catalog performs for pre-installed packages. Confusing
the two costs a deploy.

### An activated module that never host-loaded is a SILENT 404

`MapMeshModuleEndpoints` can only scan assemblies that are actually loaded. So a module the
activation record says is ON, whose bytes never reached the process, contributes no routes — and its
whole HTTP surface answers 404 for the pod's entire lifetime with no exception and nothing to grep.
That is how `/mcp` went dark on memex.systemorph while the portal was otherwise healthy and two
clean rolling restarts changed nothing.

Startup now reports it, and `pending_module_activation` distinguishes the two cases rather than
promising a restart that cannot help:

- **landed, not loaded** → "a restart activates them" — true, and the rollout performs it;
- **activated, bytes ABSENT** → "re-install the package" — a restart will NOT load them, and saying
  otherwise is a prompt no restart can ever clear.

### Breaking the deadlock by hand

The selection fix cannot deploy itself — an instance cannot roll to receive the change that lets it
roll. Once, by hand, roll it to a release a SISTER INSTANCE is already serving: that release is
proven baked for the packages the two share, so the cost is bounded to whatever only this instance
has installed.

🚨 Portal and migration images must move TOGETHER; a portal tag without a matching migration tag is
an ImagePullBackOff. And the namespaces are CROSSED — `memex` serves memex.systemorph.com,
`memex-cloud` serves memex.meshweaver.cloud. Verify with the running image before patching, never
from the name.

## 🚨 A declared floor must be SATISFIABLE, and that is a build-time question

`ModulePlatformFloor.DeclineReason` decides at RUNTIME whether the platform a deployment happens to
run satisfies a module's declared `content.minMeshVersion`. It answers "not yet" — and it cannot
distinguish "not yet" from "never", because from inside one process the two look identical. So a
floor that no published image can ever meet produces a hold that is indistinguishable from a hold
waiting on the next release, forever, with nothing red anywhere.

**Measured 2026-09-07** (#3554). Both AKS portals ran `3.0.0-ci.7989` and held every self-update
candidate:

```
HOLDING 3.0.0-ci.7989 — AI: the module requires platform 3.0.0-rc8 or newer
                         but this deployment runs 3.0.0-ci.7989
```

Two versions that read as though they are in the right order. SemVer §11.4 compares pre-release
identifiers as **text**, so `"ci" < "rc"` and `3.0.0-ci.<n>` is below `3.0.0-rc8` for **every** `n`
— including `3.0.0-ci.999999999`. 42 packages carried rc-line floors, one carried a clean `3.0.0`
(which outranks every pre-release of `3.0.0`), and one named `3.0.0-rc14`, a platform that never
existed. The registry could not have rescued any of them: `memex-portal-ai` held 1268 tags — 798
`staging-*`, 48 `3.0.0-ci.*`, **zero** `rc*`, **zero** `3.1.0-*`.

### The check, and where it belongs

The `pack` job of `node-repo-module-pack.yml` already reads the floor out of
`{package}/index.json`. It now also asserts it against the platform the bundle is being **compiled
against**, read as that image's own `MESHWEAVER_PLATFORM_VERSION` — the string a running portal
identifies itself by, i.e. the exact value `ModulePlatformFloor` compares at landing time.

That predicate is deliberately stronger than "some tag in the registry satisfies this", and needs no
registry listing. A bundle records the framework identity it was built against and a consumer only
adopts a bundle whose identity matches its own platform, so a floor **above** the build platform is
unsatisfiable by construction: no deployment can both satisfy the floor and accept the bundle. And
the lane already holds the image, so the reading is one `docker image inspect`, not a network trip.

Two properties make it a gate rather than a comment:

- **It cannot skip.** The step is unconditional. A lane that pins no platform image is not exempted
  — the checkout IS the platform there (the same reading the bundle-identity block takes, `g<sha>`),
  so the floor is measured against the LINE that checkout declares, which is a weaker reading than
  an image's own version but never a skip. Only a platform that can be identified NEITHER way is
  RED, naming what to pin: "the floor could not be checked" must never be reported as "checked and
  fine", which is the same skip-trapdoor shape the repository's CI rules ban for a gate that asks
  whether its own input exists.
- **It cannot drift from the runtime.** The ordering is written once in `NuGetVersionComparer`,
  mirrored in `.github/scripts/check-module-platform-floor.py`, and pinned by
  `ModulePlatformFloorScriptParityTest`: for every case the script's exit code must equal "the
  runtime found no reason to decline". Two call sites folding the same rule differently either never
  converge or never fire, and both are silent.

### What it does NOT cover

The **structural** half — refusing a floor that names the retired `rc` line or the unreleased clean
`3.0.0` at all — is repo-local and static, in `MeshWeaver.Plugins`' own
`scripts/check-module-floors.py` (Plugins#1447). That one has to stay static on purpose: the Plugins
validate lane runs on forks and on Dependabot, where reading a registry would need a credential, and
gating a check on whether a credential is present is exactly the trapdoor above. The two halves are
complementary: the static one catches a bad floor on the pull request that writes it; this one
catches any floor the build platform cannot satisfy, whatever its shape.

A satellite adopts this check when it moves its `node-repo-module-pack.yml@<sha>` pin — the lane is
pinned by SHA, so nothing changes for a repo until it bumps.

## See also

- [The Release Gate's Denominator](../ReleaseGateDenominator) — why "which packages must be baked" may never be read from the artifact under judgement
- [Roll Selection](../RollSelection) — the same predicate applied when CHOOSING the target rather than approving one, and the measured boundary of what a published set can be asked
- [CI Content Bake](../CiContentBake) — where the sealed bundles and the framework identity come from
- [The Continuous Delivery Contract](../ContinuousDeliveryContract) — the publication this gate reads
- [Release & Self-Update Strategy](../ReleaseStrategy) — the poll, the policy node, the roll
- The Module Adoption Policy page (`Doc/Architecture/ModuleAdoptionPolicy`, MeshWeaver#3652) — the rule: run the newest thing that loads, keep what you have until then, never let a string decide
- [The Module Platform Link Gate](../ModulePlatformLinkGate) — the measurement, and the surface document that lets the roll gate make it
- [Modules](../Modules) — the `MinMeshVersion` floor (advisory since MeshWeaver#3648) and why modules are not MVID-gated
