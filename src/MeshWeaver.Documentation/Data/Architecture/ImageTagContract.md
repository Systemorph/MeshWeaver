---
Name: The Image Tag Contract
Category: Architecture
Description: A tag with no producer is not a contract. memex-portal-ai:latest was written by a lane that was retired, then deleted by retention, and every check in the fleet stayed green because nothing had ever asserted it. What the promotion actually publishes, how `latest` on the portal and the migration got a producer back (it moves with `<major>-latest` on every armed set), and the phase ordering the gate has to respect.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20.59 13.41 13.42 20.6a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82Z"/><circle cx="7" cy="7" r="1.2"/></svg>
---

# The Image Tag Contract

**Before you rely on a tag, ask two questions: what rewrites it, and what deletes it.** A tag that
resolves today because somebody once wrote it, and that nothing rewrites now, is not a contract — it
is a coincidence with a countdown on it. This page is the inventory of which tags the platform
actually promises, and the account of the one that was promised by nobody.

## What `promote` publishes

`main-cd.yml`'s `promote` job is the only writer of consumer-visible tags in **continuous
delivery**, and it writes exactly this set across the repositories of the image set (`mw-core-tests`, core's built test
assemblies, joined them as the set's fifth digest — the deploy-test-switch design in core PR #7182, decision D3):

| Tag | On | Written in | Kind |
|---|---|---|---|
| `<short-sha>` | `memex-portal-ai`, `memex-migration`, `mw-plugin-test`, `mw-core-tests` | phase A | immutable identity |
| `<version>` e.g. `3.0.0-ci.8079` | `memex-migration`, `mw-plugin-test`, `mw-core-tests` | phase A | immutable pointer |
| `main` | all four | phase B | moving pointer |
| `latest` | `mw-plugin-test` **only** | phase B | moving pointer, repo-scoped |
| `<version>` | `memex-portal-ai` | **phase C — in `arm`, not `promote`** | immutable pointer — *the arming write*, made only once MeshWeaver.Plugins' dependent suites passed against this set's pair (policy `one-promotion-gate`, [One Promotion Gate](/Doc/Architecture/OnePromotionGate)) |
| `<major>-latest`, `<major.minor>-latest`, `<major.minor.patch>-latest` | `memex-portal-ai`, `memex-migration` | **phase D — in `arm`** | moving line pointers; never moved backwards |
| `latest` | `memex-portal-ai`, `memex-migration` | **phase D — in `arm`**, in the same write as `<major>-latest` | moving pointer to the newest ARMED set; never moved backwards |
| `<major>-latest`, `<major.minor>-latest` | `mw-plugin-test` | **phase D — in `arm`**, before the portal's | the tester of the SAME armed set — the matched channel `mw build plugin` needs; the tester's `latest` stays phase B's |

Phase D writes every one of its pointers to ACR, to GHCR and — through the digest-verified
`mirror-image-to-registry.sh` — to the fleet registry `cr.meshweaver.cloud`.

The three legs compute `$(Version)` from the same root `Directory.Build.props` inside one workflow
run, so `GITHUB_RUN_NUMBER` is shared and the three version strings are **equal by construction**.
That is what makes "the version tag" a property of the *set* rather than of each image, and it is why
one argument suffices to assert it on all three.

There is exactly one other writer, and it obeys the same rule. An **official release**
(`release.yml`) retags the promoted set `<short-sha>` → the clean `<version>` (e.g. `3.1.0`) on all
three repositories, `memex-portal-ai` **last** — the same arming-write ordering phase C uses, for the
same reason. So a release adds one more immutable pointer, applied symmetrically. For a
`<major>.<minor>.<run>` set it moves no pointer on the portal or the migration — neither the line
pointers nor `latest`: CD's `arm` already moved them to that build or past it, so a release-side move
could only move them backwards. Only the tester's GHCR `latest` is still mirrored at release time.

> 📅 **2026-09-12 — and there is no tag that means "validated", by decision.** `<version>` is the
> arming write; it precedes `publish-bake` and `verify-images` (and preceded `plugins-bake` until #6143), so a set is *sealed*
> only by those jobs' conclusions and the `_releases/<version>` markers — which every reader
> (`resolve-platform.py` in the node repos, `check-release-availability.sh` in CD and `release.yml`,
> `ReleaseAvailabilityService` on an install) re-derives itself. "Passed the full run" is the next
> daily satellite runs staying green on it, not a pointer; the clean `X.Y.Z` is the only tag-shaped
> promotion. See `Hosting/BuildAndReleaseProcess` (MeshWeaver.Plugins; `get Hosting/BuildAndReleaseProcess` on the memex MCP).

## `latest` on the portal and the migration

The Aspire integration and the CLI default to `cr.meshweaver.cloud/memex-portal-ai:latest` and
`…/memex-migration:latest`. That default has to be **pullable**, so the tag needs exactly what the
section below shows it lacked: a producer, and protection from retention.

* **The producer is `arm`'s phase D.** `latest` is written in the same `imagetools create` as
  `<major>-latest`, from the armed set's staging tag, after the arming PUT and under the same
  never-backwards re-check. So `latest` names the newest **armed** set — what a fresh install should
  start from — and never a set that was merely promoted (that is `main`, phase B), and never an
  older set than the one already there. `promote` writes `latest` on the tester only.
* **The protection is by name.** `.github/release-channels.json` declares the `latest` channel with
  `arm` as a writer on both repositories; every pruner protects a declared label, and the purge
  record filters only per-build tag shapes. `lock-pinned-digests.py` still never LOCKS it (a moving
  label locked to a digest pins bytes the label has left) — it is protected by being excluded.
* **The self-updater never selects it.** `latest` has no numeric core, so `PlatformReleaseOrder`
  cannot place it and no update pattern can make it a candidate; it is only ever a first-start
  address, like the line pointers.
* **The tester's `latest` is NOT its pair.** `mw-plugin-test:latest` follows PROMOTED sets (phase B,
  MeshWeaver.Plugins' `MW_TEST_IMAGE`), the portal's `latest` follows ARMED ones, so after a promotion
  ahead of arming the two name different builds and `mw build plugin` refuses the pair at its
  framework-identity check. The matched pair is `mw-plugin-test:<major>-latest` with
  `memex-portal-ai:<major>-latest` (or `latest`): phase D writes the tester's line pointers first, from
  the armed set's own version tag, and refuses a set whose tester and portal disagree on the version.
  A consumer that pairs the two images names the tester by that pointer, or by the portal's version.
* **It moves only when the line pointers move.** A release of a retired-notation set moves it with
  them (as before); a release of a `<major>.<minor>.<run>` set moves neither.

`test-cd-steps.py` executes phase D against stub registries: the newest armed set writes `latest` on
both repositories in all three registries, a newer armed set leaves it untouched, a failing pointer
reader writes nothing (never `latest` without its line pointers), and a `promote` that wrote the
portal's `latest` is caught. `release-channels.py check` holds the declaration to the writers.

The portal's moving pointer for CI is still `main`; its selectable pointer is still `<version>`.

## What happened: two writers, then none (#3670)

`az acr repository show -n meshweaver --image memex-portal-ai:latest` answered *"the specified tag
does not exist"*, while the sibling `mw-plugin-test:latest` resolved. Every release-following bake in
the satellite repos (`FOLLOW_RELEASE=true`) failed its preflight at *"Resolve the bake target"*.

The tag had **two producers over its life, and ended with none**:

1. `.github/workflows/release-images.yml` wrote it, on an official release, with
   `az acr import … --image "$repo:$VERSION" --image "$repo:latest"`. That lane was **deleted in
   `28fc2da4b`** (2026-09-05, *"release: the 3.1.0 line — no rc, no NuGet, and a release is a
   promotion of a sealed set"*). Its replacement, `release.yml`, retags `<version>` in ACR and
   nothing else.
2. `promote` never wrote it. Phase B tags `memex-migration` and `memex-portal-ai` with `main` only,
   and then tags `mw-plugin-test` with `main` **and** `latest` — three lines apart, in the same step.
   Core CD has therefore never produced a portal `latest`, in any version of the workflow.

So the tag moved only at release cadence, was left on the `3.0.0-rc13` manifest of 2026-08-31, and on
2026-09-05 lost even that. Frozen means **stale, then eligible**. The retention rule
`.github/acr-retention/purge-old-images.yaml` purges with
`--filter 'memex-portal-ai:.*' --ago 7d --keep 10`, which matches `latest` like any other tag, and
`--keep 10` counts *newer builds* — a repository republished every merge burns through ten in a day.
ACR task `purge-old-images` run **`dt1c`, 2026-09-07T03:01:32Z**, deleted 241 `memex-portal-ai` tags,
these two adjacent in its log:

```text
Deleted meshweaver.azurecr.io/memex-portal-ai:3.0.0-rc13
Deleted meshweaver.azurecr.io/memex-portal-ai:latest
```

The GHCR copy is still there and still says the same thing — measured 2026-09-08, both
`ghcr.io/systemorph/memex-portal-ai:latest` and `:3.0.0-rc13` resolve to
`sha256:c832f85aa75f55ef15c02b11f8ce936fa958415b8540c1ef6aade65d140c72ab`, one digest, while
`ghcr.io/systemorph/memex-portal-ai:main` does not exist at all, because phase B mirrors only
`mw-plugin-test` to GHCR. A moving tag that has not moved in over a week is the whole finding in one
reading.

### Why nothing caught it

`check-image-set.sh` is the definition of "the set", and it was keyed **entirely on the commit's
short sha**, plus the pair tag (since retired — [Platform and Module Deploy](../PlatformAndModuleDeploy)). A sha-keyed assertion says nothing whatsoever about the tags
consumers actually name, so a promoted set could be complete and correct on every assertion this
repository made while a tag a whole satellite fleet resolves did not exist. Every check was green
throughout. #3438's pinned-digest lock does not cover it either: that guard deliberately classifies
`latest` as floating and refuses to lock it, because locking whatever a moving tag happens to point
at today pins the wrong bytes forever.

The satellite side was repaired separately — MeshWeaver.Reinsurance now resolves the released set by
`client_payload.version` and falls back to `main` for the scheduled poll, never `latest`.

## The gate: pointer symmetry, asserted at promotion time

### A failed registry read is not proof of an absent image (#3882)

CD run 8239 pushed `memex-migration:main` at 02:55:08 UTC on September 10, 2026.
Its verifier reported that tag missing at 02:58:31, but a subsequent registry read returned
the promoted digest with the same 02:55:08 last-update timestamp and both Linux architectures.
The checker had discarded the command's error output and called every nonzero exit "MISSING".
Those observations do not establish a deletion or explain the original read failure.

The checker now retains the registry error and reports the command's exit status for both
image-index reads and the core/plugins pair tag. It suppresses only Azure CLI's preview warning
with `--only-show-errors`. Failed reads still fail the gate, are not retried, and do not skip
the remaining image checks. Use the retained error to investigate the failed operation before
attributing it to retention, changing registry configuration, or rebuilding an existing image.

The executable regression exercises the real shell checker against successful multi-architecture
responses, single-architecture responses, missing tags, unavailable reads and refused operations.
It verifies that all eleven requested identities and pointers are attempted once.

### Required pointers

`check-image-set.sh` takes `--pointers <version>` and, when given it, asserts that **every named
pointer the promotion publishes resolves — as a `linux/amd64 + linux/arm64` image index — on every
repository the promotion publishes it to**:

* `main` on all three repositories (set-wide);
* `<version>` on all three repositories (set-wide);
* `latest` on `mw-plugin-test` (repo-scoped — the tester's own contract, which the satellites resolve
  as `vars.MW_TEST_IMAGE`). The portal's and the migration's `latest` are NOT asserted here: `arm`
  writes them in phase D, often in a later run than the one that promoted the set — the same reason
  the portal's `<version>` is not asserted (below).

A promoted pair where only one half carries the tag is now unrepresentable: it fails the run that
produced it, naming the repository and the tag. `release.yml` runs the same assertion before it
promotes a continuous set to a release, so an asymmetric set is not releasable either.

### 🚨 The ordering constraint — why the flag is opt-in

> Under policy `one-promotion-gate` phases C and D run in main-cd's `arm` job, for the
> newest promoted set whose dependent suites passed — often in a later run than the one that built
> it. `--pointers` therefore never asserts the portal's `<version>`; the script says so.

`promote`'s phases are ordered so that a mid-flight failure is unobservable
([The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract)), and phase C —
`memex-portal-ai:<version>` — is the **arming write**: the single manifest PUT that
`SelfUpdateHostedService` acts on. The pointer set is therefore only whole *after phase C*, and the
assertion has to be opted into by a caller that runs there:

| Caller | Passes `--pointers`? | Why |
|---|---|---|
| `verify-images` (`needs: promote`) | **yes** | it asserts what promote shipped — `main`, `latest` and `<version>` on the migration and the tester; NOT the portal's `<version>`, which `arm` writes later (policy `one-promotion-gate`) |
| `release.yml` "The set is complete" | **yes** | it promotes an already-promoted set; phase C is long past |
| `gate`'s reconcile probe | **never** | it runs *before* `promote`; this run's version does not exist yet |

The last row is the trap, and it is why the default had to stay the safe one. `gate` calls the same
script to decide whether main's HEAD needs healing at all. If that probe asserted a pointer a later
phase legitimately has not written, it would answer *incomplete* on every tick, forever, and the
reconciler would publish forever. **A completeness probe must never fail on state its own success is
supposed to produce.**

There is no skip-trapdoor in any of it: nothing is conditioned on whether a variable is set, nothing
carries `continue-on-error`, and an empty version reaching the flag is reported RED — after the
per-image diagnostics, so a run whose image leg died first names the image that could not be
verified, its registry error and the command's exit status, rather than leading with a message
about a flag. The registry's own diagnostic distinguishes an absent tag from a failed read.

## The general lesson

This is a family, not an incident, and its members all look the same from outside: a check red for
something that "should obviously be there".

**Name the mechanism that was supposed to make the assertion true.** If you can name it, the red is a
finding and repointing the assertion destroys it. If you *cannot* name one, the assertion was a guess
— and #3670 is the case where the mechanism could not be named, because it had been deleted. The
repair is then neither "repoint the consumer" nor "put the tag back": it is to decide which tags are
the contract, give them one producer, and make something assert them. A tag nobody asserts is
indistinguishable from a tag nobody writes, right up until a consumer needs it.

Two corollaries worth carrying:

* **Retention deletes what nothing rewrites.** `--ago`/`--keep` cannot tell a deliberate moving
  pointer from an abandoned one; only a producer can. Raising the window moves the cliff rather than
  removing it — see [Pinned Image Retention](/Doc/Architecture/PinnedImageRetention).
* **A stale moving tag is worse than a missing one.** `memex-portal-ai:latest` on GHCR still resolved
  in September, frozen on `3.0.0-rc13`; the chart default and the compose files named exactly that
  reference. An absent tag fails loudly at pull time. A frozen one starts, and looks healthy. That is
  why `latest` came back only with a writer on every arming, never as a one-off retag.

## See also

- [The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — all-or-nothing
  publication, the A/B/C phase ordering, and the self-healing reconciler that consumes the same
  completeness answer.
- [Pinned Image Retention](/Doc/Architecture/PinnedImageRetention) — why republishing frequency
  destroys a pin rather than protecting it, and why the manifest lock is the protection.
- [What a Synthetic Probe May Assert](/Doc/Architecture/SyntheticProbeTargets) — the discriminator
  ("which half is wrong: the assertion, or the environment?") that this page is the other worked
  example of.
- [Release Process](/Doc/Architecture/ReleaseProcess) — what a release promotion is, and what it
  retags.
- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — the broader family: a check that never
  looked is indistinguishable from one that passed.
