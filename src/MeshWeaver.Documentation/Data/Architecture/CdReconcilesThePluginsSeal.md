---
Name: CD Reconciles the Plugins Seal
Category: Architecture
Description: >-
  A platform set seals on its trio alone, so it can seal without the `plugins` publication every
  satellite then asks for under its framework identity — and nothing re-attempted that. Why the fix
  repairs the state instead of forbidding it (six of nine measured cases were Plugins' own red, and
  refusing to seal would have frozen the portal image for each), and how the reconciler decides, by
  name, which identity is missing a publication.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 3v6h-6"/><path d="M12 8v4l3 2"/></svg>
---

# CD reconciles the Plugins seal

A core CD run publishes two different things for one commit, and until MeshWeaver#4539 only one of
them could heal.

* **The platform set** — the images, plus the platform's own content bake. It is *sealed* when the
  trio `Promote: tag the full set`, `Verify every image shipped` and `Bake platform content in the
  shipped image + publish` have all succeeded. That trio **is** the seal and nothing else is
  (maintainer rule, 2026-09-12; it is stated in `resolve-platform.py`'s module docstring and read by
  every satellite's copy of it).
* **The `plugins` publication** — the NodeType bundles baked from MeshWeaver.Plugins against that
  set's portal image, sealed to the artifact store under the set's **framework identity**.

Every satellite resolves the newest *sealed* set and then fetches `plugins` **under that set's
identity**. So a set can seal while its `plugins` publication does not exist, and every satellite
holds — correctly — on an upstream nobody is going to publish.

## What it looked like

On 2026-09-16 core CD #8765 promoted, verified and baked `3.0.0-ci.8765`. Then
`Plugins: pack the module bundles the bake composes / Warm the shared build environment` died
pulling the tester image:

```
Error response from daemon: Get "https://meshweaver.azurecr.io/v2/":
dial tcp 51.12.25.82:443: connect: connection refused
```

`Plugins: bake + seal the publication for this identity` was therefore **skipped**, and framework
identity `s5ec352bb102e5a2275e3831a08ac0c8d` got no `plugins` publication. At 20:14:20Z four
satellites resolved 8765 — the newest sealed set — and all four failed in the same second with

```
upstream 'plugins' answers 404 … no sealed publication for source 'plugins'
under framework identity 's5ec352bb…'
```

**Nothing re-attempted it.** MeshWeaver#2452 had given the *platform* bake a reconcile path
(`bake_only`), but the three `plugins-*` jobs were still gated on `publish == 'true'`, so every
hourly tick re-asserted the platform bake and left the publication missing. What healed it was a
coincidence: MeshWeaver.Plugins merged, which moved plugins HEAD, which made the image set read
INCOMPLETE, which produced a full re-publish.

## It is not a one-off

Sweeping the 200 core CD runs of 2026-09-14 → 09-17, **nine** sets sealed with their Plugins seal
not `success`:

| Run | Why the seal did not happen | Class |
|---|---|---|
| 8765 | ACR refused the tester-image pull | infrastructure |
| 8711 | the seal's own push to the fleet registry failed | infrastructure |
| 8601 | the promoted tester digest could not be resolved | infrastructure |
| 8599, 8647, 8663, 8666, 8670, 8674 | Plugins' module tests / bundle build failed | Plugins content |

That split is what decides the design: **three of nine are transient, six are a genuine Plugins
red.**

## Why the set still seals — the shape that was rejected

The obvious alternative is to make the seal all-or-nothing: no platform set until its `plugins`
publication exists. That removes the broken state rather than repairing it, and it was rejected on
the measurement above.

* **Six of the nine were Plugins' own red.** Under an all-or-nothing seal, core would have sealed
  *nothing* for each of those windows. A MeshWeaver.Plugins unit test would freeze the portal image
  that every AKS install self-updates to — a strictly worse outage than the one being fixed.
* **It reverses a dated maintainer rule** (the trio is the seal, 2026-09-12) and hands
  MeshWeaver.Plugins a veto over the platform's delivery. That is the dependency direction
  `PlatformNeverDependsOnPluginsGuard` forbids, and the one `satellite-compat` already refuses in
  the same words: *"Blocking the publish on a satellite's compile would hand every satellite a veto
  over the platform's delivery."*
* **It could not be an invariant anyway.** MeshWeaver.Plugins' own CI publishes for the same
  identity at any later time, so "sealed ⇒ published" is not a property core can maintain alone. It
  would hold at seal time only — which is exactly the moment it already holds when nothing fails.

So the platform seal is unchanged, and the reconciler learned to finish a set that is missing its
publication.

## How the reconciler decides

The decision lives in `gate`, which owns every other publish decision in `main-cd.yml`, and it runs
only on the `bake_only` path — the reconcile tick that finds a complete image set.

1. **`bake_version`** recovers the promoted set's release version off `memex-portal-ai:<short-sha>`'s
   tag set. (This read used to live in `publish-bake`'s `release` step; it moved here because a
   second consumer appeared, and two independent resolutions of one fact is the shape
   `gate.image_tag`'s own comment records getting wrong seven times.)
2. **`seal`** runs `check-release-availability.sh <version> plugins` — the same script, against the
   same store, that `publish-bake` already uses for `meshweaver-content` and that `release.yml`
   already uses for **both** sources. CD asking about only half of what it ships was the whole gap.
3. Its answer becomes `gate.plugins_seal_due`, and the three `plugins-*` jobs gain a second way in,
   exactly as `publish-bake` did in #2452.

### The probe names the identity

A publication seal is **per framework identity**. An "is it sealed?" check that does not name one is
answering confidently about something else.

The identity is the API-surface hash `s<hash>` of the portal's reference assemblies. It is a
property of the **binaries**, not of the source (#1725), so it cannot be computed from a tag or a
commit — resolving it means running `mw-plugin-test framework-identity` against the portal's `/app`.
The one thing outside the image that knows it is the `_releases/<version>` marker that
`publish-bake-bundles.sh` writes on every run, and `check-release-availability.sh` reads it. That is
why the probe is keyed by release version and why the version had to be resolved first.

### Three answers, and only one licenses a re-attempt

`check-release-availability.sh` deliberately keeps its refusals distinguishable, and the step keeps
them distinguishable too:

| Probe says | `plugins_seal_due` | Why |
|---|---|---|
| `all N source(s) are published for identity s…` | `false` | The control: nothing to do, and nothing is written to any ledger. |
| `N of M source(s) are not available for framework identity s…` | `true` | The only answer that is a statement about the publication. |
| `CANNOT RESOLVE … has no marker at …` (no `_releases` marker **at all**) | `false`, said as **NOT MEASURED** | The platform content bake has never published this release — which is what `publish-bake` is running to fix on this same tick. The next tick judges the seal. Self-clearing, with no timer and no retry. |
| `CANNOT RESOLVE … is empty` (a marker **exists** but records no identity) | — the step goes **RED** | The producer wrote a marker and recorded no identity. No later tick repairs that, so it is a defect, not a pending bake. |
| `CANNOT DETERMINE …` (the marker's existence or content, or a source, could not be read) | — the step goes **RED** | The gate could not ask its question. *Cannot determine* is not *clear to proceed*, and it is not *nothing to do* either. `az`'s own reason (an expired login, throttling, a missing share) is carried in the message. |

🚨 **The benign row is matched by its OWN wording, never by the `CANNOT RESOLVE` prefix.** The empty-marker
refusal opens with the same prefix, so a step keyed on the prefix answers a producer defect with a green.
And the script **asks whether the marker exists before reading it**: until the #4539 review fix, a failed
read — auth, throttling, network — was reported as "has no marker" and landed in the benign row, so a
storage outage read as a pending bake. `.github/scripts/test-cd-steps.py` pins all three rows, and
asserts each phrase still exists in the real script so a stub cannot describe an outcome the script no
longer produces.

An absence that printed no identity is also a red: the finding names no identity and cannot be acted
on.

### Why a re-attempt is repair and not a retry loop

* **Idempotent.** `publish-bake-bundles.sh` skips a sealed directory whose content × framework
  identity already match, so a re-attempt that races a publication into place is a no-op.
* **It cannot publish backwards, structurally.** `bake_only` means `check-image-set.sh` found the
  pair tag `memex-portal-ai:<core-short>-p<plugins-short>` for plugins HEAD *now* — so the promoted
  set was built from exactly the commit a re-attempt would bake. A merge in MeshWeaver.Plugins moves
  HEAD, the pair tag stops resolving, the set reads INCOMPLETE, and the ordinary full-publish path
  takes over instead. On top of that the publisher's own never-seal-backwards compare still runs,
  unchanged — including the #4152 fix that gives it the **content** repository's token, without
  which the compare answers 404 when core bakes the private MeshWeaver.Plugins.
* **Bounded.** For a fixed (core sha, plugins sha) pair the bake is deterministic: it failed on
  infrastructure and will likely pass, or on Plugins' content and will fail identically for as long
  as that commit is HEAD. A fourth attempt buys no information and costs ~30 minutes of runner time
  an hour, so the budget is **3 per pair**, the slot is consumed when an attempt *starts*, and a new
  pair resets it. The ledger is marker comments on an issue labelled `cd-plugins-seal` — its **own**
  label, because the `ci-failure` ledger closes itself when an image heal succeeds (#3176) and an
  image heal says nothing about a publication.

### And the reconcile is now judged

`delivery-verdict`'s every-leg check was `publish == 'true'`-only, and its reconcile branch fires
only on an *incomplete* set — while `bake_only` is by definition the *complete*-set branch. So a
bake-only tick was judged by nothing. It now carries its own arm: `publish-bake` always, and the
three `plugins-*` legs when `plugins_seal_due` was `true`. A leg that skipped is red there, on the
path that now performs repairs.

## What this does not cover

* **The fleet-registry copy.** `check-release-availability.sh` probes the Azure Files share, which
  is what `/api/plugins/bundles/prebuilt/{identity}/{source}` serves and therefore what the
  satellites' gates read. The ORAS copy pushed to `cr.meshweaver.cloud` is not probed, so a seal
  that completed the share publication and then failed its registry push (the 8711 shape) reads as
  available here.
* **A satellite's own upstreams.** A satellite that declares several upstreams still waits for each
  of them; that is the documented wait, not this defect.

## Reading the result

The gate's step summary carries the verdict in one line, always naming the identity and the release:

```
🔁 re-attempt 1/3 — `plugins` is NOT sealed for `s5ec352bb…` (release `3.0.0-ci.8765`),
   so this reconcile bakes and seals it.
```

A satellite holding on a missing publication is the gate **working**. The bug was always the missing
publication, never the hold.

## Related

* [Reading CI Signals](../ReadingCiSignals) — how to read the satellites' 404 when it happens.
* [CI Content Bake](../CiContentBake) — what a framework identity is and how a bake is addressed.
* [Bake Publication Receipt](../BakePublicationReceipt) — what a sealed publication contains.
* [Module Build Architecture](../ModuleBuildArchitecture) — the one build shape every repo runs.
