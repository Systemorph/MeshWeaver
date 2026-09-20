---
Name: Why the Fleet Stopped Rolling Itself
Category: Architecture
Description: >-
  The September 2026 self-update investigation, measured per instance. Four portals were not taking
  new platform builds for four different reasons — two deliberate pins awaiting an approval, two
  separate defects — and none of them the reason the policy nodes appear to state. Plus the separate,
  four-hour break in the producing half, and the availability read whose cost grew with the artifact
  store until it timed out on every candidate — both since resolved.
  Re-measured 2026-09-20: the deliberate pin was cleared and the instance still did not roll — the
  apply half had stopped after a failed roll on the 14th while the detect half kept announcing daily.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/><path d="M4.5 4.5l15 15"/></svg>
---

# Why the Fleet Stopped Rolling Itself

Measured 2026-09-18, all times UTC. The fleet appeared to have stopped taking new platform builds on
2026-09-11, with the public instance's policy node quoting roughly twenty-five modules that "cannot
load" on `3.0.0-ci.8339`. **Almost none of that was true, and the parts that were true have four
different causes.** This page is what the instruments actually say and how to read them.

## The three claims that were wrong

**"CD has not sealed a set since 2026-09-11."** It sealed at least one every day from 2026-09-09
through 2026-09-18, the last at **05:45Z on 2026-09-18** (run 8897). What broke is **four hours old**
and lives in the producing half — see *The producing half* below.

**"The roll is correctly held on ~25 unloadable modules."** That sentence was written **once**, at
2026-09-11T08:57:47Z, and has been frozen ever since. The instance it is on has since moved *past*
the target it names. Nothing has re-measured it and nothing will — see *A frozen hold is history*.

