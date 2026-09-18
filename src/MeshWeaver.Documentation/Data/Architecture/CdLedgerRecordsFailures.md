---
Name: The CD Ledger Records a Failure, Not a Cadence
Category: Architecture
Description: 109 of 109 automated "incomplete image set" alarms recorded a delivery that had not failed — the two windows in which a missing image set is the normal state, the pair tag that cannot be satisfied in steady state because MeshWeaver.Plugins merges faster than a publish takes, the registry fingerprint that separates an attempt from an absence, and the rebuild-cadence decision this deliberately does not make.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 9v4"/><path d="M12 17h.01"/><path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/></svg>
---

# The CD Ledger Records a Failure, Not a Cadence

`main-cd.yml`'s hourly reconciler files an issue when it finds main's HEAD without its deployment
image set:

> **CD: main `<sha>` has an incomplete image set**
>
> main's HEAD `<sha>` is missing part of its deployment image set in ACR, so every self-updating
> install stays on the previous image.

It had filed that issue **109 times** by 2026-09-18, at up to 15 a day. Every one of them was read,
by anybody who looked, as a delivery outage — that is what the words say, and the label is
`ci-failure`.

**Not one of the 109 recorded a delivery that had failed.**

This page is what the alarm was actually firing on, how that was measured, what changed, and the
one question it deliberately leaves open.

## The predicate, and why it is almost always false

`gate` asks `check-image-set.sh` whether main's HEAD has a complete set. That script answers about
four artifacts in ACR:

| Artifact | What it means |
|---|---|
| `memex-portal-ai:<core-short>` | the portal image an install pulls, as a linux amd64+arm64 index |
| `memex-migration:<core-short>` | the schema migration Job's image |
| `mw-plugin-test:<core-short>` | the tester every satellite resolves |
| `memex-portal-ai:<core-short>-p<plugins-short>` | the **pair tag** — provenance, not a thing anything pulls |

