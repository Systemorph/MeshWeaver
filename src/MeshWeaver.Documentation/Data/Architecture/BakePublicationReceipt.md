---
Name: Reading a Bake Publication Receipt
Category: Architecture
Description: >-
  The one line a platform bake prints about what it published, field by field — and the outcome that
  had no word for it, so a target that already held the publication rendered exactly like a
  publication that reached nothing. Both readers got it wrong on the same two runs: a human filed it
  as release markers written for nothing, and the platform resolver passed two sealed sets over.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 3h13l3 3v15l-3-2-3 2-3-2-3 2-3-2z"/><path d="M8 8h8"/><path d="M8 12h8"/><path d="M8 16h5"/></svg>
---

# Reading a Bake Publication Receipt

`publish-bake-bundles.sh` ends every run with one line, and that line is the only machine-readable
account of what the bake did. It is **one physical line** — `resolve-platform.py` matches it only up
to a newline — so the break below is visual wrapping for this page, never something to copy:

```
bake published: identity=s8cea1c45… arch=linux-x64 source=meshweaver-content ⏎(wrapped)
  source-sha=a4d12f6a39… bundles=1 surface=true ⏎(wrapped)
  targets-published=0 targets-converged=0 targets-already=2 targets-superseded=0 ⏎(wrapped)
  release=3.0.0-ci.8459 release-markers=2
```

`resolve-platform.py --verify-source` parses exactly this line to decide whether a sealed set may be
resolved as the platform, so every word in it is load-bearing.

## The fields

| field | what it says |
|---|---|
| `identity` | the framework identity the publication is keyed on |
| `arch` | which architecture lane produced it (`linux-x64` / `linux-arm64`) |
| `source` / `source-sha` | WHOSE content was baked, and at which commit — the gate-selected source, not the run's head |
| `bundles` | how many bundles the publication carries; zero is never valid |
| `surface` | whether the platform surface travelled with it |
| `release` / `release-markers` | the release version this bake maps to the identity, and how many targets got that marker |

## The four target outcomes, and what each licenses

Every target lands in exactly one, and each is printed **on every run, including zero** — a number
that appears only when non-zero is a number nobody can use as a denominator.

| outcome | meaning | is the set live there? |
|---|---|---|
| `targets-published` | this run wrote and sealed the publication | yes, by this run |
| `targets-converged` | a sibling publication of THIS content had sealed it by the time this run's postcondition ran | yes, by a sibling |
| `targets-already` | the target was ALREADY sealed on this content when this run asked | yes, from before |
| `targets-superseded` | a NEWER publication is live there — either it appeared mid-upload, or the target's sealed commit already contains this one | **no** — a newer one is |

**`published + converged + already > 0` is the question a consumer should ask.** The first three all
mean *the set is live at that target*; only `superseded` means it is not.

## The outcome that had no word (#4247)

`targets-already` did not exist until 2026-09-14. The two branches that skip because the target is
already sealed on this content recorded **nothing**, so such a run's receipt read
`targets-published=0 targets-converged=0` — byte-identical to a publication that reached no target
at all. Both of its readers then got it wrong, on the same two runs:

- **A human** read `main-cd` #8457 and #8459 (both on `cdb2878bb`) as *two platform bakes that wrote
  release markers for a publication that reached ZERO targets*, and filed it. The bake log of #8459
  says, twice — once per target — `holds a COMPLETE publication of THIS content under
  prebuilt-bundles/s8cea1c45…/meshweaver-content (sentinel present, source a4d12f6a39…) — already
  published; skipping`, and the registry said the same a moment later. Nothing was wrong: a sibling
  run on the same commit had published first, and these two correctly declined to republish.
- **`resolve-platform.py --verify-source`**, whose check was `published + converged == 0 ⇒
  unattributable`, PASSED BOTH SETS OVER. That refusal is right for a publication that reached
  nothing and wrong for one that was already everywhere — and it cost two otherwise-sealed sets out
  of 26 in the window that was measured.

🚨 **And the same ambiguity, on a THIRD run, cost a whole repository.** On 2026-09-14
`MW_PLATFORM_REF` was moved to `3.0.0-ci.8547` — the newest sealed set — and MeshWeaver.Plugins
`main` and **every open pull request** went red on the first job of every run with *"its
source/release is unverified: platform-bake job 103845949792 has an incomplete or inconsistent final
publication receipt"*. That job's log says, twice, once per target: `holds a COMPLETE publication of
THIS content … (sentinel present, source 1c1d62adf…) — already published; skipping`, and the registry
said the same. The set was live at both targets; the receipt just had no word for it. The freeze was
read as naming a bad set, and [Continuous Integration Content Bake](/Doc/Architecture/CiContentBake)
recorded it as *"the set … had been published to no target at all"* — which is what a receipt that
cannot say "already" makes a careful reader believe.

**The markers are not the anomaly, and must not be.** `publish_release_marker` runs BEFORE the
sealed-skip on purpose: the version → identity mapping has to land on every run, including the
common one whose bundles are already published, or the release gates hold every environment on a
release that is perfectly fine — and an environment frozen for weeks is its own outage. So
`release-markers=2` beside `targets-published=0` is the CORRECT shape of an already-published run,
not a contradiction.

## What to read, and in what order

1. **Read the outcome words, not the run's colour.** A `success` says the script did not fail; it
   does not say the publication reached anything.
2. **`published + converged + already`** is "the set is live at N targets". If it is zero, ask why —
   `superseded` is a legitimate zero (something newer is live), and anything else is a real failure
   the script would already have exited 1 for.
3. **The log says which branch each target took.** Every skip prints a `::notice::` naming the
   target, the directory, and the reason. A receipt is a summary; the notices are the evidence.
4. **A receipt from before 2026-09-14 carries no `targets-already`.** The resolver defaults it to
   zero — such a receipt reads exactly as it always did, and an old already-published run still
   reads as unattributable. That is history, not a live defect.

## Related

[The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — what a published
image set guarantees ·
[Publication Seal Starvation](/Doc/Architecture/PublicationSealStarvation) ·
[Sealed Publication Generations](/Doc/Architecture/SealedPublicationGenerations) — which directory
`already published` is decided from ·
[Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — the wider family of answers that read like
a verdict they never gave
