---
Name: Seal Only Settled Content
Category: Architecture
Description: >-
  Core CD binds every set it builds to ONE MeshWeaver.Plugins commit and seals the Plugins
  publication from it. It used to take the raw `main` HEAD — a commit Plugins' own lane may have
  refused to publish because its manifest locks were not settled. Now the set binds to the newest
  first-parent commit that Plugins' OWN settle check accepts, names the tip and every lock that would
  move when that is not the tip, and fails red when no settled commit is in reach.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="10" width="16" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/><path d="M12 14v3"/></svg>
---

# Seal only settled content

MeshWeaver.Plugins owns its `manifest.lock` files on `main` (`lockOwner: main`): a source merge
leaves them stale, `settle-locks` proposes the settle as a pull request, and **a run publishes,
seals and tags only when its own commit is settled** — every lock describes its tree and every
package floor is stamped. Every other run says so and publishes nothing:

```
fd3dbd1e11 is NOT settled (22 lock(s) would move) — this run publishes, seals and tags nothing;
the settle pull request's merge publishes this tree.
```

That rule held in Plugins' lane. It did not hold in core's.

## What happened (2026-10-04)

Core's `main-cd` resolves ONE Plugins commit in `gate` ("Resolve the plugins HEAD this image set is
bound to") and threads it everywhere: the portal hosts, `Plugins: pack the module bundles the bake
composes`, and `Plugins: bake + seal the publication for this identity`. It took
`git ls-remote refs/heads/main` — the raw tip.

Core run 37208911281 resolved `plugins HEAD (main) = fd3dbd1e…`, the very commit Plugins run
37208121672 had refused, and packed and sealed its bundles. The seal therefore carried a Hosting
prebuilt compiled against an AI tree whose lock still said 1.20.4 and whose 1.21 was never
published. The control instance kept its installed AI 1.20.4; the adopted Hosting prebuilt called a
setter AI 1.20.4 does not have (`MissingMethodException: set_Group`); the PR reviewer could not
start, and with it nothing merged anywhere.

The publication rule was right. The platform simply had a second door that did not ask.

## The rule

**A sealed set binds only to a content commit the content repository would itself publish.**

`gate` now clones Plugins (blobless) and runs `.github/scripts/resolve-settled-content.sh`, which
walks the trunk's **first-parent** chain from the tip (at most 10 commits) and at each commit runs
exactly what Plugins' `settle-locks` job runs to decide `own`:

| Check | Unsettled when |
|---|---|
| `python3 scripts/gen-manifests.py --settle` | any `*/manifest.lock` would move |
| `python3 scripts/mesh-floors.py check --require-stamped` (where present) | it fails — a floor is not stamped for its sources |

The scripts are read **from the commit being judged**, never from a copy here, so core's verdict is
Plugins' verdict by construction, and a change to Plugins' settle rule changes this answer with it.

* **Tip settled** — the set binds to the tip; the log says so.
* **Tip unsettled** — a workflow **warning** names the tip and every lock that would move, and the
  set binds to the newest settled first-parent ancestor, named next to it. That is not a fallback to
  "something older": it is the commit Plugins' own last publication came from, and the image, the
  module pack and the seal all bind to it together, so they stay one consistent pair. The settle
  pull request's merge makes the tip sealable on the next run.
* **No settled commit inside the walk** — **red**, naming the tip and its moving locks. Ten
  unsettled merges in a row means the settle pull request is stuck, and that is exactly what must be
  seen rather than sealed around.
* **The settle check fails** (exit ≠ 0 — it could not read its baseline or the remote) or **the
  commit has no `scripts/gen-manifests.py`** — **red**. An instrument that cannot decide is never
  read as "settled", and never walked past.

Only the first parent is walked: a settled commit reachable only through a merged branch never
reached `main` as a tree of its own, and Plugins never published it.

## Why bind back instead of failing on every unsettled tip

An unsettled tip is the **normal** state of Plugins `main` between a source merge and its settle
merge. Failing core CD for it would turn every core merge in that window red for a state nothing is
wrong with — and a red that means "wait" is the cry-wolf red [Reading CI Signals](../ReadingCiSignals)
warns about. Binding to the newest settled commit publishes exactly what Plugins itself would
publish; the red is reserved for the state that IS wrong (the settle never landing, or a check that
cannot decide).

## Verifying it

`python3 .github/scripts/test-settled-content-resolution.py` (run by the CI-scripts lane) builds
throw-away repositories whose stub settle scripts are driven by a file committed with each commit,
and runs the real resolver: settled tip, unsettled tip with a settled parent (the incident), pending
floors, a settled commit on a second parent only, the walk bound, a failing settle check, a missing
script, and the clone left clean. Its **negative control** mutates the resolver to ignore moved locks
and asserts the incident case then fails — so the harness cannot pass while measuring nothing.

Against the real repository, the resolver on `fd3dbd1e` reports the same 22 moving locks Plugins'
lane reported — and walking 12 first-parent commits back from it finds **none** settled: the source
merges move locks, and the settle merges `0e4890f4b2` and `9591a45796` leave every lock current but
carry floors not stamped for their sources. Plugins' own run on `0e4890f4b2` (37204680734) agrees:
*"carries package floors that are not stamped for their sources — this run publishes, seals and tags
nothing"*. So on that afternoon the correct answer was the red, not any commit: Plugins had published
nothing settled within reach, and every set sealed in that window carried a tree its own repository
had withheld.

## What this does not cover

* **Satellites.** Core CD does not seal a satellite's tree; satellites publish from their own lanes
  (`node-repo-publish-bake.yml`). Whether every satellite lane refuses an unsettled commit the way
  Plugins' `settle-locks` does is NOT established by this change.
* **The dependency a prebuilt records for a module.** A prebuilt bound to a module build that is not
  installed must be refused at adoption — a separate change.
* **A settle that never lands.** This page makes it red; landing it is the settle pull request's job.

## Related

* [CD Reconciles the Plugins Seal](../CdReconcilesThePluginsSeal) — the other way a `plugins`
  publication goes missing for a sealed set, and how the reconciler repairs it.
* [Image Pair Skew](../ImagePairSkew) — why the Plugins commit is resolved once and threaded.
* [Module Build Architecture](../ModuleBuildArchitecture) — the one build shape every repo runs.
