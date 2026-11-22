---
Name: Canary Per Major Line
Category: Architecture
Description: Every live major line has ONE canary deployment that follows `latest` for the platform image and every module; every other deployment of that line follows `stable`, which advances only after the canary ran a build for a soak window with a green verdict. The promotion is computed once on the control instance, recorded as a node, needs no human on the normal path, and holds (and files into triage) when the canary is red. Production takes a promoted build as one wave.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3c-3 3-4 6-4 9a4 4 0 0 0 8 0c0-3-1-6-4-9z"/><path d="M5 21h14"/><path d="M12 16v5"/></svg>
---

# Canary Per Major Line

> Maintainer, 2026-11-22: *"Let's factor out a dedicated instance per major version where we test
> changes, not in prod. Prod should be stable."* And: servers must not restart for module updates;
> only boot-time infrastructure may require one.

**Status: design of record, implemented in slices.** Each slice below says whether it is landed or
owed. The vocabulary is [Release Channels](/Doc/Architecture/ReleaseChannels): `latest` is derived,
`stable` is promoted. This page is the *who* and the *when* of that promotion for deployments.

## Why — what production did on 2026-11-22

Every portal followed the newest build of its line. Platform: every working record declared
`updatePolicy: Continuous` on its line (`4.*`, `3.*`). Modules: the registry's install-defaults pass
and each install's `Auto` policy, plus `autoRecycleOnStaleBuild`. Nothing distinguished a portal
people work in from one that exists to try things.

Measured over about 19 hours:

