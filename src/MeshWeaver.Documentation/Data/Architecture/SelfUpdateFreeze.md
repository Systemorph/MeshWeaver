---
Name: Why the Fleet Stopped Rolling Itself
Category: Architecture
Description: >-
  The September 2026 self-update investigation, measured per instance. Four portals were not taking
  new platform builds for four different reasons — two deliberate pins awaiting an approval, two
  separate defects — and none of them the reason the policy nodes appear to state. Plus the separate,
  four-hour break in the producing half, since resolved.
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
approval rather than patching unattended. **Two are defects, and they are different defects**:
memex-cloud's policy record has lost its own policy, and build cannot list tags on its registry at
all (MeshWeaver#4093). Neither is the module-compatibility hold the policy nodes appear to show.

## What was measured

Running versions are `GET /api/version` on each host; policy readings are each instance's own
`Admin/UpdatePolicy`; pins and fleet intent are the `Deployments/<id>` records on the control
instance **memex.systemorph.com**.

| instance | host | running | why it is not self-rolling |
|---|---|---|---|
| **memex-cloud** | memex.meshweaver.cloud | `3.0.0-ci.8411` (`c84c6c05`), pods from 2026-09-17 | 🚨 **defect** — its `Admin/UpdatePolicy` carries **no `policy` field at all**, which reads as `None`; the poller returns before listing the registry |
| **memex** (control) | memex.systemorph.com | `fc8cd583`; record pins `3.0.0-ci.8710` | **by design** — it detects, hands the build to the control lane, and *"a newer tag waits for an approval in the mesh. This install does not patch itself."* |
| **pearl** | pearl.meshweaver.cloud | `3.0.0-ci.8080` (`67cbbe0e`) | **by design** — `pinnedImageTag: 3.0.0-ci.8080`; and the instance-side policy node is a separate act from the record's `updatePolicy` |
| **build** | build.meshweaver.cloud | `3.0.0-ci.8411` (`c84c6c05`) | 🚨 **defect** — its images come from `cr.meshweaver.cloud`, where the self-updater cannot list tags **at all** (MeshWeaver#4093); the pin is the workaround, not the reason |
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
| memex, pearl | a newer tag than `pinnedImageTag` waits for an approval | **working as designed** — approve, or clear the pin deliberately |
| every instance | `PreWarm__PrebuiltBundleRetention__Delete` | **an operations decision**, from a ledger line, after confirming the protected set covers every instance and every CI gate pinning an older platform build |
| the availability gate | answer inside its budget over a store that only grows | **a code fix** — the denominator is monotone over an append-only store and does not need re-walking every tick |
| a timeout hold | record `heldIndeterminate: true` | **a code fix**, one call site |
| build | MeshWeaver#4093 — list tags on `cr.meshweaver.cloud` | **a code fix**, already tracked |

## How to read these instruments

- **`lastCheckVerdict` first, always.** It is the only field written on every tick. If it says
  *"updates are disabled on this install"*, every other field on the node is from an earlier regime.
- **`heldAt` vs `lastCheckedAt`** is the staleness test for `heldReason`. Days apart means history.
- **`checkedAt` / `latestAvailableTag`** move only when candidates were listed. A stale pair under a
  `None` policy is the absence of a listing, not a failed one.
- **"handed to the control lane … waits for an approval" is not a hold.** It is the pull mechanism
  succeeding and stopping where a person was meant to decide.
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