**"The fleet is frozen, for one reason."** Four instances, four causes. **Two are deliberate** — memex
and pearl each carry a reviewed `pinnedImageTag`, and a candidate newer than the pin waits for an
approval rather than patching unattended. **Two are stuck, and for different reasons**:
memex-cloud's policy record has lost its own policy — a live defect — and build cannot list tags on
its registry at all, which is **not** an outstanding code defect but an undeclared pairing
(MeshWeaver#4093; the platform half merged on 2026-09-12, see below). Neither is the
module-compatibility hold the policy nodes appear to show.

## What was measured

Running versions are `GET /api/version` on each host; policy readings are each instance's own
`Admin/UpdatePolicy`; pins and fleet intent are the `Deployments/<id>` records on the control
instance **memex.systemorph.com**.

| instance | host | running | why it is not self-rolling |
|---|---|---|---|
| **memex-cloud** | memex.meshweaver.cloud | `3.0.0-ci.8411` (`c84c6c05`), pods from 2026-09-17 | 🚨 **defect** — its `Admin/UpdatePolicy` carries **no `policy` field at all**, which reads as `None`; the poller returns before listing the registry |
| **memex** (control) | memex.systemorph.com | `fc8cd583`; record pins `3.0.0-ci.8710` | **by design** — it detects, hands the build to the control lane, and *"a newer tag waits for an approval in the mesh. This install does not patch itself."* |
| **pearl** | pearl.meshweaver.cloud | `3.0.0-ci.8080` (`67cbbe0e`) | **by design** — `pinnedImageTag: 3.0.0-ci.8080`; and the instance-side policy node is a separate act from the record's `updatePolicy` |
| **build** | build.meshweaver.cloud | `3.0.0-ci.8411` (`c84c6c05`) | 🚨 **stuck, but not on platform code** — its images come from `cr.meshweaver.cloud`, where the self-updater refuses to list tags because nothing on its record declares which portal validates its key (MeshWeaver#4093). The platform half shipped 2026-09-12; what is owed is a declaration and a roll — see below |
| **partnerre** | — | — | `Ops/Status/partnerre` is `Unknown`, last written 2026-09-15 — outside this measurement |

Note also that **the images are moving**: memex-cloud and build both run `3.0.0-ci.8411`, and
memex-cloud's pods started on 2026-09-17. The public instance took a newer image through the reviewed
`pinnedImageTag` + helm route in the private `Systemorph/Memex` repo. "Nothing has rolled" is true of
the **pull** mechanism and false of the **push** one, and conflating the two hides that memex and pearl
are pinned deliberately.

🚨 **A pin is not evidence of intent.** memex's and pearl's pins are reviewed decisions; build's is a
*workaround* for MeshWeaver#4093, recorded as such in its own deployment record — *"the self-updater
cannot check for updates on an instance whose images come from cr.meshweaver.cloud, so rolls go through
the control instance until that lands."* The same field means "we chose this version" on one instance
and "detection is broken here" on another, and only the record's own words tell them apart. That one
matters more than it looks: build is the instance that compiles modules, and a build instance held
behind the newest sealed platform produces bundles keyed to an identity the fleet has moved past.

### build: the platform half landed on 2026-09-12; the declaration did not

🚨 **"Cannot list tags on `cr.meshweaver.cloud`" has read as an outstanding platform defect since
2026-09-12, and it is not one.** The rule that lets an installation present its `mwi_` key to a
container registry validated by *another* portal — the fleet's shape, where `cr.meshweaver.cloud`
forwards the key to `memex.meshweaver.cloud` — merged that day as `830c8c402`
([The Self-Update Registry Credential](../SelfUpdateRegistryCredential)), with the fleet's shape
pinned on both sides by `OciTagListerTest`: declared, it resolves and lists; undeclared, it refuses
and the key does not leave.

What is left is **two statements on a record and a roll**, in this order, and neither is a platform
change:

1. `SelfUpdate__RegistryValidationUrl: "https://memex.meshweaver.cloud/api/instances/token"` in
   `extraPortalConfig` on `Deployments/build` and `Deployments/pearl` — the registry record's own
   `validationUrl`, restated on the consumer. Measured 2026-09-18 on the control instance:
   `Deployments/build` (v21) carries neither key.
2. A roll onto an image whose core sha has `830c8c402` as an ancestor. Both instances run
   `3.0.0-ci.8411` (`c84c6c05`), which **predates** it — so on those pods the declaration is an env
   var nothing reads. Declaring first is harmless and does not help until the roll.

**The pairing is a DECLARATION on purpose.** `RegistrySpec.ValidationUrl` lives on the record of the
instance that *hosts* the registry, so a consumer cannot look it up, and both shortcuts the issue
originally proposed were declined: resolving by whatever endpoint the registry *names* lets the party
receiving the credential choose which of the installation's keys to redirect, and "exactly one mount
carries a key, so present it" is, for the one-mount shape most of the fleet is, simply dropping the
check. The refusal names the host the operator must declare — read it, do not re-derive the rule.

## A frozen hold is history, not a hold

This is the instrument that misled three sessions, and it is worth stating as a rule.

Three writers touch `Admin/UpdatePolicy`, and they touch **disjoint** field sets
(`SelfUpdateHostedService`):

| writer | fields | runs |
|---|---|---|
| `RecordCheck` | `lastCheckedAt`, `lastCheckVerdict`, `lastCheckTrigger`, `unresolvedInstalledTag` | **every** check, every outcome |
| `RecordAvailable` | `latestAvailableTag`, `checkedAt` | only when candidates were listed |
| `RecordHold` | `heldTag`, `heldReason`, `heldIndeterminate`, `heldAt`, `advisoriesTag`, `advisories` | only when a candidate was evaluated |

Under `UpdatePolicyKind.None` the poller returns on its first statement, so only `RecordCheck` runs.
`latestAvailableTag`, `checkedAt`, `heldTag`, `heldReason` and `heldAt` are left **exactly as the last
real evaluation left them**, indefinitely. That is deliberate: `UpdatePolicyNodeType` says the record
is not cleared because the last real evaluation is the only diagnostic an operator has when they turn
updates back on, and `IsHoldOperative(tag)` returns `false` whenever the policy is `None` so the UI
does not present a fossil as a live refusal.

- **The staleness test is `heldAt` against `lastCheckedAt`.** On memex-cloud today: 2026-09-11T08:57:47Z
  against 2026-09-18T09:55:24Z. Seven days.
- **The discriminator is the verdict string**, a compile-time constant reachable from exactly one place:
  `updates are disabled on this install (Admin/UpdatePolicy = None); the registry was not listed.`
  When that sentence is present, no tag list, no candidate selection, no availability gate, no combo
  gate and no patch happened on that tick.
- So `latestAvailableTag: 3.0.0-ci.8057` dated 2026-09-07 is **not a failing registry read** — it is the
  absence of one. *"Listing is broken"* and *"listing was never attempted"* look identical on the node
  and are told apart by this sentence alone.

## memex-cloud: the record lost its own policy

`UpdatePolicyContent.Policy` is a computed property over a nullable backing field:

```csharp
public UpdatePolicyKind Policy
{
    get => DeclaredPolicy ?? UpdatePolicyKind.None;
    init => DeclaredPolicy = value;
}
```

**An absent declaration reads as `None`, never as enabled** (#3542) — and because the hub serializer
drops default-valued members, `Continuous` (the zero member) only ever reaches the wire as an explicit
`"policy"` property. memex-cloud's node carries neither `policy` nor `pattern`. The control instance's
carries both.

`SelfUpdateOptions.DefaultPolicy` (`Stable`) does not rescue it: it is seed-only, consumed by
`EnsureExists`, which returns early for a node that already exists. And **no chart value, no deployment
record and no rendered environment variable can set the policy** — the portal ConfigMap enumerates its
`SelfUpdate__*` keys explicitly and renders five of them, none of which is `DefaultPolicy`. On an AKS
install there are exactly three routes to `None`: an explicit write from the settings tab or an MCP
patch; an unparseable content (`ParseContent` fails closed to an empty record); and **the record losing
the field under the poller's own bookkeeping writes**.

The third is named in the code's own documentation as something that has already happened *to this
instance*:

> an install whose record lost its policy under its own bookkeeping writes no longer rolls itself.
> That is how memex-cloud reached a withdrawn `3.1.0-ci` line "on a policy record that lost its own
> policy".

The mechanism is visible in every bookkeeping writer:

```csharp
Content = (cur ?? new UpdatePolicyContent()) with { … }
```

`cur` is the node's content materialized as `UpdatePolicyContent`. When it does not materialize —
untyped JSON from the polymorphic converter, a same-named type from another collectible assembly, a
field a running build cannot deserialize — `cur` is null and the write **replaces the whole record with
a default one**. The policy and the pattern are gone; `None` then suppresses the `BuildCompletion` and
`ModuleSetProposed` triggers entirely, so the instance drops to one hourly `SafetyNet` check that can
only re-state that updates are disabled. **It is self-latching**: the condition that erases the policy
also removes every event that could notice.

### The timeline on memex-cloud

| when | what the node said |
|---|---|
| 2026-09-07T22:26:44Z | last `RecordAvailable` — `latestAvailableTag: 3.0.0-ci.8057`; **frozen here ever since** |
| 2026-09-07T22:27:17Z | a hold recorded for `3.0.0-ci.8057` (module floors) |
| 2026-09-08T20:10Z (v60000) | already `updates are disabled …`; trigger `BuildCompletion` |
| 2026-09-10T00:25Z (v70000) | unchanged |
| **2026-09-11T08:57:47Z** | a hold recorded for `3.0.0-ci.8339` — so the policy was **operative at that moment** |
| 2026-09-12T02:15Z (v77000) | `updates are disabled …` again, trigger now `SafetyNet`, the 8339 hold still displayed |
| 2026-09-18T09:55:24Z | unchanged; hourly `SafetyNet` only |

The 09-11 row is the important one: the policy was restored, produced exactly one real evaluation, and
was lost again within hours. **That is a recurring fault, not residue from a past incident** — so
restoring the policy by hand is a repair with a known half-life, not a fix.

### Would those ~25 modules still hold on a current set?

Probably not, and the control instance is the evidence. It evaluated a **current** target
(`3.0.0-ci.8886`) at 01:02Z and produced **no module hold at all** — only advisories: one assembly
version skew that rolls forward (`System.Reactive` 6.1 against the platform's 7.0), three declared
floors that decide nothing since #3648, and one package that would recompile at boot. The two
instances share the five modules their records require (`Blazor.Radzen`, `Blazor.Analysis`,
`Blazor.EntityViews`, `Blazor.GoogleMaps`, `Speech`).

So the working hypothesis is that memex-cloud's module story **dissolves into its stale-target story**:
the ~25 entries were measured against a set from 09-11 and were never re-measured. It is a hypothesis
and not a measurement — memex-cloud carries packages memex does not (RolePlay, SocialMedia, Edu), and
the only instrument that can settle it is that instance evaluating a current target, which needs its
policy back. **Restore the policy first, then read the hold it produces.** Reading today's frozen text
as the answer is what this page exists to prevent.

## memex: detecting, handing over, and waiting for an approval

The control instance is the one portal whose policy is intact, and it shows what the fleet would do if
every policy were restored. At 2026-09-18T01:02:19Z its verdict was:

> **update available: 3.0.0-ci.8886** — handed to the control lane (`Hosting/PlatformBuilds`: stored at
> `Hosting/PlatformBuilds/_Inbox/88ca1f01…`, signature verified); the control plane opens a Roll for
> this deployment (a Roll to the record's pinned tag restores unattended, **a newer tag waits for an
> approval in the mesh**). **This install does not patch itself.**

So the pull mechanism is working end to end on this instance: it lists, selects, gates, hands over and
records. What does not happen is an unattended patch — because `Deployments/memex` carries
`pinnedImageTag: 3.0.0-ci.8710` and the candidate is newer. That is the intended shape for an instance
with a reviewed pin, and it is the same routing pearl's record documents for a customer portal: one
approval per release rather than an unattended roll.

**Reading this as a freeze is the second version of the first mistake.** "Waiting for a human" and
"blocked by a gate" produce the same standstill and want opposite responses.

### The 60-second timeout is a separate, newer condition

By 09:59Z the same node read:

```
HOLDING 3.0.0-ci.8913 — the artifact catalogue for release 3.0.0-ci.8913 could not be read
(the availability check did not answer within 60s) — cannot determine availability, which is not
clearance to proceed
```

`ReleaseAvailabilityService.IsUpdatable` and `SelectRollTarget` each carry `.Timeout(AnswerBudget)`
with `AnswerBudget = TimeSpan.FromSeconds(60)`, `private static readonly`, no configuration key. On
timeout the observable resolves to `ReleaseArtifacts.Unreadable(…)`, which
`ReleaseAvailability.IndeterminateReason` turns into that sentence — a **hold**, by design, because "I
could not look" may never be reported as clearance.

The budget bounds two cold legs on the file-system `IIoPool`:

1. `PublishedBundleCatalogue.EverSealedBundles(publishedRoot)` — the gate's **denominator**, which
   enumerates **every framework-identity directory and every source directory** under
   `PreWarm:PrebuiltBundleRoot` (`/data/prebuilt-bundles`, on the `azurefile-memex` share).
2. The target's own publication — marker file, identity directory, each source's bundles, their
   dependency records, the surface manifest, every sealed module bundle opened as a zip — plus a mesh
   query for the install records and the landed-generation sidecar.

`SelectRollTarget` walks candidates newest-first and reads one observation per candidate until one
clears. **Since 06:29Z today the newest sixteen-odd tags carry images but no sealed publication** (next
section), so the walk has that many more candidates to read and reject before it can reach one that
clears — inside the same fixed budget.

**The prediction, stated so it can fail.** If that is the cause, the timeout should disappear once the
seal recovers and the newest tag clears on the first read. If the timeout survives a freshly sealed
set, the cause is the denominator's size and not the candidate walk, and the remedy moves from "the
seal" to "the store". Either way the next reading decides it, and the two do not look alike.

### The prediction RAN, and it went the way that moves the remedy to the store

The seal recovered at 10:15Z and `3.0.0-latest` moved at ~16:35Z. The timeout survived it. Measured on
**memex.meshweaver.cloud**'s own `Admin/UpdatePolicy`, on two freshly sealed candidates, after the
instance's policy was restored to `Continuous` / `3.0.0-ci*` at 18:17Z:

| check | trigger | verdict |
|---|---|---|
| 18:45:36Z | `BuildCompletion` | `HOLDING 3.0.0-ci.8931 — the artifact catalogue for release 3.0.0-ci.8931 could not be read (the availability check did not answer within 60s)` |
| 19:16:36Z | `BuildCompletion` | `HOLDING 3.0.0-ci.8932 — … could not be read (the availability check did not answer within 60s)` |

Two consecutive checks, two candidates sealed minutes earlier, the same timeout. By the prediction's
own test that is the **denominator**, not the candidate walk — and the cost was not theoretical: the
public instance served `3.0.0-ci.8411` (`c84c6c05`, 2026-09-12), **1,198 commits behind `main`**, while
CD sealed a fresh set every half hour and its `Promote: tag the full set` job stayed green. Eight Store
NodeTypes sat at `compilationStatus: Error` there as a result, `Store/Catalog` and `Store/Order` among
them, on the instance where subscriptions happen. Filed as
[MeshWeaver#4742](https://github.com/Systemorph/MeshWeaver/issues/4742).

**Fixed at the read, not at the bound.** `SealedBundleFloorCache` — a mesh-scoped singleton the
availability gate and the roll selector share — remembers what each **source** directory
(`<root>/<identity>/<source>`) declared, keyed by its path and its own write stamp. A tick still
*lists* the root and each identity, so a new identity is always seen, a new source is always seen and
a pruned one always drops out; what it no longer does is **open** the publication pointer and the
completion sentinel of a source whose directory has not changed. The listings are one directory
response each; the opens were the cost.

🚨 **Why the SOURCE directory's stamp, and not the identity's.** The identity directory's stamp moves
when a source is added or removed under it and **not** when a source is republished in place — and a
republication in place is routine, not exotic: a satellite re-bakes into a platform identity that has
not moved every day its own build runs. Keying on the identity would freeze a newly-added package
*out* of the denominator for as long as that identity stayed newest, which **exempts** it from the gate
(#3461) — the one direction that must never happen. The source directory's stamp moves on every
publication path there is: in the generation layout a republish creates `<source>/<token>/` and
rewrites `_current`, both entries of the source directory; in the flat layout it removes and rewrites
`_complete` in the source directory itself.

And the argument closes in the direction that matters: the stamp detects any change to the source
directory's **entry set**, and a republication that ADDS a package necessarily adds an entry — a bundle
file, or a whole generation directory. The only change it cannot see is a rewrite of `_complete` in
place with no entry added, which can only re-word or SHRINK a declaration, i.e. can only make the gate
hold.

🚨 **Fail-closed otherwise, clause by clause**, because a cache that turned a hold into a roll would be
far worse than the freeze it removed. A refusal — an absent root, an unfollowable publication pointer,
an enumeration fault — is **never** remembered, so one transient share fault cannot latch into a
permanent verdict. A source carrying **no seal** is never remembered either: a flat-layout republish
unseals, rewrites and re-seals, and if all of that landed inside one stamp granule a mid-way reading
would otherwise be remembered as "declares nothing". Nothing about the CANDIDATE is cached — the
target's own publication, its marker, its surface and its module set are read fresh per candidate per
tick, and only the denominator's *history*, the part #3441 made monotone on purpose, is remembered.
Eviction happens **only after a successful, complete enumeration** and only for the root it covered, so
a share that could not be read never evicts a reading it merely failed to reach — and memory therefore
tracks the live store rather than every publication the process has ever seen.

`SelfUpdate__AvailabilityAnswerBudget` is now a configuration key with the same 60 s fail-closed
default, rendered by the portal ConfigMap. 🚨 **It is the secondary half and never the fix** — a knob
for an instance whose share is genuinely slow, not a remedy for a read that grows. Widening a bound
over an append-only store buys a longer freeze with the same ending, which is what the section below
already said and is why it is worth repeating here.

🚨 **Do not read this timeout as the thing standing between the control instance and a roll.** It is
not, and the same node said so eight hours earlier: at 01:02Z it *did* answer, *did* select
`3.0.0-ci.8886`, *did* hand it over — and still did not patch itself, because the candidate is newer
than the record's pin and waits for an approval. Fixing the timeout restores the *diagnosis*; it does
not produce a roll. Two standstills with the same appearance and different remedies is the recurring
shape of this whole incident.

### Why the denominator has no upper bound

`EverSealedBundles` is deliberately monotone: a package that has once sealed a bundle under *any*
identity stays in the denominator for ever, so a bake that silently regresses becomes a hold rather
than an exemption. Its own documentation names the cost:

> it reads EVERY identity on a network share against a 60 s verdict budget, and a denominator
> expensive enough to time out would answer `Indeterminate` and freeze every environment.

The store gains one identity directory per distinct framework identity, and the identity moves whenever
the platform's API surface moves (`FrameworkBuildIdentity` hashes the canonical 26-assembly subset of
`meshweaver-surface.manifest`, plus the full MVIDs of the toolchain closure).

**Measured cadence: no two sampled sets shared an identity.** Six tags, read off the policy nodes' own
sentences and the replicas' bake reports:

| set | framework identity |
|---|---|
| `3.0.0-ci.8057` | `sf456af88e1d9c07c0d9d75de97b0800c` |
| `3.0.0-ci.8339` | `sbb8b372062fdc895e3f80ec99ee2df3c` |
| `3.0.0-ci.8411` | `sd608997…` (memex-cloud's bake report) |
| `3.0.0-ci.8710` (`fc8cd583`) | `saea1aea34067e4f08d37dac68091d518` |
| `3.0.0-ci.8767` | `s5ec352bb102e5a2275e3831a08ac0c8d` |
| `3.0.0-ci.8886` | `s72c46bad7d8e4d1647482cbf670d0e30` |

Six samples, six values. Treat that as "the identity moves about as often as a set is cut", not as a
rate — but the direction is not in doubt, and CD cuts roughly sixty sets a day. **The denominator
therefore grows with the number of sealed sets**, which makes a fixed 60 s budget a *when*, not an
*if*.

(Identity churn does **not** by itself make modules unloadable — `Modules:VersionStrictness` defaults
to `Family`, so a bundle sealed for any same-line identity is adopted when its platform type
references measurably resolve. That is why the control instance's 01:02Z evaluation carried advisories
and no module hold. Churn is a cost paid by this directory, not by the loader.)

Nothing removes the old ones:

```yaml
# deploy/helm/templates/memex-portal/config.yaml
PreWarm__PrebuiltBundleRetention__Delete: "{{ … | default "false" }}"
```

The chart's own note says **the code default is the opposite** (`PrebuiltBundleRetention.Delete` is
`true`) and that it renders `false` on purpose, because the sweep removes bytes something is executing
and must not run on unexamined defaults. Measured 2026-09-18: **no deployment record in the fleet
overrides that key** — not memex, not memex-cloud, not pearl, not build. On every portal the retention
pass has been scanning, planning and reporting, and deleting nothing, since the lane began.

An append-only store, a monotone walk over all of it, and a fixed 60 s budget do not fail on a
particular build. They fail once the store is big enough, and then on every build after that.
**Raising the budget is not the remedy** — it exists to convert a stall into an honest answer, and a
bigger one buys a longer freeze with the same ending.

🚨 **Count the OPERATIONS, not the local milliseconds.** On a developer SSD with a warm page cache a
directory listing and a file open cost about the same — both are VFS hits — so a local stopwatch
understates this fix by construction. What decides it on Azure Files is **round trips**, and those
track file-system operations exactly, because the cifs client's metadata cache (`actimeo`, one second
by default) has long expired between two checks minutes apart.

Measured with three sources per identity, which is the fleet's shape (`meshweaver-content`, `plugins`,
a satellite):

| store | directory listings, per tick | publication opens, per tick — before | after, steady state |
|---|---|---|---|
| 50 identities | 51 | 300 | **0** |
| 200 identities | 201 | 1,200 | **0** |
| 800 identities | 801 | 4,800 | **0** |

A "publication open" is one pointer probe plus one sentinel read — two file operations per source, and
three or more SMB round trips each. At 800 identities that is **4,800 file operations removed from
every tick**, and a few milliseconds of round trip apiece is how the old read reached sixty seconds.
The listings remain, and they are the honest residual: the read is no longer flat, it is one directory
response per identity. Removing that last linear term would need the publisher to advance a stamp on
the identity directory itself (`publish-bake-bundles.sh`), which is the follow-up if the store grows
another order of magnitude — not something to fold into a fix for a live freeze.

### The timeout hold reports itself on the wrong side

`ReleaseAvailabilityService` states the rule ten lines above the place it breaks it: build the verdict
with `UpdatabilityVerdict.Unavailable`, *not* `IsUpdatable(target, [], Unreadable(…))`, because the
latter answers `IsUpdatable = false` with `IsIndeterminate = false` — `IsIndeterminate` reads the
per-package verdicts, and an empty package list has none. The timeout `Catch` passes `[]`. So a timeout
hold records `heldIndeterminate: false`, and every surface that splits *"the catalogue could not be
read"* from *"a package cannot survive this release"* files this one under the second heading. The
textbook "I could not look" is presented as a compatibility verdict.

## The producing half: the seal, and a cross-repo pair

Separate from everything above, **opened at 06:29Z and closed at 10:15Z on 2026-09-18** — under four
hours, and worth recording because the shape recurs and no gate can see it.

`main-cd` kept building, promoting and verifying images throughout — `3.0.0-ci.8906` through `8913`
exist. What stopped was **`Plugins: bake + seal the publication`**, `skipped` because the module suite
ahead of it failed. Without that job an image set is published but **no sealed set is registered**, and
a sealed set is what an install may adopt.

One test, one assertion. Core `9da6e85cf7` (PR #4682, issue #4668), merged **05:43:08Z**, made deleting
an already-absent node a *success*:

```
Nothing to delete at <path>: it was already absent.
```

`MeshWeaver.AI.Test.McpNegativeOperationsTest.Delete_NonExistentNode_ReturnsError` in the Plugins repo
still expects the string `Error`. Ancestry pins the boundary exactly: the head of the last green CD
(run 8897, 05:45Z) does not contain the merge; the head of the first red one (run 8904, 06:29Z) does.

This is the **shape 7** cross-repo break named in `AGENTS.md` — a behaviour change behind an unchanged
signature, which no surface gate can see by construction. The `Cross-repo pair` gate looks at removed
public types and members; nothing here was removed, so nothing was red until the suite ran.

**Resolved.** The counterpart, `Systemorph/MeshWeaver.Plugins#2059`, **merged at 10:15:38Z**; core CD
run **8916** (created 10:17:53Z) is the first to resolve `content-ref` at a Plugins main that carries
it. Verify the recovery the way this page says to verify everything — **on the seal JOB, not the run's
conclusion**:

```bash
gh api "repos/Systemorph/MeshWeaver/actions/runs/<id>/jobs?per_page=100" \
  --jq '.jobs[] | select(.name | test("bake \\+ seal")) | "\(.name): \(.conclusion)"'
```

Run 8896 concluded `failure` and sealed; runs 8909 and 8914 concluded `success` with every build job
skipped. Neither conclusion tells you whether a set exists.

> **Not related:** `Combo verification (candidate × instance)` has been red since the workflow's first
> run on 2026-09-07 because its three inputs — `vars.COMBO_VERIFY_SOURCES`, `secrets.COMBO_VERIFY_KEYS`,
> `secrets.COMBO_VERIFY_TOKENS` — have never been provisioned. It gates nothing; its effect is that
> every roll in the fleet is recorded `UNVERIFIED` (#3544). Its green runs are "no candidate" exits
> with all four real jobs skipped, so it has never verified an instance.

## What each thing needs

| thing | what it needs | kind |
|---|---|---|
| the seal | ✅ done — `MeshWeaver.Plugins#2059` merged 10:15:38Z; confirm on run 8916's seal JOB | **an action**, taken |
| memex-cloud | `policy: Continuous` + `pattern: 3.0.0-ci*` restored on its own `Admin/UpdatePolicy` | **a decision**, and a repair with a known half-life |
| every instance | a bookkeeping write must never replace a record it could not materialize — refuse and log instead | **a code fix** |
| memex, pearl | a newer tag than `pinnedImageTag` waits for an approval | **working as designed** — approve, or clear the pin deliberately. 🚨 **Superseded for memex on 2026-09-19**: the pin was cleared and it still did not roll — see [the 2026-09-20 re-measurement](#2026-09-20-the-apply-half-stopped-and-the-detect-half-kept-announcing-into-it) |
| every instance | `PreWarm__PrebuiltBundleRetention__Delete` | **an operations decision**, from a ledger line, after confirming the protected set covers every instance and every CI gate pinning an older platform build |
| the availability gate | answer inside its budget over a store that only grows | ✅ **done** — `SealedBundleFloorCache` (#4742) remembers each SOURCE publication's declaration, so a tick lists but no longer re-opens them; `SelfUpdate__AvailabilityAnswerBudget` is the secondary knob, never the fix |
| a timeout hold | record `heldIndeterminate: true` | **a code fix**, one call site |
| build, pearl | MeshWeaver#4093 — `SelfUpdate__RegistryValidationUrl` on the record, then a roll onto an image carrying `830c8c402` or later | **a config change and a roll** — NOT a code fix; the platform half merged 2026-09-12 |

## 2026-09-20: the apply half stopped, and the detect half kept announcing into it

**Six days after this page was written, memex.systemorph.com had still not rolled — and the reason had
changed.** This section is the re-measurement, because the remedy table above ("memex … a newer tag
than `pinnedImageTag` waits for an approval") is no longer what the instruments say.

| instrument | reading, 2026-09-20 | what it means |
|---|---|---|
| `GET /api/version` | `3.0.0+96f88406` | what is actually running |
| `Admin/UpdatePolicy` → `policy` / `pattern` | `Continuous` / `3.0.0-ci*` | behaviour is intact |
| → `latestAvailableTag` | `3.0.0-ci.9014` | listing works |
| → `handedOverTag` / `handedOverAt` | `3.0.0-ci.9014` / `06:12:52Z` **today** | **the hand-over webhook fires, daily** |
| → `comboVerifications` | `[]` | no verdict for the candidate ⇒ any roll is taken UNVERIFIED |
| `Deployments/memex` → `pinnedImageTag` | **absent** (record modified 2026-09-19T19:41Z) | 🚨 the `3.0.0-ci.8710` pin named above is GONE |
| `Ops/Actions/*` newest `Hosting/InstanceAction` | **2026-09-14** | **no Roll opened for six days**, across ≥3 candidates (8886, 8996, 9014) |

**So the deliberate-pin explanation has expired.** The pin was cleared on 2026-09-19 and the instance
still did not roll, which rules out "waiting for an approval because the candidate is newer than the
pin" as the current cause. Anyone reading the remedy table without re-reading `Deployments/memex`
will fix a pin that is not there.

**What the 14th actually left behind.** `reconcile-memex-20260914-nav-rail` rolled onto
`3.0.0-ci.8612`; `sample-memex-20260914-nav-rail` records that the replica **never became Ready**; and
`sample-memex-20260914-rollback` records the roll back to `3.0.0-ci.8411`, 30 minutes at 1/2 updated.
Nothing has been opened since. **The apply half stopped after a failed roll and the detect half has
gone on announcing into it every day** — so the daily "update available … handed to the control lane"
line is evidence that detection works, and no evidence at all that anything consumes it.

**Two causes this page does NOT distinguish**, because the reader who can see the inbox should:

1. the control plane opens a `Roll` that waits for an approval nobody gives — the designed shape; or
2. the delivery is never turned into an action at all — the MeshWeaver#777 shape, where signed build
   facts sat unconsumed because the watcher was armed on on-demand hubs.

Today's verdict names `Hosting/PlatformBuilds/_Inbox/dc1fab8d191e466c9b09069140328240` as *stored,
signature verified*. That node read **Not found** and the inbox listed **empty** to a global admin over
MCP — which is consistent with both "already consumed" and "not visible to me", so it settles nothing.
**Check it as System on the control instance before concluding**, and read the inbox area's watcher
liveness rather than the node list.

### The distinguishing question, and the order to ask it in

1. **Is an action open?** `search path:Ops/Actions nodeType:Hosting/InstanceAction` and sort by date.
   An action newer than the last `handedOverAt` ⇒ shape 1, and the remedy is an approval.
   **No action newer than the hand-over ⇒ shape 2, and an approval will never come.**
2. **Is the inbox draining?** The `Inbox` area of `Hosting/PlatformBuilds` shows watcher liveness and
   every pending event with its age. A non-empty ageing inbox is the failure; an empty inbox with no
   action is the watcher consuming and dropping.
3. **Only then** the combo gate: `comboVerifications` empty means UNVERIFIED, which grants no
   clearance and takes no refusal ([Combo Gate Wiring](/Doc/Architecture/ComboGateWiring)) — it
   explains a roll being *taken* unverified, never a roll that never happens.

🚨 **The general trap, one level up from the one this page opens with.** A working detector in front of
a dead consumer produces a *daily fresh timestamp* on `handedOverAt` — the field most likely to be read
as "the pipeline is alive". Liveness of the announcing half is not liveness of the acting half, and
here they are in different processes on different instances. **Pair every `handedOverAt` with the age
of the newest action on the target**; a hand-over with no younger action is the whole diagnosis.

### What the moving-label design does and does not fix

The obvious reading of this — "pin it to a label, move the label, roll on the move" — is
[Release Channels](/Doc/Architecture/ReleaseChannels) (core #4769): a channel is a named moving pointer
whose selection always resolves to an **immutable id**, so nothing downstream runs a moving name. On
this instance the selecting half of that already exists (`pattern: 3.0.0-ci*` resolving to
`latestAvailableTag`), and it is not where the stall is. **A channel that moves is only as good as the
consumer that acts on the move**, so a decision table for "the pointer moved and no action was opened"
has to be loud rather than silent — otherwise the channel work lands on top of this exact failure and
inherits it.

### Downstream cost, so the next reader knows what it blocks

A portal that does not roll does not advance its plugin sources either: its GitSync holds every package
at the commit sealed for the framework identity it RUNS. On 2026-09-20 `Crm/_GitSync` read *"sealed at
2c4cfa10 for this instance (identity s091d69fee9df4e5dabee742024122f71) … the registry has since sealed
3.0.0-ci.9014 under se67137088031dee4af0f30c7d5edcf4c, which this instance does not run — so this source
advances when this instance IMAGE does (a roll), NOT when another publication lands"* — 15 commits
behind, holding a CRM data migration and an unrelated invoice feature. **"Merged, green and verified on
`origin/main`" says nothing about a portal having it**; check the seal before promising a date.

## How to read these instruments

- **`lastCheckVerdict` first, always.** It is the only field written on every tick. If it says
  *"updates are disabled on this install"*, every other field on the node is from an earlier regime.
- **`heldAt` vs `lastCheckedAt`** is the staleness test for `heldReason`. Days apart means history.
- **`checkedAt` / `latestAvailableTag`** move only when candidates were listed. A stale pair under a
  `None` policy is the absence of a listing, not a failed one.
- **"handed to the control lane … waits for an approval" is not a hold.** It is the pull mechanism
  succeeding and stopping where a person was meant to decide.
- 🚨 **`handedOverAt` is the ANNOUNCING half's liveness, never the acting half's.** A working detector
  in front of a dead consumer refreshes it daily, which reads exactly like a healthy pipeline. Pair it
  with the age of the newest `Hosting/InstanceAction` on the target: **a hand-over with no younger
  action is the diagnosis** (2026-09-20 — six days of daily hand-overs, no action since the 14th).
- **Re-read `Deployments/<id>` before acting on any pin advice on this page.** The `pinnedImageTag`
  it documented for memex was gone by 2026-09-19 and the standstill outlived it.
- **The record's `updatePolicy` is intent; the instance's `Admin/UpdatePolicy` is behaviour.** Setting
  the first alone changes nothing an instance runs.
- **Read the seal JOB, never the CD run's conclusion** — run 8896 concluded `failure` and sealed; runs
  8909 and 8914 concluded `success` with every build job skipped.
- **`GET /api/version` beats every record** for what is running; `GET /health` is the per-replica
  census, and repeated calls sample *different* replicas.
- **`Deployments/<id>` on memex.systemorph.com is authoritative.** The copy on memex.meshweaver.cloud is
  a second sync of the same folder and legitimately disagrees.

## See also

- [The Continuous Delivery Contract](../ContinuousDeliveryContract) — what a published set guarantees
- [Module Versioning](../ModuleVersioning) — framework identity, what moves it, and the closure boundary
- [Operating from the Portal](../OperatingFromThePortal) — the Hosting API reads that replace cluster access
- [Stale State Until Recycle](../StaleStateUntilRecycle) — why a rolled image is still not a served answer
