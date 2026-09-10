---
Name: The Image Tag Contract
Category: Architecture
Description: A tag with no producer is not a contract. memex-portal-ai:latest was written by a lane that was retired, then deleted by retention, and every check in the fleet stayed green because nothing had ever asserted it. What the promotion actually publishes, why `latest` is not part of it, and the phase ordering the gate has to respect.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20.59 13.41 13.42 20.6a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82Z"/><circle cx="7" cy="7" r="1.2"/></svg>
---

# The Image Tag Contract

**Before you rely on a tag, ask two questions: what rewrites it, and what deletes it.** A tag that
resolves today because somebody once wrote it, and that nothing rewrites now, is not a contract — it
is a coincidence with a countdown on it. This page is the inventory of which tags the platform
actually promises, and the account of the one that was promised by nobody.

## What `promote` publishes

`main-cd.yml`'s `promote` job is the only writer of consumer-visible tags in **continuous
delivery**, and it writes exactly this set across the three repositories of the image set:

| Tag | On | Written in | Kind |
|---|---|---|---|
| `<short-sha>` | `memex-portal-ai`, `memex-migration`, `mw-plugin-test` | phase A | immutable identity |
| `<core-short>-p<plugins-short>` | `memex-portal-ai` | phase A | immutable identity (the pair tag, #2622) |
| `<version>` e.g. `3.0.0-ci.8079` | `memex-migration`, `mw-plugin-test` | phase A | immutable pointer |
| `main` | all three | phase B | moving pointer |
| `latest` | `mw-plugin-test` **only** | phase B | moving pointer, repo-scoped |
| `<version>` | `memex-portal-ai` | **phase C** | immutable pointer — *the arming write* |

The three legs compute `$(Version)` from the same root `Directory.Build.props` inside one workflow
run, so `GITHUB_RUN_NUMBER` is shared and the three version strings are **equal by construction**.
That is what makes "the version tag" a property of the *set* rather than of each image, and it is why
one argument suffices to assert it on all three.

There is exactly one other writer, and it obeys the same rule. An **official release**
(`release.yml`) retags the promoted set `<short-sha>` → the clean `<version>` (e.g. `3.1.0`) on all
three repositories, `memex-portal-ai` **last** — the same arming-write ordering phase C uses, for the
same reason. So a release adds one more immutable pointer, applied symmetrically; it adds no floating
tag, and since `28fc2da4b` it writes no `latest` at all.

**There is no `memex-portal-ai:latest`, and that is the contract, not an omission.** The portal's
moving pointer is `main`; its selectable pointer is `<version>`. Nothing in `.github/` writes a
portal `latest`, and nothing should: a floating tag on the portal would have no producer this
repository can name, and `lock-pinned-digests.py` refuses to lock floating tags by design
(`FLOATING_TAGS = {"latest", "main", …}`), so it would carry no protection from retention either — it
would age out exactly as the last one did.

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
short sha**, plus the pair tag. A sha-keyed assertion says nothing whatsoever about the tags
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
  as `vars.MW_TEST_IMAGE`; deliberately *not* generalised to the portal).

A promoted pair where only one half carries the tag is now unrepresentable: it fails the run that
produced it, naming the repository and the tag. `release.yml` runs the same assertion before it
promotes a continuous set to a release, so an asymmetric set is not releasable either.

### 🚨 The ordering constraint — why the flag is opt-in

`promote`'s phases are ordered so that a mid-flight failure is unobservable
([The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract)), and phase C —
`memex-portal-ai:<version>` — is the **arming write**: the single manifest PUT that
`SelfUpdateHostedService` acts on. The pointer set is therefore only whole *after phase C*, and the
assertion has to be opted into by a caller that runs there:

| Caller | Passes `--pointers`? | Why |
|---|---|---|
| `verify-images` (`needs: promote`) | **yes** | it asserts what the run shipped; phase C is behind it |
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
* **A stale moving tag is worse than a missing one.** `memex-portal-ai:latest` on GHCR still resolves
  and is over a week old; the chart default in `deploy/helm/values.yaml` and the compose files name
  exactly that reference (the AKS deploy script repoints it to ACR, which is why nothing has tripped
  over it). An absent tag fails loudly at pull time. A frozen one starts, and looks healthy.

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