- The company portal restarted 4 times, and the public portal 4 times. Most were **same-image
  restarts after module waves** (the AI module's 4.0.10 and 4.0.11 waves, about 20 modules each),
  which reached the control lane as `Restart` actions.
- Two public-portal restarts came through the package-uninstall restart coordinator.
- Each restart showed every connected user the "server was updated" reload.

So production was the first place a build ran under real load. That is the property this page removes.

## The rule

> **Every live major line N has exactly one CANARY deployment. The canary follows `latest` for the
> platform image and for every module. Every other deployment of line N follows `stable` for both.
> `stable` for line N advances only after the canary of line N ran it for the soak window with a
> green verdict.**

Four properties follow from the rule and are pinned by tests:

1. **An unpromoted build never reaches a follower.** A follower's roll is capped at the line's
   promoted build. If nothing is promoted, it holds.
2. **A red canary holds promotion.** The cause is filed into bug triage, and `stable` stays where it
   was. A broken promoter degrades to "stable stops advancing", never to "nothing resolves".
3. **Lines are independent.** The v3 canary promotes only v3, and the v4 canary only v4. A
   declaration for one line never reads or writes another.
4. **Promotion is monotonic.** `stable` never moves backwards. A follower is never downgraded by it.
5. **The canary soaks in BATCHES.** The canary HOLDS the build it runs until that build's verdict,
   `Promoted` or failed, and only then takes the newest sealed build. Without the hold, a canary that
   follows `latest` rolls on every merge, each roll restarts the soak, and with a build every 3 hours
   against a 240-minute soak nothing is ever promoted: production starves (measured on line 4,
   2026-11-22: three builds in six hours, no promotion). A same-image restart (a module wave) is held
   by the same rule, since it would restart the soak too. A candidate that FAILS (the canary is red,
   was red inside the soak, or is over its issue budget) releases the hold at once, because the newest
   build is what carries the fix; the pointer records it as `failedTag`, and the release applies only
   while the canary still runs exactly that build. An unread pointer or an unorderable pair holds. A
   line that is disabled holds nothing, since nothing would ever decide the build. For an urgent fix,
   declare `holdOverride: true` on the line declaration: the canary then follows `latest` as before,
   until the declaration drops it.

## The pieces

```
 core CD ── arm ──► {N}-latest ──► canary of N (Continuous N.*, modules Auto)
                                        │  runs build T, soaks
                                        ▼
                     control: FleetWatch (its own pass) WRITES Ops/Status/<canary>, Ops/Issue/*
                                        ┆  data only: the promoter READS these samples
                                        ┆  on its own schedule; it is not a step of FleetWatch
                     LinePromotion (own self-clocking pass, every 5 min, per declared line)
                        verdict: Promoted T | Held (reasons) | CouldNotEvaluate (reasons)
                        writes Ops/Promotion/v{N}            (the pointer; one serialised writer, never backwards)
                               Ops/Promotion/v{N}/<T>        (the record, keyed by set, never mutated)
                                        │
                                        ▼
       follower announces T' ≥ T ──► SelfUpdateRouting caps the Roll at T, admits it unattended
       follower restart-pending  ──► held; its modules land live or ride the next promoted Roll
```

### 1. The line declaration — DECLARED, not derived

A line, its canary and its followers are **committed content**: one `Hosting/ReleaseLine` node per line
beside the deployment records (`Deployments/Lines/v4`, …). This follows policy
`declared-not-derived`: an absent contributor is never a removal, and a line nobody declared has no
canary and promotes nothing.

| Field | Meaning |
|---|---|
| `line` | The major, e.g. `"4"`. Must equal the canary's `updatePattern` line (`4.*`). |
| `canary` | The ONE deployment id that follows `latest` for this line. |
| `followers` | The deployment ids that follow `stable`. Never the canary, never a record of another line. |
| `soakMinutes` | How long the canary must run a build before it promotes. Default 240 (4 h). |
| `issueBudget` | How many non-holding issues of the canary may be open in the soak window. Default 0. |
| `enabled` | `false` keeps the declaration but promotes nothing, and followers then hold. |
| `holdOverride` | `true` lets the canary take every newer build at once instead of holding its current candidate until its verdict. The declared way to rush an urgent fix. Default `false`. |

The deployment records themselves keep `updatePolicy: Continuous` and their line pattern, so the
Memex no-pins gate is untouched. A follower is not pinned. The declaration decides WHEN it takes a
build, never WHICH build exists (the same contract as `rollGate`). The resolved tag is recorded on the
control side (`Ops/Promotion`) and never written back onto a record.

### 2. The canary

The canary is a working portal, not a test fixture. It exercises what production exercises:

- **More than one replica.** Orleans membership, grain placement and rolling restarts only show up
  with several silos.
- **Every package installed.** For example `preInstall: ["Plugins/*"]`.
- **Modules `Auto` and `autoRecycleOnStaleBuild: true`**, so a module wave lands there first.
- **Real use**, so the error-signature budget has something to measure.

Choice for the current major: **the staff development instance.** It is already Continuous on `4.*`,
runs three replicas, installs every package, and carries no fleet role and no client data. Reusing
it costs nothing. Its user traffic is light: one user was indexed when this was measured on
2026-11-22. So its verdict rests on what every boot exercises, as FleetWatch samples it: the roll,
the NodeType bake, module adoption and the health checks. It does not rest on load. A synthetic smoke
over the canary's own `Tests` areas is OWED, so that the "real use" bullet above is met by more than a
handful of people. A new idle instance would cost a namespace, a database and secrets and would
measure no more.

Choice for the previous major: **a dedicated small canary instance is OWED.** Only one customer
portal remains on the previous major, and no non-production instance runs that line. Until the canary
is provisioned, the previous line is **not declared at all**, so that portal keeps following
`latest`. Declaring it a follower of a line that has no canary would freeze it with no way to receive
a fix. Provisioning goes through a governed Provision `Hosting/InstanceAction`. The smallest envelope
still keeps three replicas, because the records repository's two-legs rule holds every serving
instance to a floor of three.

### 3. Promotion — computed once, on the control instance

`LinePromotion` runs on the control instance's resident platform-build inbox hub. It is a
self-clocking pass at FleetWatch's own cadence (5 min), the cadence that writes the `Ops/Status`
samples it reads. Each pass evaluates every declared line. One activation per address and a schedule
that starts the next pass only after the last one finished make it a single, serialised writer.

**The platform evidence.** Candidate T is promoted when every item below holds. The readings are
the same ones `RollGates` already trusts, and each unanswered reading is a reason to WAIT, named:

1. The canary's `Ops/Status/<canary>` sample is current (not stale, scraped), **converged**, on
   **one generation**, and its running tag T is on line N.
2. The generation has run for at least `soakMinutes`: the oldest pod start on T. A same-image restart
   makes the pods younger, so a module wave on the canary **restarts the soak**. That is the safe
   direction, and it is how a module wave is soaked as well.
3. No `StuckRoll` and no `Critical` `Ops/Issue` of the canary is open, or was resolved inside the soak
   window.
4. At most `issueBudget` other open issues of the canary. These come from the FleetWatch kinds:
   `RestartSpike`, `ReplicaShortfall`, the declared health-check alarms and the roll-log signatures.
5. T is strictly newer than the current `stable` of the line (`BuildOrder`), and T is orderable
   against it. Two tags of unknown order never promote.

**The verdict is a verdict, not a tally** ([Release Channels](/Doc/Architecture/ReleaseChannels)):

| Verdict | Means | Effect |
|---|---|---|
| `Promoted` | T qualified, the pointer moved | `Ops/Promotion/v{N}` names T; `Ops/Promotion/v{N}/<T>` records the evidence |
| `Held` | Evaluated, and T did not qualify (soaking, red, not newer) | Pointer unchanged; reasons recorded on the pointer node |
| `CouldNotEvaluate` | A reading did not answer (no sample, query failed, page full) | Pointer unchanged; RED, reasons recorded |

**A red canary files into triage.** A `Held` verdict caused by a holding issue does **not** open a second
issue for the same fact, because the canary's own `Ops/Issue` is already the triage item and
FleetWatch notifies it. A `CouldNotEvaluate` that persists beyond three passes means the promoter is
blind. It is raised as a `Hosting/Issue` of kind `PromotionBlind` (Critical), because "I did not
check" must be as loud as a fault.

**Monotonic by the writer.** The pointer write is a compare-and-swap on the node's version and
refuses any tag that is not strictly newer than the one it read. The per-set record is keyed by the
tag, so a retry rewrites the same entry. Entries are never mutated after creation.

### 4. Followers take `stable` — as one wave

On the control instance, `SelfUpdateRouting` routes every self-update announcement. For a record that
a line declaration lists as a follower:

- **A Roll announcement** for build T' is **retargeted** to `min(T', stable)`, exactly as an open
  `rollGate` retargets to the newest build its followed deployments run. If `stable` is not newer than
  what the follower runs, the announcement is **held**, with the reason in the log, and the next
  announcement re-evaluates. Nothing is promoted, so nothing rolls. That is the negative control.
- **The roll is admitted unattended.** The promotion record is the evidence an approval would have
  asked a person for, and the maintainer's directive removes the human step from the normal path.
  An explicit `rollGate` on the record still always waits for an approval. The two compose: a gated
  follower takes `min(T', stable, gate candidate)` and still asks.
- **A restart-pending announcement** (a module wave that wants a same-image restart) is **held**:
  - When `stable` is newer than what the follower runs, it becomes the promoted Roll, so the
    restart and the roll are one wave.
  - Otherwise the pending modules land live (the live-swap path) or ride the next promoted Roll.

  This is "servers must not restart for module updates" applied on the control side. A boot-time
  infrastructure change arrives with the platform image and therefore with a promoted Roll.

**At most one restart per promoted set** follows: a follower restarts when, and only when, `stable`
advances past what it runs. `MinRollInterval` stays as the floor.

### 5. Modules follow `stable` — OWED, and where it goes

The control-side hold in §4 stops module waves from **restarting** a follower. It does not yet stop a
follower from **installing** a module generation that the canary has not soaked. That gap matters
only for modules that swap live, and it closes on the instance side:

- **The promoted module set.** A promotion also records the canary's installed module generations,
  `{package: {version, generation}}`. This needs the canary to report its installed set. Today
  `Ops/Status` carries only `pluginCount` per pod, so a module half needs a FleetWatch reading of each
  pod's installed generations first.
- **The channel the instance reads.** A deployment record gains a module channel, rendered as
  `PluginCatalog__Channel` (`latest` | `stable`, absent = `latest`). Under `stable`,
  `RegistryUpdateReconciler.AdoptOne` caps `PluginBundleClient.PlanModuleAdoption` at the promoted
  generation of each package. The registry serves the promoted set per line beside its index, and a
  missing set FAILS CLOSED (holds), never "adopt the head".
- **One wave.** A change to the promoted set is one `ModuleSetProposed`. The landing service already
  restarts at most once per proposed set (`BootModuleSet.IsSupersededBy`), so a promoted set lands as
  one landing, not one per package.

### 6. The control instance

The control instance is not a follower in the first slice. Core policies `platform-deploy-control-first` and
`control-always-latest` make it the first deployment of every accepted platform build. CD's
`control-acceptance` proves it before anything else sees the build. Making it follow the canary would
invert those two policies, so that is a **maintainer decision, recorded as owed**. Its module-wave
restarts are still in scope for §5, under the same instance-side channel.

## What this is NOT

- **Not a pin.** No record names a tag. A follower whose line has no promotion holds; it is not frozen
  to a value. Removing the declaration restores `latest` behaviour exactly.
- **Not a replacement for the combo gate or the floor.** A follower still runs its per-instance checks
  before adopting. The promotion only decides what it is offered.
- **Not `UpdatePolicyKind.Stable`.** That policy means *deliberately cut releases only*, which is the
  enterprise edition. The follower channel is a promotion over the CI line, and it is expressed by
  the line declaration, not by the record's `updatePolicy`. Overloading `Stable` would freeze every
  follower, because no clean 4.x release exists.

## Slices and state

| # | Slice | Where | State |
|---|---|---|---|
| 0 | This page | core `Doc/Architecture/CanaryPerMajorLine` | landed with this page |
| 1 | `Hosting/ReleaseLine` declaration type + `LinePromotion` promoter + follower routing + tests | MeshWeaver.Plugins `Hosting/` | Systemorph/MeshWeaver.Plugins#5782 |
| 2 | `Deployments/Lines/v4` (canary: the staff development instance; followers: the working portals) (the previous line is declared with its canary) | the deployment-records repository, `mesh/Deployments/Lines/` | after slice 1 is live on the control instance |
| 3 | The previous major's canary: record + governed Provision | records repository + control instance | owed: cost and sign-in decision |
| 4 | Instance-side module channel (§5) | core `MeshWeaver.PluginCatalog` + registry | owed |
| 5 | The control instance follows the canary (§6) | core CD policies | owed: maintainer decision |

## See also

- [Release Channels](/Doc/Architecture/ReleaseChannels): the `latest`/`stable` vocabulary and the
  promotion invariants this page instantiates for deployments.
- [One Promotion Gate](/Doc/Architecture/OnePromotionGate): what arms a set, so it may reach a canary
  at all.
- [Module Adoption Policy](/Doc/Architecture/ModuleAdoptionPolicy): what an installation runs, and the
  adoption decision §5 extends.
- [Live Module Update](/Doc/Architecture/LiveModuleUpdate): the live-swap path that makes holding a
  module-wave restart safe.
- [Self-Update Control Lane](/Doc/Architecture/SelfUpdateControlLane): the announcement route §4
  extends.
