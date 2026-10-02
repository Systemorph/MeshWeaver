---
Name: One Promotion Gate
Category: Architecture
Description: Never block a core merge, always build the newest green core with the newest Plugins, and let exactly one gate decide what the fleet rolls to — the arming of a promoted set whose MeshWeaver.Plugins dependent suites passed against that exact pair. What changed on 2026-09-27, why, and where each piece lives.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12h10"/><path d="M10 6l6 6-6 6"/><rect x="17" y="4" width="3" height="16" rx="1"/></svg>
---

# One Promotion Gate

**Three policies, one design** (register: [Policy Not Prose](../PolicyNotProse)):

| policy | the rule |
|---|---|
| `core-merge-never-blocked` | A core pull request merges on core's OWN required checks. No required context, gate or queue step waits on MeshWeaver.Plugins or any other repository. |
| `build-latest-green` | Every CI consumer — Plugins pull requests, Plugins `main`, the image build — takes the NEWEST GREEN core `main` build. A red core `main` falls back to the last green one, never forward into red. No pin to move for ordinary changes; the freeze variable `MW_PLATFORM_REF` stays for incidents. |
| `one-promotion-gate` | The fleet rolls only to an ARMED set, and a set is armed only when MeshWeaver.Plugins' dependent suites passed against exactly the pair it was built from. That is the one place a cross-repository verdict decides anything. |

## Why — measured on 2026-09-27

- **A green core PR waited four hours and was then ejected.** MeshWeaver#5807 was green at 13:21Z,
  sat in the merge queue, and its merge group failed at 17:21Z: *"MeshWeaver.Plugins did not answer
  within 42 min (no verdict yet). Silence is not a pass."* The queue waited synchronously on the
  other repository's runner capacity. (The merge queue on `main` was switched off the same day; the
  ruleset `main pr protection` carries no `merge_queue` rule.)
- **A two-repository change had to cross five hops in series:** core merge → core CD seal → Plugins
  pull request re-resolves the platform (after Plugins `main` had PASSED on it — the main-passed
  ceiling) → full Plugins CI → merge → the portal-image rebuild, queued behind any in-flight CD run.
- **The whole number is in** Systemorph/Memex `docs/build-bottlenecks-2026-09-27.md` (run ids,
  durations).

## The flow now

```
core PR ──(core's own checks)──► core main ──► Build and Test ──► main-cd
                                                                   │
Plugins push to main ──(image-relevant?)──► main-cd rebuild ───────┤  coalesced: one run in flight,
                                                                   │  the newest pending supersedes
                                                                   ▼
            gate: core main HEAD + Plugins main HEAD (at gate time) = the PAIR <core7>-p<plugins7>
                                                                   │
                         build ─► promote (phases A+B: identity + pair tag, CI pointers)
                                  + promotion record (artifact)    │
                                                                   ├─► verify, platform bake,
                                                                   │   Plugins seal, satellites …
                                                                   │   (CI reads the set NOW)
                                                                   ▼
   Plugins promotion-candidate.yml (poll) ──► core-candidate.yml: dependent suites against the pair
                                                                   │ verdict at
                                                                   │ refs/core-candidate/pair-<core7>-p<plugins7>
                                                                   ▼
                  main-cd `arm` (every run) ──► phase C memex-portal-ai:<version>
                                                phase D <line>-latest pointers
                                                notify-platform-update (the release event)
                                                                   │
                                                                   ▼
                                     the fleet: Continuous on 3.0.0-ci* — rolls to ARMED tags only
```

### Never block a core merge

`Consolidate test results` no longer needs `preflight`, `dependent-suites-dispatch` or
`dependent-suites`, and no step of it reads them. The dependent-suites run on a pull request is
**advisory**: requested by the `dependent-suites` label or by a declared Plugins counterpart
(`Pairs-with: Systemorph/MeshWeaver.Plugins#<n>`, see [Paired Change Sets](../PairedChangeSets)),
reported on the pull request, and blocking nothing. Measure the required set through the ruleset
endpoint — `gh api repos/Systemorph/MeshWeaver/rules/branches/main` — never the classic protection
endpoint, which answers 404 for this repository.

### Always build latest

- **The image** — `main-cd`'s `gate` resolves core `main` HEAD and MeshWeaver.Plugins `main` HEAD at
  gate time, and stamps both into the pair tag `<core7>-p<plugins7>` on `memex-portal-ai`. A push to
  Plugins `main` that can change the image dispatches a rebuild (Plugins
  `portal-image-rebuild.yml`, `scripts/portal-image-relevance.py`); main-cd's concurrency keeps one
  run in flight and ONE pending, so a burst of merges collapses to the newest — never a queue of
  stale builds, and no run already in flight is cancelled.
