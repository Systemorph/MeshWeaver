---
Name: Pinned Image Retention
Category: Architecture
Description: Registry retention deletes what CI pins, and republishing frequency is what destroys a pin rather than what protects it — the second clause the purge rule was missing, the guard that names a dead pin, and the nightly job that locks every pinned manifest so the purge skips it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 17v5"/><path d="M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z"/></svg>
---

# Pinned Image Retention

**A container registry's retention policy and a CI pin are two rules about the same bytes, and
nothing was reconciling them.** On 2026-09-05 the `meshweaver` registry's nightly purge deleted the
manifests `MeshWeaver.Education`'s `main` pinned. Every run of that repo's `Disposable-mesh gate`
then died at `manifest unknown` before a single content gate executed — **including `main`'s own
nightly**, so the repository lost its gate and its green baseline in the same instant. Education,
Manufacturing and SocialMedia went down together at 15:29Z (#3438).

## The rule that was there, and the clause it was missing

The registry's older purge task carries its own reasoning, written after the previous incident
(Memex#122, when the whole `memex-website` repository was deleted and the public brand site served
503 for eleven hours):

> The test for adding any repository here: **if this tag were deleted tonight, would anything
> recreate it?** The repositories below are continuously republished. A pinned tag is not.

That rule is correct and it does not cover this failure. `mw-plugin-test` **passes** it: it is
republished many times a day and carries `latest`, `main`, a version tag, a git sha and several
`staging-…` tags at all times. By the recreate test it is a perfectly legitimate purge target.

It broke anyway, because the hazard is not "nothing rebuilds this repository". It is:

> **Something PINS a specific digest of it — and `--keep N` is counted in NEWER BUILDS, so frequent
> republishing is what DESTROYS the pin rather than what protects it.**

Every satellite pins the platform images by digest, and a pin is *designed* to sit still for weeks:
pins move as one set, deliberately and rarely ([Module Versioning](../ModuleVersioning),
[Module Build Architecture](../ModuleBuildArchitecture)). `--ago 7d --keep 10` guarantees that any pin
older than about a week, or displaced by ten newer builds, names bytes that no longer exist. The
retention window and the intended pin lifetime are in direct contradiction.

So the retention rule has two clauses, not one:

1. *(Memex#122)* If nothing would recreate this tag, never age-purge it.
2. *(#3438)* **If anything PINS a digest of this repository, the purge must not delete what is
   pinned** — regardless of age, and regardless of how many newer builds exist.

## 🚨 The triage trap: the purge and the breakage are never adjacent in any log

Carried forward from Memex#122 unchanged, because it applies identically to a CI runner:
**image caches hide the deletion.** A pod keeps serving from its node's image cache and a warm
runner from its own layer cache long after the manifest is gone. The failure surfaces at the next
*fresh* pull — a reschedule, a new runner, a new environment — which can be hours or days later.

Do not look for an `ImagePullBackOff` or a `manifest unknown` near the purge timestamp. There will
not be one. Work from the pin instead: read the digest out of the repository's workflow and ask the
registry whether it still exists.

## The guard — a dead pin named before a repository discovers it, an unprotected one before it dies

`.github/scripts/check-pinned-digests.py`, run daily by
`.github/workflows/pinned-digests.yml` at 05:20 UTC — **after** the 01:00 lock and the 03:00 purge —
and again on any push to `main` that touches it, the lock workflow, or the retention record.

Its **existence** arm does not prevent the deletion. It converts *"an entire repository's CI is
inexplicably dead, including its nightly"* into one red naming the repository, the declaration and
the digest.

🚨 **Existence is a LAGGING indicator, and until 2026-09-08 it was the only one.** A manifest that
is `GONE` is a past incident — the wedge has already happened and the repository is already dead.
The leading indicator is one field away and the *same* `az acr repository show` already carries it:

> a pinned manifest whose `deleteEnabled` is still **true** is not protected, and `--ago 7d
> --keep 10` guarantees it dies. The only open question is which night.

So the sweep reads `changeableAttributes.deleteEnabled` off every pin it resolves — no extra call,
`-o json` instead of `-o none` — and classifies it:

| Verdict | Meaning | Red? |
|---|---|---|
| `PROTECTED` | `deleteEnabled == false` — the purge skips it | no |
| `UNPROTECTED` | unlocked, and the lock job has had a scheduled opportunity | **yes** |
| `PENDING-LOCK` | unlocked, but the pin moved *after* the last scheduled lock | no — counted |
| `INDETERMINATE` | the registry did not report the attribute, or the opportunity could not be established | **yes** |

**The default is never `PROTECTED`.** Only the registry saying `deleteEnabled: false`, in its own
words, earns it; "the field was not in the response" is INDETERMINATE. That asymmetry is the point —
the failure being guarded against is a protection that quietly stopped, and a protection that cannot
be read has not been shown to be working.

**What this closes, and nothing else did.** The lock job asserts its own postcondition *per
manifest, at lock time* (see below). Nothing asserted that the postcondition still **holds**, from a
process that does not depend on the lock job having run. So every one of these left a green wall
until a satellite died days later: `lock-pinned-digests.yml` deleted; its `schedule:` removed; the
`metadata/write` grant revoked; a manifest unlocked by hand; the cron moved past the purge.

### 🚨 The ordering of two clocks IS the protection, and it is now asserted on every pull request

`acr purge` skips locked manifests; the lock job is what makes a pinned manifest locked. If the lock
ever fires **after** the purge, every pin moved since the previous lock faces that purge unprotected
— and *both jobs would still report success about their own work*. So the sweep also asserts, from
the two places the schedules are actually written down:

* `.github/workflows/lock-pinned-digests.yml` exists and carries a `schedule:` — a protection that
  only runs when someone remembers to dispatch it is not a protection;
* some lock occurrence fires **earlier the same night** than every *enabled* purge step's `schedule`
  in `.github/acr-retention/tasks.json`. A `Disabled` task deletes nothing, so only a live clock
  counts.

🚨 **"Some occurrence, the same night" — not "the day's last occurrence is earlier".** A schedule
firing twice (`0 1,23 * * *`) satisfies the protection through its 01:00 run, and rejecting it would
be a gate that is usually wrong, which is a gate nobody reads. Requiring the same *night* is what
keeps a lock moved to 04:00 red: yesterday's 04:00 run does technically precede today's 03:00 purge,
twenty-three hours earlier, and that gap **is** the defect rather than a satisfaction of it.

Neither assertion needs a credential, so both live in `--self-test` and run on **every pull request**
in `dotnet-test.yml`'s workflow-shell lane, rather than only at 05:20. Falsified 2026-09-08 by
editing the shipped files: moving the lock cron to `0 4 * * *` reds with *"no lock fires before
purge task 'purge-old-images' on the same night"*; deleting the workflow reds with *"that workflow IS
the protection"*. Three cases are driven through the same comparison inside the self-test — a lock at
23:00 and a lock that never fires must both come back non-empty, and the twice-daily `0 1,23 * * *`
must come back **empty**, so the false-positive direction is proven too.

### PENDING-LOCK is an exemption, and it is COUNTED

A pin that moved after the last scheduled lock has not yet had an opportunity to be protected, so
calling it a failure would red on the fleet's ordinary working day. It is exempt — and the count is
printed as its own line, because an exemption that can silently grow to cover the whole fleet is how
this arm would become vacuous.

The proxy is deliberately conservative: *when did the declaring workflow file last change*, one REST
call per repository, not *when did this digest change*. A file touched for an unrelated reason
exempts its pins — a **softening**, never a false alarm. And the window where that matters is narrow
at the hour it runs: the sweep is at 05:20 and the lock at 01:00, so a pin has to have moved inside
those 4h20m to be exempted at all.

🚨 **A missing lock schedule does NOT exempt anything.** "The clock is gone" is the worst possible
reason to relax a protection check, so with no schedule to compare against an unlocked pin stays
`UNPROTECTED` — and the missing schedule is its own red besides.

🚨 **It answers existence and protection, not agreement.** Whether the digests one repository pins
name the *same promoted build* is a third question, and two manifests that both exist and are both
locked satisfy this sweep completely while being a half-moved set — see
[Pin Set Consistency](../PinSetConsistency), whose tag arm runs beside this one in the same workflow,
under the same credential.

```
    repositories scanned                  8
    …declaring at least one digest pin    6
    …declaring NO digest pin              2  (Systemorph/MeshWeaver, Systemorph/Memex)
    …whose workflows could not be read    0
    pin DECLARATIONS found                15
    …over how many DISTINCT digests        9
    …declarations that RESOLVE            12
    …declarations that are GONE            3
    …INDETERMINATE (registry not reached)  0
    pin-shaped declarations MALFORMED      0
```

Measured 2026-09-06, mid-release-wave. The three GONE are Education's, and they are `#3438` itself:
`memex-migration@sha256:d81df6cc79ad…`, `memex-portal-ai@sha256:dab2a7b3a7e1…` and
`mw-plugin-test@sha256:df19f10afc1f…`. Note the gap between **15** and **9**: the fleet moves its
pins as one set, so most manifests are pinned from several repositories at once, and the two numbers
must not be collapsed into one — see below.

### The same sweep with the protection arm, measured 2026-09-08T13:5xZ

```
    …declarations that RESOLVE            15
    …declarations that are GONE            0

    of the declarations that RESOLVE —
      …PROTECTED (deleteEnabled false)    11
      …UNPROTECTED — purge can delete      0
      …PENDING-LOCK (exempt: moved since   4
        the last scheduled lock)
      …protection INDETERMINATE            0
      last scheduled lock run             2026-09-08T01:00:00Z  (0 1 * * *)
```

**Zero gone, and every pin the lock has had a turn at is locked.** The four `PENDING-LOCK` are
MeshWeaver.Plugins' current pair (`mw-plugin-test@7202afd4…`, `memex-portal-ai@05080741…`, over four
declaration sites), whose `ci.yml` moved at 10:46Z — nine hours after the 01:00 lock run that saw
the previous pair. Nothing is wrong: tonight's 01:00 lock precedes tomorrow's 03:00 purge, so they
are protected before anything can delete them.

🚨 **But it makes the shape of the residual exposure legible, and it is worth stating plainly:
protection is DERIVED on a daily cadence and the purge does not wait for it.** The two live in
different systems — the lock is a GitHub Actions cron, the purge an ACR timer task — with no
interlock between them. So a pin that lands in the **01:00–03:00 window** meets that night's purge
unprotected, and if the lock job does not run at all (a GitHub Actions outage, a failed OIDC login,
a revoked grant) the purge still runs at 03:00 regardless. Neither is a widened window away from
being fixed; both are closed only by making the pin move and its lock ONE act, which today means a
cross-repo dispatch from each satellite and is not built.

**Until it is, the guard's protection arm is what makes the residual exposure visible instead of
silent** — and a pin that stays unprotected past a lock run is a red naming the repository, the
declaration, the digest and the one `az` command that fixes it now.

**The independent denominator, measured the same day** (a plain `sha256:` grep over every workflow
file in all 34 org repositories, deliberately not the shipped extractor):

| | |
|---|---|
| repositories in the org | 34 |
| …with a `.github/workflows` directory | 16 |
| …declaring ≥ 1 digest pin | 6 (Crm, Education, Manufacturing, Plugins, Reinsurance, SocialMedia) |
| `sha256:`-shaped tokens found | 31 |
| …inside COMMENTS (prose about past incidents) | 11 |
| **pin declaration SITES** | **20** — exactly what the lock job reports |
| …over how many DISTINCT manifests | 9 |
| …that exist | 9 |
| …that are GONE | 0 |
| …that exist and are LOCKED | 7 |
| …that exist and are UNLOCKED | 2 (both Plugins', pinned hours earlier) |

Two things that only a hand count shows. **Eleven of thirty-one `sha256:` tokens are comments** —
several of them narrating *this* incident inside the very files it broke — so any "count the digests"
instrument that does not exclude them over-reports by more than a third. And **20 sites collapse to
15 declarations** in the shipped report, because it dedupes on `(declaration name, digest)` and both
Plugins and Crm write the same `platform-image-digest:` value at two lane calls; 9 distinct
manifests is the number retention actually has to keep alive, and it is the one the two counts agree
on.

### What it is built not to do

Each of these is a way a sweep reports a clean fleet having checked nothing, and each has happened
here in some form:

- **Print the denominator.** "0 unresolved" has two causes — every pin is fine, or nothing was
  extracted — and they are indistinguishable from the verdict alone. So the report states repos
  scanned, repos that pin, repos that do **not**, pins found and pins resolved; and a sweep that
  extracted no pins at all fails, because that proves the extractor broke, not that pinning stopped.

- **Count DECLARATIONS and DIGESTS separately, and label which is which.** The fleet moves its pins
  as one set, so a single manifest is routinely pinned from four repositories at once. "12 pins, 9
  resolve" then invites the reading *nine different images are healthy* when it means nine
  declaration **sites**. Both numbers earn their place and they answer different questions:
  declarations are what a pin move has to edit, distinct digests are how many manifests retention
  actually has to keep alive. The failure summary says it too — *N declarations name M manifests
  that no longer exist* — because N reds from one deleted manifest is one retention event and one
  fix, while N from M is a policy failure of a different size.

- **Refuse placeholder vacuity.** A matcher of the shape `sha256:[0-9a-f]+` reads a *non-hex*
  placeholder such as `sha256:PLACEHOLDER` as "no literal pin present" and passes. Here, a
  declaration whose NAME says digest and whose VALUE is neither a well-formed `sha256:` + 64
  lowercase hex nor a `${{ … }}` forward is a **failure**, never an absence.

  🚨 **That claim was true of one shape and not the other until 2026-09-06** (#3454). The
  `$GITHUB_OUTPUT` pattern required a well-formed digest to match at all, so
  `image-digest=sha256:PLACEHOLDER` matched *nothing* and read as an absence — exempting SocialMedia
  and Crm, the two repositories that write their tester pin that way, from the guard while this page
  said none were exempt. Both shapes now classify through one predicate and the self-test asserts
  both. The way it was found is the transferable part: falsifying a *different* gate against the
  **real** `ci.yml` of the three repositories, rather than against a fixture — see
  [Pin Set Consistency](../PinSetConsistency).

- **Find pins by SHAPE, not by name.** A check keyed on the two common variable names would report
  four of the six pinning repositories clean while verifying none of their pins. The fleet writes a
  pin three ways and all three are extracted:

  | Shape | Written as | Who writes it this way |
  |---|---|---|
  | a workflow `env:` assignment | `MW_TEST_DIGEST: sha256:…` | Education (`MW_PORTAL_DIGEST` / `MW_MIGRATION_DIGEST` / `MW_TEST_DIGEST`), Plugins and Reinsurance (`MW_IMAGE_DIGEST` / `MW_PORTAL_IMAGE_DIGEST`), Manufacturing |
  | a `$GITHUB_OUTPUT` step output | `echo "image-digest=sha256:…"` | SocialMedia, Crm — invisible to any scan of `env:` blocks |
  | an inline image reference | `…azurecr.io/mw-plugin-test@sha256:…` | anywhere a job names the image directly |

- **Know where the extractor stops, and treat a fourth shape as a defect.** Those three are what the
  fleet writes *today*, measured — not a proof that no other shape exists. A digest declared under a
  name that does not say `digest` **and** never written beside its `…azurecr.io/<repo>@` reference
  matches neither rule, and its absence would look exactly like a repository that pins nothing. That
  is why the report prints a **per-repository** pin count and not only the fleet total: a repo whose
  count silently drops from three to two is visible, where a single fleet number would not be. A
  satellite adopting a new pin shape must extend `extract()` and the self-test in the same change —
  a pin the sweep cannot see is spelled identically to a repository that has none, which is the very
  confusion this page exists to remove.

- **Discover the fleet, never commit it.** The repository list comes from the App installation
  (`/installation/repositories`), so a repository added to the org is swept the day the App reaches
  it. `MeshWeaver.Crm` pins the same tester digest as three other repos and is absent from
  `.github/shared-rules.json`'s `repos` array — a check keyed on that list would have swept six and
  silently left a seventh out.

- **A GitHub 404 has two causes and they are opposite verdicts.** Listing `.github/workflows`
  answers 404 both when a repository genuinely has no workflows — a **measured zero** — and when the
  repository is absent, renamed, or unreadable by the token, which means **nobody looked**. Reading
  the second as the first prints *"no digest pin declared"* for a repository that was never swept,
  and the run goes green. So the repository itself is probed before any absence is believed:
  `Systemorph/iac` (real, no workflows) reports a measured zero and passes; a name that does not
  resolve reports UNREADABLE and **fails**. Both arms measured 2026-09-06 — the second only after it
  was caught doing the wrong thing.

- **Never read "cannot reach" as "gone".** `az` exits non-zero both when a manifest is absent and
  when the registry is unreachable or the credential is refused. Only the registry's own
  `manifest unknown` counts as absence; every other answer is INDETERMINATE and fails the run
  naming the `az` output. A control probe runs first, so "every pin in the fleet is gone" can never
  be produced by a broken login.

- **No skip-trapdoor.** `preflight` asserts every input and fails RED naming what to provision; the
  sweep `needs:` it and carries no input-shaped `if:` and no `continue-on-error:`. There is no
  `pull_request` trigger — the live state of a registry is not a property of a pull request, and a
  credentialed PR-reachable job would put both secrets into the Dependabot store's blast radius for
  no gain. The extractor's own falsification runs on every PR instead, as
  `check-pinned-digests.py --self-test` in the workflow-shell lane.

### The sibling check, on the other axis

`Systemorph/Memex`'s `scripts/check-image-pins.py` asserts the same family of invariant for the
**deployment overlays**: every image the chart *derives* from a committed TAG resolves, not just the
one that is written down (Memex#141 — a portal tag that still resolved while its migration twin was
gone left a Job in `ImagePullBackOff` 639 times in 146 minutes, with the portal up the whole time).
That one covers tags in helm overlays; this one covers digests in CI workflows. **It is not wired
into CI** — its header says so — which is a gap on that axis, not this one.

## The retention half — the deletion is stopped, not reported

🚨 **The purge task definitions are CLOUD-ONLY.** Established three ways rather than assumed:
`az acr task show` returns an `EncodedTask` with **`contextPath: null`** — the YAML is stored
base64-encoded inside Azure, with no source repository; an org-wide code search for
`purge-old-images` returns zero hits outside this page; and the deployment repo's tree carries no
bicep, terraform or task definition for them. The shared registry itself is out-of-band —
`deploy/aks/infra/main.bicep` treats `meshweaver.azurecr.io` as an existing `sharedAcrLoginServer`
and only creates a registry when that is empty.

So the reasoning that keeps two incidents from recurring lived in comments that existed in exactly
one place, that nobody diffs, and that any `az acr task update` from any laptop replaces silently
and completely. **`.github/acr-retention/` is now the record**: the decoded task YAML byte for byte,
plus `tasks.json` for the schedule, status and timeout that the YAML does not carry. Nothing deploys
from it — `.github/scripts/acr-retention-tasks.sh` is the only thing that relates it to the live
registry:

```bash
.github/scripts/acr-retention-tasks.sh show     # the live definitions, decoded        (read-only)
.github/scripts/acr-retention-tasks.sh record   # overwrite the record FROM the cloud  (read-only)
.github/scripts/acr-retention-tasks.sh verify   # diff live against the record         (read-only)
.github/scripts/acr-retention-tasks.sh apply    # push the record onto the registry    (MUTATES)
```

`verify` is deliberately **not** wired into CI: it needs `registries/tasks/read`, a strictly larger
grant than the lock job itself holds, and a step that is permanently red for a missing grant is a
step that gets ignored. The half a pull request can actually break — the record — *is* gated, with
no credential at all, by `lock-pinned-digests.py --check-retention-record .` in the workflow-shell
lane.

### What must NOT be the fix

**Raising `--ago` or `--keep` moves the cliff; it does not remove it.** A pin left stable for a
quarter walks off a 30-day window exactly as it walked off a 7-day one, and the failure is then
rarer, later and harder to attribute. The numbers are not the defect — the absence of any relation
between retention and the pin set is.

### One task, two steps — as applied 2026-09-06

There were two purge tasks and only one of them had learned Memex#122. They are now one:
`purge-old-images` (`0 3 * * *`) carries **two steps**, and `purge-old-ci-releases` is **Disabled**,
not deleted, so that re-enabling it is a visible act rather than a rediscovery.

| Step | Filters | Window |
|---|---|---|
| CI images | `mw-plugin-test`, `memex-migration`, `memex-portal-ai`, `memex-portal-next`, `memex-bake` | `--ago 7d --keep 10 --untagged` |
| Production portal | `memex-portal` | `--ago 30d --untagged` |

🚨 **Two steps, not one, and that is the point.** Unioning the filters under the stricter window
would have dragged `memex-portal` from 30 days to `7d --keep 10` with nobody deciding to. The merge
removed the second *task* — the "which of two lists?" question — while preserving each repository's
effective window exactly. Both steps share **one 3600 s task budget**; the per-step `timeout` is a
per-step ceiling, not a second hour.

**Re-verified against the live registry 2026-09-08**, from the task definitions rather than from any
summary of them — `az acr task show` for both, decoding `step.encodedTaskContent`:

| Task | `status` | `schedule` | Steps |
|---|---|---|---|
| `purge-old-images` | **Enabled** | `0 3 * * *` | 5 CI repos at `--ago 7d --keep 10 --untagged`; `memex-portal` at `--ago 30d --untagged` |
| `purge-old-ci-releases` | **Disabled** | `0 4 * * *` | its old single 30-day step, kept as the record of the Memex#122 reasoning |

So the issue's headline finding — *"the second purge task never learned Memex#122"* — is **settled,
and the settlement is that there is no longer a second task to teach.** The divergence itself was
the defect: one repository (`mw-plugin-test`) appeared in the aggressive list and in no other, and
whether a new repository landed under a 7-day or a 30-day window depended on which of two
independently-edited filter lists someone happened to open. `lastModifiedAt` on both is
2026-09-06T21:51Z, ninety seconds apart, which is the edit that collapsed them.

🚨 **The committed record is not the registry, and only one command relates them.**
`.github/acr-retention/*.yaml` is a *record* — nothing deploys from it — so "the record says
Disabled" is not evidence the task is. `acr-retention-tasks.sh verify` is what compares the two, and
it is deliberately not in CI (it needs `registries/tasks/read`, a strictly larger grant than the
lock job holds, and a step that is permanently red for a missing grant is a step that gets ignored).
The reading above was taken by hand, read-only.

🚨 **A correction to the reasoning recorded in that task, measured 2026-09-07.** The task's own
comment justifies the split by saying `memex-portal` "is pinned on the OTHER axis (committed tags in
the deployment overlays, Memex#141)". **It is not — not today.** The overlays pin `memex-portal-ai`,
`memex-migration`, `memex-portal-next` and `hosting-operator`; an org-wide code search for
`meshweaver.azurecr.io/memex-portal:` returns **zero** hits, and the repository's newest tag is
`3.0.0-rc13`. The split is still right, on the plainer ground that tightening a low-churn production
image repository is a separate decision with its own evidence — but the *stated* reason is no longer
true, and a reason that is quietly false is how a future merge talks itself into the union.

### The mechanism: lock the manifest

`acr purge` **skips locked manifests by default** — those with `deleteEnabled` or `writeEnabled` set
to `false` — and deleting them requires the explicit `--include-locked`, which **neither step
passes** (verified against the live definitions, 2026-09-07, and asserted on every pull request
against the committed record). So a lock is a hard protection against this exact configuration, not
a hint.

🚨 **Lock `deleteEnabled` only. Do not also clear `writeEnabled`.** `deleteEnabled: false` is exactly
and only what the purge skips on. `writeEnabled: false` additionally refuses a manifest PUT for that
digest — and the release pipeline PROMOTES by retagging in ACR (`release.yml`:
`docker buildx imagetools create --tag $ACR/$repo:$VERSION $ACR/$repo:$SHORT`), which is a manifest
PUT of an already-present digest under a new tag. A write-lock therefore buys no extra purge
protection and puts a promotion at risk. The five manifests locked by hand on 2026-09-06 carry
**both** flags; two of them (`mw-plugin-test@642f686f…`, `memex-portal-ai@31aab07d…`) are the
`3.0.0-ci.7917` wave, which is the set a `3.0.0` promotion would retag. **This is reasoned from the
mechanism, not measured** — confirming it needs an actual retag against a write-locked manifest, and
the change that wrote this page held read-only access to the registry. It is **worth confirming
before the next promotion and cheap to undo either way**: `az acr repository update …
--write-enabled true` leaves the delete-lock, and therefore the purge protection, untouched.

🚨 **The OCI read-through mirror is not this.** Core #3353/#3495/#3497 put a local content-addressed
cache in front of ACR; its store is a **bounded LRU against `CacheMaxBytes`**, so any entry can be
evicted at any time and a miss simply falls through upstream. It can add availability; it can never
preserve a manifest ACR has deleted. A digest is protected by its lock alone.

## The lock job — protection is derived, not remembered

`.github/workflows/lock-pinned-digests.yml`, running `.github/scripts/lock-pinned-digests.py` at
**01:00 UTC** — two hours before the 03:00 purge — plus `workflow_dispatch`, and report-only on any
push to `main` that touches the script or its two extractor dependencies.

It reads the fleet's live pin set and locks every manifest it names. **A hand-maintained keep-list is
the defect, not the fix**: it is only ever correct on the day it is written, and the five hand locks
of 2026-09-06 were already two-fifths stale a day later (see below).

### The pin set is the UNION of two axes

| Axis | What it is | Read from | Measured 2026-09-07 |
|---|---|---|---|
| 1 | a **digest** pinned in CI | every repository's `.github/workflows` | 20 sites, 6 repositories, 5 distinct manifests |
| 2 | an image **TAG** pinned in a deployment overlay | `values*.y{a}ml` under a `deploy/` or `deployments/` path | 11 sites, 2 repositories, 7 distinct manifests |

A lock set built from axis 1 alone leaves every overlay-pinned manifest unprotected — and
**Memex#122's victim was pinned exactly that way**. Axis 2 reads two shapes, because both occur:
the whole reference on one line (`image: "…/memex-portal-ai:3.0.0-ci.7926"`) and helm's split
convention (`repository:` plus a sibling `tag:`), which is how both whisper charts in the fleet
write theirs. A reader that saw only the first would report those overlays as pinning nothing.

**Axis 1 has no second extractor.** The script *imports* `check-pin-set-consistency.py` and uses its
`extract()`, so there is one definition in this repository of what a digest pin is; the self-test
additionally asserts that it and `check-pinned-digests.py` still **agree** on a shared fixture, so a
future edit that diverges them reddens rather than quietly splitting the fleet's idea of a pin in
two.

That agreement was also measured on the **real fleet**, not only on the fixture — both scripts run
over the same 34 repositories, their digest sets compared at a common truncation (2026-09-07):

```
guard distinct digests   5      lock distinct digests  15
in guard, NOT in lock:   0      ← the lock job misses no axis-1 pin the guard finds
in lock,  NOT in guard: 10      ← 7 axis-2 overlay manifests + 3 currently-locked-but-unpinned
```

Zero divergence on the axis they share, and the whole difference is the axis the guard cannot see
plus the release candidates — the shape the design predicts, measured rather than asserted. Worth
re-running after any change to either extractor: a fixture can be made to agree, a fleet cannot.

**Axis 2's scope is narrow, and the boundary was measured rather than guessed.** Widening it to
every YAML/JSON under `deploy/` was tried and rejected: this repository's
`deploy/aks/operator/test/fixtures/**` carry image references to tags that never existed
(`memex-portal-ai:3.0.0-rc8.ci.5000`), which would red the job nightly over test fixtures, and
`deploy/aks/manifests/observability/log-watcher.yaml` names the floating tag `:latest`, which has no
fixed manifest to protect. Floating tags are reported and never locked, for the same reason.

### What makes it RED

Fail-closed throughout — an unswept repository must never read as a swept one:

- a repository whose workflows or whose git tree could not be read (a GitHub 404 has two causes and
  they are opposite verdicts, so the repository itself is probed before any absence is believed);
- a **truncated** recursive tree listing — an unknown number of paths were never seen, and an
  absence read off a truncated listing is not a measured zero;
- a pin-shaped declaration whose value is not a digest (I4 — placeholder vacuity);
- a digest whose ACR repository cannot be determined (**I5**; for locking this is worse than
  unchecked — there is no manifest to lock);
- an overlay tag that does not resolve, or a pinned digest that is already gone;
- an INDETERMINATE registry answer: `az` failing for any reason other than the registry's own
  `manifest unknown`;
- **ZERO pins on either axis**. Both were non-zero when this was written, so a zero means the
  extractor stopped matching, not that pinning stopped;
- a lock that was requested and did not take.

The report prints the denominator **per axis** — repositories scanned, repositories that pin,
repositories that do not, sites found, sites unclassified — because "0 unprotected" has two causes
and only one of them is good news.

### 🚨 It locks. It does not unlock.

The unlock half is designed, implemented and **shipped disabled**. It is enabled only by a person
setting the repository variable `MW_ACR_RELEASE_UNPINNED` to `true`, and even then it releases
nothing on a run that carries any blocker above.

The asymmetry is the whole safety argument, and it is not theoretical:

> **Unlocking is the only direction that can destroy data, and it is unsafe in exactly the case where
> the extractor is wrong** — because a pin the scan cannot see is spelled identically to a pin that
> is not there. "Nothing pins this any more" and "I failed to read the thing that pins it" produce
> the same answer.

**The first live report-only run proved it.** Of the five manifests locked by hand, the job would
have protected **two** and listed the other **three as release candidates**:

```
RELEASE CANDIDATE  memex-migration@sha256:7f5b6ad2…   tags=['staging-bbcb22f-34015096490-linux-x64']
RELEASE CANDIDATE  memex-portal-ai@sha256:eb9ffcf4…   tags=['staging-bbcb22f-34015096490-linux-x64']
RELEASE CANDIDATE  mw-plugin-test@sha256:c91d6f29…    tags=['staging-bbcb22f-34015096490-linux-x64']
```

That is **not an extractor defect**. Those three are the `staging-bbcb22f-…` set that
MeshWeaver.Education's *pending* pin bump will name — locked **pre-emptively**, to protect a pin that
does not exist yet. No derivation from the live pin set can see a pin nobody has committed. An
enabled release arm would have unlocked the fix for #3438's own victim, on its first run, and
`--ago 7d` would have finished the job. **A pre-emptive lock is therefore a legitimate reason for a
lock this job cannot derive** — which is precisely why a scheduled job must never silently remove
one, and why the release candidates are a *report* a person reads.

Correspondingly, locks are applied **even on a degraded run** — a lock cannot destroy anything, so
protecting what was found beats protecting nothing — while releases are not. Both facts are printed.

### What a real run would do, measured 2026-09-07

```
    AXIS 1 — digest pins in .github/workflows
      repositories scanned                     34
      …declaring at least one digest pin        6
      …declaring NO digest pin                 28
      …whose workflows could not be read        0
      digest pin SITES found                   20
      …UNCLASSIFIED (I5 — not checked)          0

    AXIS 2 — image TAG pins in deployment overlays
      repositories scanned                     34
      …carrying an overlay that pins            2   (Systemorph/Memex, Systemorph/MeshWeaver)
      …whose tree could not be read             0
      overlay files considered                 15
      tag pin SITES found                      11

    UNION — manifests the fleet pins
      distinct manifests wanted                12
      …already protected (deleteEnabled false)  2
      …TO LOCK                                  7
      …that could NOT be protected              3

    REGISTRY
      manifests in the registry              2831
      …currently locked (deleteEnabled false)   5
      …locked and pinned by NOTHING             3
```

**Seven manifests are pinned and unprotected right now**, and four of them sit in repositories the
7-day step sweeps: the `3.0.0-ci.7926` portal/migration pair both live deployments run,
`memex-portal-next:3.0.0-next.43`, and the pearl overlay's `3.0.0-rc9.ci.7601` pair.
`hosting-operator:1.0.0` and `whisper-swiss-german:1.7.4` are in no filter today, so locking them is
protection against a future filter addition rather than against tonight.

**The three that could not be protected are #3438 itself** — MeshWeaver.Education's
`memex-portal-ai@dab2a7b3…`, `memex-migration@d81df6cc…` and `mw-plugin-test@df19f10a…`, already
deleted. The job reds on them, by construction, on the same fact the 05:20 guard reds on. Education's
bump must move all three.

### ✅ The WRITE half works — measured on the first apply run, 2026-09-08

**Locking is a WRITE, and the read-only sweep's credential could not do it.**
`az acr repository update` needs the data action
`Microsoft.ContainerRegistry/registries/repositories/metadata/write`, which is in
**`Container Registry Repository Writer`** (and in Contributor/Owner) and in **neither `AcrPull` nor
`AcrPush`** — measured 2026-09-07: `AcrPush` is `pull/read` + `push/write` and nothing else. The
registry's three service principals held only `AcrPull`, `AcrPush` and `Container Registry Data
Importer and Data Reader` (`metadata/**read**` only), so the prediction was that the lock would be
refused until a grant was added:

```bash
az role assignment create --assignee <the AZURE_CLIENT_ID app's object id> \
  --role 'Container Registry Repository Writer' \
  --scope /subscriptions/<sub>/resourceGroups/meshweaver-shared/providers/Microsoft.ContainerRegistry/registries/meshweaver
```

**That grant landed, and the prediction was confirmed by its removal**: the scheduled run of
2026-09-08T01:14Z ([34176008757](https://github.com/Systemorph/MeshWeaver/actions/runs/34176008757))
is the first `apply` run in the job's history to succeed, and it wrote locks:

```
mode: apply (event: schedule)

    UNION — manifests the fleet pins            REGISTRY
      distinct manifests wanted        18         manifests in the registry     2813
      …already protected               10         …currently locked               26
      …TO LOCK                          8         …locked and pinned by NOTHING   16
      …that could NOT be protected      0

Protected 18 manifest(s) (8 newly locked this run); 0 released.
```

Every previous run was report-only or blocked, so **`0 that could NOT be protected` is the first
measurement of the mechanism end to end** rather than of its plan. The three that #3438 could not
protect were Education's already-deleted trio; Education's bump moved all three, and they now read
`memex-portal-ai@47dc1750…`, `memex-migration@24ea2fc6…`, `mw-plugin-test@4cef7b67…`.

🚨 **`…locked and pinned by NOTHING 16` is the standing debt of a lock-only job**, and it grows every
time a pin moves. The release arm that would clear it stays disabled on purpose — see below.

### 🚨 The lock asserts its own postcondition — an exit code is not a locked manifest

The whole job does exactly one thing: set `deleteEnabled: false` on a manifest. Until 2026-09-07 it
believed **`az`'s exit code** that it had:

```python
def set_delete_enabled(...):
    rc, _, err = az(["acr", "repository", "update", …, "--delete-enabled", "false", "-o", "none"])
    return (rc == 0, …)          # ← the only evidence the job had
```

An exit code is a statement about the **request**, not about the manifest. A write that returns 0
without the attribute moving — a subscription policy, an API version whose `--delete-enabled` is
inert, a proxy that accepts and drops it — would leave the job printing `locked …` per manifest and
`Protected N manifest(s)` at the end, over manifests the 03:00 purge could still delete. That is
[a verification step that cannot fail](../ReadingCiSignals), pointed at the job's own reason for
existing.

So `apply_locks` now **reads the attribute back** and requires it to be `false`. Three outcomes, and
only one of them counts:

| Read-back | Verdict |
|---|---|
| `deleteEnabled == false` | protected — counted, and `locked …` printed |
| `deleteEnabled == true` | **RED**: "the lock WRITE succeeded and the manifest still reads deleteEnabled=true" |
| the registry did not answer | **RED**: "cannot be confirmed locked" — INDETERMINATE is not a confirmation |

Two self-test arms drive it (`--self-test`, which runs on every pull request in `dotnet-test.yml`'s
workflow-shell lane): a fixture whose write exits 0 without taking, and one whose read-back cannot
answer. Both were **falsified** — deleting the read-back turns them red with
`5 lock(s) were COUNTED although none took`, which is what makes them assertions rather than
decoration.

That read-back is what makes the 2026-09-08 apply run above *evidence*. Before it, the job had never
written a lock at all; had it still been believing exit codes, `Protected 18 manifest(s)` would have
been indistinguishable from 18 manifests the purge could still delete.

### Two questions about the lock job, settled by measurement rather than reasoning

🚨 **"Should the scan read each repo's DEFAULT branch rather than every branch?" — it already does,
and changing that would be actively harmful.** `check-pin-set-consistency.py:scan_remote` calls
`repos/{repo}/contents/.github/workflows` with **no `?ref=`**, and the GitHub contents API defaults
to the repository's default branch. Verified by running the extractor against
`Systemorph/MeshWeaver.Education` on 2026-09-07: it returns the three pins on `main`
(`47dc1750…` / `24ea2fc6…` / `4cef7b67…`) and nothing else.

Reading *every* branch would be a defect in both directions: a stale branch's pin would make the job
red forever on a commit nobody will merge, **and** — worse — it would enter the lock set, so a
manifest nothing live pins would be protected on the strength of an abandoned branch. The derivation
is only sound because its input is what the fleet actually runs.

🚨 **"A lock job that fails because a pin is already dead should NAME the stale pin, not just die."
— it already names it**, with repository, file, line, variable and digest:

```
##[error]Systemorph/MeshWeaver.Education ci.yml:45 `MW_PORTAL_DIGEST`:
         memex-portal-ai@sha256:dab2a7b3… does not exist in the registry, so it CANNOT be
         protected. … Move the pin to a manifest that exists — as one set.
```

And the red was **correct, and transient**. Education's `main` sat at `53f0a65df` (2026-09-04T12:12Z)
until `d622e7dc9` merged PR #274 at **2026-09-07T05:52:00Z** — `d622e7dc9`'s first parent is
`53f0a65df`, and that commit's `ci.yml:45/46/64` carries exactly the three purged digests. So at
00:07Z and 01:18Z the default branch really did declare three manifests that no longer exist. Note
the trap this hid behind: those pins' *commit dates* (2026-09-06/07) belong to the branch they were
authored on, not to the moment they reached `main` — reading a commit date as a merge time makes the
job look like it read stale content when it read correctly.

Locks are still applied on a degraded run, so those three never blocked the protection of anything
else.

### The exact commands, by hand

Lock one pinned manifest (idempotent):

```bash
az acr repository update --name meshweaver \
  --image mw-plugin-test@sha256:<digest> --delete-enabled false
```

Release one that nothing pins any more — **a person's decision, never a scheduled job's**:

```bash
az acr repository update --name meshweaver \
  --image mw-plugin-test@sha256:<digest> --delete-enabled true
```

Note what `--ago` measures: a manifest's `lastUpdateTime`, which a re-push refreshes. A digest that
happens to be republished stays young; a digest that is merely *pinned* does not. That asymmetry is
the whole failure in one sentence.

## See also

- [Image Cleanup](../ImageCleanup) — hand-pruning ACR safely, and the live-keeper rule this page extends from deployments to CI pins
- [Module Versioning](../ModuleVersioning) — what a pin is, and why it is supposed to sit still
- [Module Build Architecture](../ModuleBuildArchitecture) — pins move as one set, deliberately and rarely
- [The Continuous Delivery Contract](../ContinuousDeliveryContract) — all-or-nothing publication; verify the image, never the tick
- [A Container Registry in Memex](../ContainerRegistryInMemex) — why the boot image stays on ACR
- [Reading CI Signals](../ReadingCiSignals) — what a green wall does and does not attest to
- [Pin Set Consistency](../PinSetConsistency) — the other question about the same pins, and the extractor this page's lock job imports