The first three are **deliverability**: an install that cannot find them stays on its old image.
The fourth is **provenance**: `promote` phase A stamps it to record which `MeshWeaver.Plugins`
commit the portal hosts were built from ([#2622](https://github.com/Systemorph/MeshWeaver/issues/2622)
— the hosts live in that repo, so a merge there changes what the image should contain while core's
HEAD does not move).

All four shared one exit code. So did the reconciler's verdict, and so did the alarm.

### Window 1 — the pair tag is unsatisfiable in steady state

`gate` resolves `MeshWeaver.Plugins` `main` **afresh on every tick** and demands the pair tag for
*that* commit. Measured on 2026-09-14…18, `MeshWeaver.Plugins` `main` takes **43–48 pull-request
merges a day** — a median interval near 30 minutes. A CD publish takes about **43 minutes**
(run `35282036886`: started 22:26:33Z, promote tags written 23:02:57Z, closed 23:06:50Z).

A publish is therefore slower than the ref it is racing. The pair tag a run writes names a plugins
commit that has already been superseded before `verify-images` reads it back, so the next tick
finds the pairing stale again — **forever, by construction**. It is the shape the memory note
*"a LIVE CENSUS cannot measure PROGRESS — an arrival cancels a completion one-for-one"* describes.

The registry records it plainly. Core commit `0dadacc` sat as main's HEAD for four hours on
2026-09-17 and was built **three times**:

| `3.0.0-ci.N` | written | pair tag | digest |
|---|---|---|---|
| `3.0.0-ci.8883` | 22:03:12Z | `0dadacc-pf23ce32` | `sha256:9bfa9888…` |
| `3.0.0-ci.8885` | 23:04:24Z | `0dadacc-pa89a016` | `sha256:597a7651…` |
| `3.0.0-ci.8886` | 00:05:29Z | `0dadacc-p27029a5` | `sha256:3251daeb…` |

Three issues ([#4667](https://github.com/Systemorph/MeshWeaver/issues/4667),
[#4669](https://github.com/Systemorph/MeshWeaver/issues/4669),
[#4670](https://github.com/Systemorph/MeshWeaver/issues/4670)), three "✅ Healed" closures, one
unchanged source tree, and the `0dadacc` tag moved twice — orphaning the two earlier images. That
is the very shape `main-cd.yml`'s own comment calls a defect under *ONE COMMIT, ONE IMAGE SET*
([#3376](https://github.com/Systemorph/MeshWeaver/issues/3376)).

Across the retained staging tags: **418 publishing builds over 340 distinct core commits** — 78
rebuilds of trees that already had a complete, deliverable set. That is a floor, not a total; a
staging tag can age out.

### Window 2 — a publisher that has not been created yet

The other window has nothing to do with plugins. `gate`'s only wait condition was
`PENDING || (!GREEN && AGE_MIN < 120)` — it waits while CI is *running* and, the instant the
required check concludes green, treats a missing image set as unhealable. But at that instant
nothing has published, and nothing *can* have:

* GitHub takes minutes to CREATE the `workflow_run` delivery run after the required check
  concludes. Measured on `ecc544a`, 2026-09-17: check green **15:29:08Z**, CD run created
  **15:32:54Z** — 3 m 37 s. The scheduled tick decided at **15:30:05Z**, inside that gap, and filed
  [#4608](https://github.com/Systemorph/MeshWeaver/issues/4608).
* The build itself then takes ~43 more minutes.

The `INFLIGHT` probe that `#3376` added cannot cover this: it looks for runs that **already exist**,
and only ones with a **lower run id**. On `0dadacc` the scheduled run (`35276745073`, created
21:27:14Z) was created **34 seconds before** the genuine push-path delivery run (`35276798708`,
21:27:48Z) — newer id, later creation, invisible to the probe in both directions.

## What was measured

Every `ci-failure` issue titled *has an incomplete image set* — 109 of them, the complete
population — was resolved to its heal comment, to that run's `gate` job, and to that job's failure
**annotations**, which name the exact `repo:tag` the probe could not verify.

| What the annotations named | Issues |
|---|---|
| **Only** the pair tag — all three images present and multi-arch | **28** |
| The core sha tags too (i.e. HEAD had no set yet), pair tag included | **79** |
| The core sha tags only (pre-dating the pair check, 2026-08-30) | **2** |
| Unclassified / annotations unreadable | **0** |

For the 25 most recent of the 79, the CD runs on that head sha were then listed, excluding the run
that filed the issue and judging each by its state *at the moment of filing*:

| State when the alarm was filed | Count |
|---|---|
| **No CD run for that commit had ever been created** | **19** |
| The only prior run was a zero-job supersede cancellation | 3 |
| A run was mid-publish, newer than the tick, so the probe could not see it | 3 |
| A prior run had completed and failed to produce the set | **0** |

A zero-job cancellation is the one-pending-slot supersede rule; AGENTS.md says in as many words
that it *"cannot have torn a set or a seal"*.

So: 28 alarms over a complete, deliverable set; 81 over a set whose publisher had not run yet or
was still running. Zero over a failed delivery.

### What it was NOT

Two hypotheses were tested and refuted, because "something is deleting images" and "the heal does
not heal" are very different bugs and neither is this one.

* **Nothing deleted anything.** Both ACR purge tasks read `status: Disabled` live
  (`az acr task list --registry meshweaver`, 2026-09-18) — paused since 2026-09-12 under
  `Memex#219`, exactly as `.github/acr-retention/tasks.json` records. And the annotations name the
  pair tag, not a deleted image; the three sha tags resolved on every pair-only tick.
* **The heal healed.** Every reconcile run completed `Promote`, `Verify every image shipped`, the
  bake and the seal, and published a new `3.0.0-ci.N`. `Verify every image shipped` asserts the
  pair tag for the plugins sha **`gate` resolved at the start of that run** — which is the tag the
  run itself just wrote, so it is true by construction. The detector re-resolves the ref. Same
  script, same registry, same repositories, one parameter read at two different instants: verify
  cannot fail on it, and the detector cannot pass on it.

## What changed

**`check-image-set.sh` answers three things instead of two.**

```text
exit 0  complete, and paired with the plugins commit asked about
exit 1  an image of the set is missing, malformed, or could not be read  → NOT DELIVERABLE
exit 2  every image is present and good; only the pair tag is behind     → DELIVERABLE, HOSTS STALE
```

Ordering is the contract: a run that lost a leg **and** whose plugins HEAD moved exits 1, never 2 —
exit 2 asserts the set is intact, and saying that over a torn set is the one failure the file exists
to prevent. Both are non-zero, so `verify-images` and `release.yml`, which simply *run* the script,
keep today's behaviour exactly: a pair tag a run just wrote and cannot read back is still red there.
Only `gate` inspects the code.

**A stale host pairing publishes, silently.** `gate` still rebuilds — [#2622](https://github.com/Systemorph/MeshWeaver/issues/2622)
is unchanged, and the *publish* decision is byte-for-byte what it was — but it says what it is doing
and writes nothing to the ledger, the same discipline the batching path already follows.

**The ledger requires evidence of an attempt.** The fingerprint is in the registry, not in the API:
every publishing leg pushes `staging-<sha>-<run_id>` **before** `promote` applies a single
consumer-visible tag. A staging tag for this sha, with no older run live, is exactly *a publisher
started on this commit and did not finish* — which is
[#1026](https://github.com/Systemorph/MeshWeaver/issues/1026)'s shape and what the reconciler was
built to shout about. Without one, the tick is not repairing anything: it **is** the publisher, and
it says so.

It is a positive, clock-free condition. No grace period, no retry, no widened bound — a delivery
that has not started is a different fact from a delivery that failed, and the probe asks which.

The probe **fails towards the alarm**: an unreadable registry answers "attempted", because one extra
comment costs less than a missed delivery hole, and the warning names the read so a reader is never
guessing.

### What this costs

The attempt budget now starts one tick later. A first, ledger-free publish that dies leaves the
staging tags that make the *next* tick open the issue and count attempt 1 — four builds instead of
three before `cd-unhealed`, for a commit that genuinely cannot build. The run that dies is still a
`CD failed on main` alert in its own right, from the `verdict` job, which this does not touch.

## The controls

Both are in the existing harnesses, which **extract** the real step and the real script rather than
restating them, and both were run against the pre-fix tree to confirm they fail there.

`test-check-image-set.py` — a missing pair tag alone is exit 2 and a `::notice::`, a missing image
outranks it, and a complete correctly-paired set is still exit 0 (the inert control, so "exit 2"
cannot pass by being answered unconditionally). Two of the four fail before the fix.

`test-cd-steps.py` — the `decide` step's cases assert what it **asked GitHub to do**, read from the
recorded `gh` calls, because a decision that prints nothing alarming and still calls
`gh issue create` is precisely the defect and is invisible in stdout. A host-stale set publishes and
files nothing; a green commit with no staged layers publishes and files nothing; **the same inputs
with `ATTEMPTED=true` still open the issue and still record the attempt**; an empty probe fails
towards the ledger; a settled-red required check is still reported. Three fail before the fix,
including the one that shows the old step calling `gh issue create` with the body *"every
self-updating install stays on the previous image"* for a commit no publisher had touched.

## The decision that is still open

**This does not stop the rebuild loop, and stopping it needs a decision nobody has made.** It is
tracked on [#4688](https://github.com/Systemorph/MeshWeaver/issues/4688), which carries the same
three options and the measurements below.

The reconciler still rebuilds the portal image whenever `MeshWeaver.Plugins` `main` has moved —
so, at 43–48 merges a day against an hourly tick, up to 24 full multi-arch builds a day, each
publishing a `3.0.0-ci.N` and each a publication event the fleet rolls on. The measured sample of
plugins merges driving those rebuilds is dominated by commits that cannot change the portal image
at all: lock regeneration, i18n mirror syncs, doc and CI changes, `Merge main into <branch> — only
generated manifest.lock files conflicted`.

Core's own side of the same decision already has a relevance filter — `gate`'s `relevance` step
skips a build when every changed path since the newest published set is image-irrelevant. The
plugins side has **none**, and the core filter cannot supply it: when only plugins moved, core HEAD
*is* the newest published set, the compare is empty, and `relevant` stays `true`.

Three ways out, none of them free:

1. **A plugins-side relevance filter in core** — compare the published image's plugins provenance
   (recoverable from its own pair tag) against plugins HEAD, and rebuild only when a changed path
   can enter the image. Same shape as the core filter, same fail-open discipline. It encodes another
   repository's layout in this one, which nothing here compiles or checks.
2. **The producer moves to the repo that knows** — `MeshWeaver.Plugins`' own lane dispatches core's
   `main-cd` with `rebuild: true` (the operator door that already exists, documented for exactly
   this case) when a merge touches host paths, and the pair tag leaves the reconciler's publish
   trigger entirely. Architecturally the right owner; it is a cross-repo change, and per AGENTS.md
   the half that removes the automatic producer must land **last**.
3. **Move the host refresh to the daily cadence.** The maintainer's 2026-09-12 directive for how
   this fleet reacts to an upstream repo moving is *"do not recompile and run all tests on every
   platform build; the full run happens once a day"*. Applying it here is an analogy, not a quote.

Whichever is chosen, the alarm is no longer the thing that has to be silenced to choose it.

## No link to the stale portals

The issue body claims every self-updating install stays on the previous image. Measured on the
control instance (`memex.systemorph.com`) on 2026-09-18, the fleet is behind for reasons this
defect neither causes nor would fix:

* **memex-cloud** runs `3.0.0-ci.8411` (built 2026-09-12). Its own `Admin/UpdatePolicy` reads
  *"updates are disabled on this install (Admin/UpdatePolicy = None); the registry was not listed"*,
  with a `heldTag` of `3.0.0-ci.8339` held since 2026-09-11 over ~25 modules unloadable on that
  framework identity. **Self-update is off**; a fresher image would not move it.
* **pearl** runs `3.0.0-ci.8080` behind a deliberate pin that its record calls *"a FLOOR and the
  anchor the ACR retention lock protects"*.
* **build** runs `3.0.0-ci.8411` behind its provisioning pin, and its record notes the self-updater
  cannot check for updates on an instance pulling from `cr.meshweaver.cloud`
  ([#4093](https://github.com/Systemorph/MeshWeaver/issues/4093)).
* **memex** (the control instance) runs `3.0.0-ci.8844`, 13 tags behind the newest.

If anything, the loop has been *over*-supplying images. What holds the portals is update policy and
pins, and those are operational decisions, not this lane.

## See also

- [The Image Tag Contract](/Doc/Architecture/ImageTagContract) — which tags the promotion publishes, and the one that
  had no producer at all
- [Image Pair Skew](/Doc/Architecture/ImagePairSkew) — the same core/plugins pairing seen from the other end: each
  half green, the pair never run
- [Pinned Image Retention](/Doc/Architecture/PinnedImageRetention) — the paused purge tasks, and why republishing
  frequency destroys a pin rather than protecting it
- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — how a green wall and a zero-job cancellation are read
- [Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — what a sealed, delivered set is