- **CI** — the platform resolver (`.github/scripts/resolve-platform.py`) takes the newest set whose
  platform trio is sealed (promote, verify, platform bake: every one of them runs ~5–30 minutes after
  a green core main commit and none waits for the fleet's gate), resolving a set that is promoted
  but not yet ARMED by the portal's `<core7>-p<plugins7>` tag from that run's promotion-record
  artifact. The run head can differ from the commit whose image the gate built, and the portal's
  bare core tag can move when Plugins changes. The resolver checks the recorded pair and refuses
  a missing pair instead of selecting the moving bare tag. A red core `main` publishes nothing, so
  the last green set is taken. Plugins pull requests no longer hold back to the set Plugins `main` last
  passed on by default; the label `platform:main-passed` asks for that ceiling.

### Page-one freshness

GitHub can return a cached page 1 of core main-CD runs that is stale but still younger than the
12-hour age guard. That made a consumer quietly choose an older sealed set even though newer sets
had completed (#73). Whenever a resolution has no proven ceiling shortfall — with no ceiling, or
with a caller-supplied main-passed ceiling that page 1 satisfies — the resolver now compares page 1 with a second query for main-CD runs created within that same 12-hour window.
A higher run number is a positive witness that page 1 omitted a run; the resolver re-reads page 1
up to the bounded retry limit and refuses to resolve from it if it remains stale. If the independent
query cannot be read, freshness is unverified and the resolver fails closed. A successful query
with no newer run proves nothing, so the existing age and ceiling rules still decide the result.

### One promotion gate

`promote` (phases A and B) makes a set usable by CI. It no longer ARMS it. The arming writes —
phase C, `memex-portal-ai:<version>` (what `SelfUpdateHostedService` lists, pattern `3.0.0-ci*`);
phase D, the `<major>-latest` / `<major.minor>-latest` / `3.0.0-latest` pointers; and the signed
release event (`notify-platform-update`) — moved to the `arm` job, which runs in EVERY main-cd run
and arms the newest promoted set that is

1. newer than every set already armed (never backwards — re-checked at the pointer write), and
2. carries a `success` verdict from MeshWeaver.Plugins for exactly its pair — key, core commit, base
   (the core commit of the newest ARMED set — below) and Plugins commit all checked
   (`.github/scripts/arm-promoted-set.py`, `--self-test`).

A missing verdict WAITS, a red one is REFUSED, a newer green one supersedes both. The verdict is
produced asynchronously by MeshWeaver.Plugins' `promotion-candidate.yml`, which polls for the newest
promoted-but-unarmed pair without a verdict and runs `core-candidate.yml` against it; when that run
finishes it dispatches main-cd so the arming does not wait for the hourly reconcile. Core sends
nothing to Plugins for this (`CoreDispatchesToNoRepository` stands); it only READS the verdict ref,
which is ledgered in `PlatformNeverDependsOnPluginsGuard.ApiReadLedger`.

### An arming is COMPLETE only when the whole sequence landed — and it is serialised

The cursor `arm` reads — the newest `<version>` tag on memex-portal-ai — moves at phase C, before phase
D (the line pointers) and before the release event. A failure after phase C would otherwise leave the
set counted as armed and never retried. So `notify-platform-update` writes a non-selectable marker,
`memex-portal-ai:arm-complete-<version>`, only after the build fact was delivered;
`arm-promoted-set.py armed-state` reports whether the newest armed set carries it; and while it does
not and nothing newer is green, `select --resume <version>` re-arms exactly that set (phase C is an
idempotent re-tag of the same digest; phase D and the event run again, and the control instance
consumes a repeated build fact idempotently). A newer green set still wins — arming it moves the
pointers and announces the line past the incomplete one. A set armed before the marker existed has no
promotion record to resume from and is reported, never failed.

Two runs can be in `arm` at once (the push lane and the reconcile lane are separate workflow
concurrency groups), so the job carries its own group, `main-cd-arm`, never cancelled: the read of
the cursor, the selection and the phase-C write happen one run at a time. The release event is a
separate job and re-checks at the send that no newer set was armed meanwhile, so an older set is never
announced after a newer one.

### The bundle's base is the newest ARMED set, never the first parent

A promoted set usually carries several core merges — the batch window coalesces them, and `pending`
takes only the newest unarmed pair and supersedes the older ones instead of queueing them — and ALL
of them reach the fleet the moment the set is armed. The record's `base` is what MeshWeaver.Plugins
diffs from to SELECT its suites and re-runs its control arm AT, and the verdict is pinned to it. A
first-parent base would therefore measure one merge of the bundle and arm every other merge
unmeasured — exactly how #5635/#5647/#5655 would reach the fleet if they landed in one set.

So `promote`'s record step takes `base` from the newest armed set: `arm-promoted-set.py armed-base`
reads memex-portal-ai's manifests (the armed `<version>` tag shares its manifest with the
`<core7>-p<plugins7>` pair tag). The bare core sha tag can move to a newer pair for the same core;
this happened on `3.0.0-ci.9564`, leaving its armed manifest with only the pair tag. The core commit
is resolved from that tag and checked to be an ANCESTOR of the candidate. `base..candidate` is then
exactly the merges the fleet has not seen. The record says which rule produced `base` in `base_kind`:

| `base_kind` | when | what happens |
|---|---|---|
| `armed` | the normal case | measured and armed as usual |
| `first-parent` | nothing was ever armed, or this commit IS the armed set | measured and armed; nothing wider exists |
| `unresolved` | the armed set could not be read, resolved, or is not an ancestor | promoted for CI as always, but `select` REFUSES to arm it and `pending` does not measure it (a warning names why); the next promoted set retries |

An `unresolved` base never holds CI and never arms merges nobody measured. A record written before
this rule carries no `base_kind` and is judged as before, so no set promoted during the transition is
stranded.

**What the gate does not hold:** promote, `verify-images`, the platform bake, the Plugins seal, the
satellites' compatibility legs and every CI resolver. `delivery-verdict` judges platform delivery
without `arm` or `notify-platform-update` (`PlatformDeliveryNeverWaitsOnPluginsGuard.
TheArmingIsNeverAPlatformDeliveryLeg`). What waits for Plugins is only the fleet's roll.

**The override.** `workflow_dispatch` of main-cd with `arm_override: <set>` and `arm_reason` arms
exactly that promoted set without a verdict — for an incident, recorded in the run summary. A set
that has no promotion record is refused, never substituted. A single instance is still forced with
a `Roll` `Hosting/InstanceAction`, not here.

### Instances roll only from armed tags — verified

- `SelfUpdateHostedService` rolls to the newest version-shaped tag of `memex-portal-ai`; the fleet's
  `Admin/UpdatePolicy` is Continuous on `3.0.0-ci*` (Systemorph/Memex `scripts/check-no-pins.py`).
  Those tags are written only by `arm`.
- A new instance seeds from `3.0.0-latest`, resolved to its concrete `3.0.0-ci.N` by Memex
  `scripts/resolve-line-pointer.sh`; the pointer moves only in `arm`'s phase D.
- `memex-portal-ai:main` and the identity/pair tags move at promote — they are CI pointers, not a
  roll target of any instance.
- **The control image follows the same gate.** `memex-control` is the same build as
  `memex-portal-ai` under another repository name (`-p:MemexControlImage=true`, which adds
  `MeshWeaver.Fleet.Control` and `MeshWeaver.SelfUpdate.Aks` and closes the type set — see
  [Closed Type Set](../ClosedTypeSet)). Its own lane (`control-image` → `control-acceptance` →
  `control-promote`) stays outside the fleet's delivery verdict, and `control-promote` writes only
  identity tags: `<core7>`, the pair `<core7>-p<plugins7>` and `main`. The version tag and the line
  pointers are written by `control-arm`, which runs after `arm` in every main-cd run
  (`arm-promoted-set.py control-follow`): it reads the newest ARMED version and its pair tag off
  memex-portal-ai and tags the accepted control image of that pair. An armed set without an accepted
  control image is a warning naming the version the control image stays on — never a red on the
  fleet's delivery, and healed by the next run once the pair is accepted. The version tag is written
  to the fleet registry first and to ACR last, so its presence on ACR means both registries carry it.
  Before this, `control-promote` wrote the version and the pointers at promotion, so an instance on
  `memex-control` with a Continuous policy would have rolled to sets the gate had not armed.

### Containment is answerable

Every armed set carries its pair — the tag `<core7>-p<plugins7>`, the promotion record's full
`core_sha` / `plugins_sha`, and the release event's `coreSha` / `pluginsSha`. "Does image
`3.0.0-ci.N` contain core commit C / Plugins commit P?" is `git merge-base --is-ancestor` of C
against the record's core commit (P against its Plugins commit) — Systemorph/Memex
`scripts/image-contains.py`, which also resolves the tag an instance runs.

## Content-neutral pushes do not restart a Plugins pull-request run

A push that only merges `main` into a pull request, or regenerates `manifest.lock` files, changes
nothing the pull request authored. MeshWeaver.Plugins' `pr-supersede.yml` cancels an in-flight run
only when the pull request's AUTHORED diff (everything but generated locks, against the respective
merge-bases) changed; otherwise the run finishes and the new head ADOPTS its verdict
(`scripts/ci-change-set.py`, `adopted-from`). This generalises the resolver bot's lock-only-merge
rule to any author.

## What is still owed

Kept in Systemorph/Memex `docs/build-bottlenecks-2026-09-27.md` → "Recommendations and status", and
filed to bug triage on the control instance.
