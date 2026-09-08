---
Name: An install updates to clean releases by default
Category: Feature
Description: Self-update now waits for a clean release such as 3.0.1 unless you tell it which continuous builds to follow. Settings → Updates gains a version pattern — set it to 3.0.0-ci* to keep tracking the current line's builds; leave it empty to move only on releases.
Icon: ArrowSync
Order: -20260908
---

# An install updates to clean releases by default

Until now an installation on the **Continuous** update strategy rolled itself onto every
build-numbered image the pipeline published — `3.0.0-ci.8059`, then `ci.8079`, and so on. That is
what a development portal wants and what a production one does not: a production install should
move when a release is *labelled*, not every time a build passes.

## What changed

- **The default strategy is now Stable.** A new installation takes the newest clean release
  (`3.0.1`) and nothing before it. Nothing about an existing record is rewritten.
- **Continuous builds are opt-in, by pattern.** Settings → Updates has a new **version pattern**
  field. `Continuous` together with `3.0.0-ci*` follows exactly that line's builds — newest run first
  — and stops matching the day `3.0.1` is tagged, which is the intended way for "follow the line" to
  end. `3.0.1-ci*` then follows the next one.
- **Continuous without a pattern behaves like Stable.** If your record already reads `Continuous`
  and you want it to keep tracking builds, set the pattern; the update log says so once, naming the
  record and the pattern to set.
- **`None` is unchanged** — no automatic update at all.

## Why a pattern rather than a switch

A pattern says *which* builds you accept, not merely *that* you accept builds. It cannot cross a
release boundary you did not write down (`3.0.0-ci*` never selects `3.0.1` or `3.0.1-ci.4`), so a
portal that was meant to follow one line during its development does not silently continue onto
the next. The order of the builds a pattern admits is still the pipeline's own publication order.

The moving image pointers the pipeline now maintains — `3-latest`, `3.0-latest`, `3.0.1-latest` —
are the image a fresh installation *starts* from; self-update never treats them as a version to roll
to.
