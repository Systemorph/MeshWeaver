---
Name: CiArtifactStorage
Category: Architecture
Description: The measured GitHub Actions storage bill, cross-run and same-run artifacts, and the object-store and named-artifact migration contracts
Icon: CloudArchive
---

# CI artifact storage — the bytes, the bill, and where they belong

**Compute left GitHub; storage has not.** Every private-repo job now runs on our ARC scale sets
(`aks-silos` / `aks-silos-dind`). Runner routing and billing limits are separate controls: a
positive Actions budget does not enforce self-hosted-only execution. What is still billed is
**Actions storage**, and it is the whole of the remaining Actions line.

> Maintainer, 2026-09-17: *"we still incur cost for github actions … please see that it goes to 0"*
> · *"disable for any private repo"* · *"and when free capacity gone => defer to our infra"*.

The object-store changes described below do **not** by themselves remove every GitHub artifact
handoff. [PR artifacts on our infrastructure](../OwnPrArtifacts) documents the named-artifact
adapter, partial-rerun contract, shared-mount proof and remaining rollout gates. Do not treat a
declared store variable, an admission check or an extra durable copy as proof of zero uploads.

🚨 **READ THE BUDGET, NEVER REMEMBER IT — the amount moves within the day, and the whole fleet's CI
hangs off it.** `GET /organizations/Systemorph/settings/billing/budgets` (the classic
`/orgs/{org}/settings/billing/actions` endpoint answers **410 Moved**) returns each budget's
`budget_amount` and `prevent_further_usage`. Measured at **$0** on 2026-09-17 morning and at
**$6,000** the same day at 16:18Z — and the difference between those two numbers is whether any
private repo in the fleet can upload an artifact at all. At $0 the first upload of every
MeshWeaver.Plugins run failed from ~08:29Z with *"Artifact storage quota has been hit"*, every gate
behind it reddened for want of its input, and nothing published
([MeshWeaver.Plugins#2016](https://github.com/Systemorph/MeshWeaver.Plugins/issues/2016)). A
storage-quota refusal is therefore **a budget event, not a capacity event**, and deleting artifacts
does not lift it until GitHub recalculates (every 6–12 h).

## The measurement (2026-09-17, REST only)

Org net Actions storage, 1–17 September: **$66.24**, rising — **$8.70 on 09-16 alone**, i.e. a
~$260/month run rate. The public repo is not in it: `Systemorph/MeshWeaver` averaged **4,353 GB**
and was billed **$0**, because a public repository's Actions storage is free. Only the private
repos pay.

| repository | live artifacts | month-to-date |
|---|--:|--:|
| MeshWeaver.Plugins | **381.5 GB** | $52.98 |
| MeshWeaver.SocialMedia | 12.0 GB | $1.16 |
| MeshWeaver.Reinsurance | 10.2 GB | $4.53 |
| MeshWeaver.Education | 8.4 GB | $4.59 |
| MeshWeaver.Manufacturing | 6.1 GB | $0.47 |
| MeshWeaver.Crm | 6.0 GB | $2.51 |
| MeshWeaver (public) | 4,353 GB avg | **$0** |

🚨 **Actions CACHE is not billed.** Plugins holds 11.87 GB of cache and SocialMedia 11.38 GB against
a 10 GB per-repo allowance, but the allowance is enforced by **LRU eviction, not by an invoice** —
SocialMedia's 11.38 GB of cache sits beside a *9 GB* average storage charge, which is arithmetic
only if cache is outside it. Cache is a capacity question (an idle entry pushes a live one out), not
a cost one.

## 🚨 You pay for retention PLUS a deletion lag of 2–6 days

The artifact inventory and the invoice disagreed by 2.4x, and the explanation is not compression and
not logs. **An artifact keeps costing money after it expires, until GitHub actually deletes it**, and
that lag is days.

Integrating every artifact's bytes over 2026-09-16 — alive from `created_at` to `expires_at` — gives
9,044 GB-hours for Plugins against 21,904 billed. Re-integrating with a deletion lag added to each
expiry, and fitting the lag per repository, reconciles **every repo to within 1%**:

| repository | best-fit lag | modelled GBh | billed GBh |
|---|--:|--:|--:|
| MeshWeaver.Plugins | 2.8 d | 21,885 | 21,904 |
| MeshWeaver.Reinsurance | 5.0 d | 1,183 | 1,189 |
| MeshWeaver.Crm | 5.8 d | 730 | 729 |
| MeshWeaver.Manufacturing | 4.0 d | 228 | 228 |
| MeshWeaver.Education | 4.8 d | 1,249 | 1,259 |
| MeshWeaver.SocialMedia | 2.2 d | 584 | 573 |

The artifacts API corroborates it directly: **1,070 GB of Plugins artifacts are listed `expired:
true` and have not been deleted**, 506 GB of them expired within the last two days.

Three consequences, and they reorder what is worth doing:

1. **A 1-day artifact is billed for four to seven days.** The scratch families — `platform-refs-*`,
   `workspace-build-*`, `compile-check-refs`, `portal-hosts-bin-*` — are consumed inside a 40-minute
   run and declare the shortest retention GitHub allows, and they are still **52% of the fleet's
   daily storage cost**.
2. **Explicit deletion beats shortening `retention-days`.** `DELETE
   /repos/{o}/{r}/actions/artifacts/{id}` is immediate; expiry is followed by days of billed limbo.
   That is the argument for the fleet cleanup job (Memex `actions-cleanup.yml`), not just for
   smaller retention numbers.
3. **Moving the bytes off GitHub avoids both**, which is what the rest of this page is about.

### 🚨 An EXPIRED artifact is deletable, and the sweeper used to skip every one of them

The lag above is not GitHub's to end on its schedule — it is ours to end with a request. **Measured
2026-09-17T16:22Z:** `DELETE /repos/Systemorph/MeshWeaver.Plugins/actions/artifacts/10455640297`,
on an artifact whose `expired` field already read `true`, answered **204 No Content**, and a `GET`
of the same id then answered **404**. So the 1,070 GB sitting `expired: true` and undeleted is a
pool a sweep can reclaim.

**The size of that pool, read straight off the invoice:** MeshWeaver.Plugins was billed **21,904
GB-hours on 2026-09-16 — an average of 913 GB standing** — against a **381.5 GB** live inventory.
The **531 GB difference is the lag**. It is the majority of what that repository pays for, and it is
not live artifacts at all.

Memex's `scripts/actions-cleanup.py` skipped it, on the premise — written into its own docstring —
that *"expired artifacts hold no storage"*. That premise is the exact opposite of the fit above, and
what it cost was **structural, not marginal**: its live rule selects an artifact that is *live* AND
older than `--artifact-days` (2), and a **1-day-retention** artifact is `expired: true` before it is
two days old. So the four families that are only a handoff between the jobs of one run —
`platform-refs-*`, `workspace-build-*`, `compile-check-refs`, `module-pack-tool-*` — **could never be
selected at all, by construction**, and those are the families the table below prices at 44% of the
fleet's daily cost. The two rules, dry-run over the same repositories on 2026-09-17:

| repository | the live rule alone | live + *expired within 7 days* |
|---|--:|--:|
| MeshWeaver.Manufacturing (40 pages) | 108 artifacts / 1.71 GB | **616 / 9.22 GB** |
| MeshWeaver.Plugins (its newest 1.2 days) | **0** | **194 / 7.97 GiB** |

🚨 **Expiry is the EARLIEST safe moment to delete a handoff — and deleting one before it is NOT
safe.** A download of an expired artifact already fails, so deleting it cannot break a re-run that
would otherwise have worked. Deleting a *live* handoff at the end of its own run — the tempting
"it has been consumed, drop it" — **would**: `POST …/rerun-failed-jobs` re-runs the failed job but
not the succeeded producer behind it, so the consumer comes back looking for bytes nobody
re-uploaded. That is a live shape, not a hypothetical: see the misdirection below.

🚨 **And a budgeted sweep must spend its budget on BYTES.** The invoice is GB-hours; the sweeper's
budget is REQUESTS (`--max-deletes`, because GitHub wants ≥1 s between mutative requests and the
caller is capped at 45 minutes). Measured over MeshWeaver.Plugins' newest 120 artifact pages —
10,800 artifacts, 2026-09-16T11:37Z → 2026-09-17T16:16Z, **163.5 GiB uploaded in 29 hours** — **595
rows (5.5%) carry 102 GiB (62%)**. Deleting in page order spends the budget on 300-byte receipts, so
the sweeper orders candidates by size, descending.

#### What one repository uploads in a day, by family

The live-inventory table further down is a *stock*; this is the *flow* that refills it, and the two
answer different questions. MeshWeaver.Plugins, the same 29-hour window:

| family | GiB | rows | retention | read by |
|---|--:|--:|--:|---|
| `platform-refs-catalog-*` | 34.25 | 164 | 1 d | this run's `pack` legs, only when `/opt/platform` misses |
| `workspace-build-catalog-*` | 33.81 | 143 | 1 d | this run's `pack` legs |
| `compile-check-refs` | 31.11 | 130 | 1 d | this run's compile-check units and lanes |
| `bake-<sha>[-shard-*]` | 11.58 | 542 | 3 d | the fold, in this run — **nothing in this repo** |
| `portal-hosts-bin-*` | 6.69 | 45 | 1 d | this run's `portal-hosts-test` shards |
| `module-pack-tool-catalog-*` | 3.07 | 158 | 1 d | this run's `pack` legs |
| `module-bundle-*` (60 modules) | ~35 | ~5,700 | 7 d | this run's gates **and** a later run's ledger |

### 🚨 A quota refusal surfaces one hop downstream, wearing no quota in its message

When the org's Actions budget refused uploads on 2026-09-17 (08:29Z onward), the *producer* job
failed at `Run actions/upload-artifact@v7` with GitHub's own sentence — and every consumer behind it
failed with:

```
##[error]Unable to download artifact(s): Artifact not found for name: portal-hosts-bin-network-133-…
  Please ensure that your artifact is not expired and the artifact was uploaded using a compatible
  version of toolkit/upload-artifact.
```

Sixteen legs, one run, and not one of those messages names a quota, a budget or the producing job.
The author of a documentation-link PR reads *"artifact not found"* and goes looking in their own
diff. The steward's verdict on the producer was worse — *"step 'Run actions/upload-artifact@v7'
failed and NO named signature matched — this is (or may be) a real red"*. MeshWeaver.Plugins#2017
adds a third class for it (`INFRA`: a **named** condition that is provably not the change under test
and that no retry can clear, reported with its cause and never retried), which is the right shape
and the one to copy into core's steward rather than adding the quota to a list that means *retry*.

### And the declared retention is capped at 7 days anyway

Measured off `expires_at`: `publication-inputs` declares `retention-days: 30` and is created with a
**7**-day expiry; so are `teardown-stragglers` (14) and `test-evidence` (14). The repositories'
"Artifact and log retention" setting is 7 days and silently clamps four declarations in the fleet —
`gate-coredumps` (15) among them, so the SIGSEGV evidence everyone reaches for lives a week, not a
fortnight, whatever the YAML says.

### What each family actually costs, per day, fleet-wide

With the lag included, the model totals **$8.78/day against $8.70 billed**:

| family | $/day | share |
|---|--:|--:|
| `module-bundle-*` | $2.40 | 27.3% |
| `bake-*` | $1.51 | 17.2% |
| `workspace-build-*` | $1.38 | 15.7% |
| `platform-refs-*` | $1.28 | 14.6% |
| `compile-check-refs` | $1.10 | 12.5% |
| `portal-hosts-bin-*` | $0.78 | 8.8% |
| `e2e-*` (Education) | $0.19 | 2.2% |
| `module-pack-tool-*` | $0.13 | 1.4% |
| everything else | $0.03 | 0.3% |

## What the 381.5 GB in Plugins actually is

| bytes | artifact family | retention | who reads it |
|--:|---|--:|---|
| 247.1 GB | `module-bundle-<module>` | 7 d | **both**: this run's gate / compile-check / publish-bake, **and** a LATER run through the build ledger |
| 36.7 GB | `bake-<sha>[-shard-*]` | 3 d | the fold, in this run; `bake-<sha>` by MeshWeaver.Education's e2e jobs. **Opt-out per caller** (`upload-bake`); 3 d is a floor, not a habit — see below |
| 28.2 GB | `platform-refs-<lane>` | 1 d | this run's `pack` legs, only when the `/opt/platform` mount misses |
| 28.2 GB | `workspace-build-<lane>` | 1 d | this run's `pack` legs |
| 24.4 GB | `compile-check-refs` | 1 d | this run's compile-check units and lanes |
| 13.8 GB | `portal-hosts-bin-*` | 1 d | this run's `portal-hosts-test` shards |
| 2.5 GB | `module-pack-tool-<lane>` | 1 d | this run's `pack` legs |
| 0.6 GB | gate logs, receipts, verdicts, change-set | 3–30 d | humans and the callers' ratchets |

Two facts decide the design:

1. **Only ONE family is cross-run.** `module-bundle-*` is downloaded by another run — by the build
   ledger's reuse leg (`gh run download "$ART_RUN" -n "$ART_NAME"`), by
   `node-repo-publication-reuse.py`, and by core's `satellite-compat-image` baseline. Everything
   else is a handoff between the jobs of one run and is only kept for 24 hours because **1 day is
   GitHub's floor**, not because anyone reads it that long.
2. **Most of the 247 GB are DUPLICATES.** A run that the ledger tells to REUSE a bundle downloads
   those bytes and then **re-uploads them under the same name in its own run**, because its own
   consumers read the artifact from *this* run. 754 live copies of one module's bundle were
   measured at once, at 8 MB each.

## What became OPT-OUT — and why it is not "removed outright"

`node-repo-gate.yml` uploads the gate's bake twice — per shard, then folded — *"for publish-bake to
reuse (`bake-run-id`)"*. **That reuse was never built**: `bake-run-id` exists in no workflow, script
or input anywhere in the fleet, and `node-repo-publish-bake.yml` uploads no artifact at all and
bakes its own mount. Two copies of identical bytes (every shard bakes the whole mount, so the fold
is a union of duplicates), held three days — **66.8 GB across the fleet**, and ~100% of the
Reinsurance, Manufacturing, Crm and Education bills.

🚨 **But "read by nothing" was WRONG, and the correction is the lesson.** #4578 deleted both uploads
on a fleet-wide search that found no consumer; the search read **local checkouts**, and
MeshWeaver.Education's was eight commits stale. Its `main` downloads `bake-<sha>` **twice** — in
`e2e-install` and in the four-shard `e2e-mesh`, neither with `continue-on-error`, both feeding the
blocking `mesh-gate` — and it calls the lane at `@main`, so the removal would have reddened its next
run on an artifact that had simply stopped being produced. #4585 restored the uploads behind an
`upload-bake` input **defaulting TRUE**; Reinsurance, SocialMedia, Crm and Manufacturing pass
`false` (verified 2026-09-17 against each repo's REMOTE default-branch workflows), Plugins' opt-out
is MeshWeaver.Plugins#2024, and Education keeps it. That holds ~64.9 GB of the 66.8 and costs
Education nothing.

> 🚨 **Never conclude "nothing reads this" from a working tree.** `git rev-parse HEAD` against
> `gh api repos/<o>/<r>/commits/main --jq .sha` first, or read the file through the API. An absent
> consumer and a stale checkout are byte-identical from here.

> 🚨 **An upload whose reader does not exist is not a contract, it is a bill.** If a cross-run bake
> reuse is ever wanted, it lands *with* its consumer.

### 🚨 A same-run handoff's retention floor is NOT 1 day — it is the QUEUE, twice

The per-shard bake looks like the ideal candidate for GitHub's 1-day minimum: `bake-<sha>-shard-<n>`
has exactly one consumer, `Collect every shard's bake`, and nothing outside `node-repo-gate.yml`
names the sharded form. It was shortened to 1 on 2026-09-17 and **reverted the same hour**, because
"the consumer runs minutes later" is a description of the *happy path*, not a bound.

The fold `needs: [plan, gate]`, so it cannot start until the **last** shard has finished — and
GitHub's usage limit lets a job sit **QUEUED for 24 hours** before it is terminated.
`timeout-minutes` bounds execution and never the wait, so that bound applies **twice**:

```
first shard uploads → last shard queued ≤24 h + runs ≤45 min → fold queued ≤24 h
```

≈ **48.8 h** maximum age at the moment the fold downloads it. **1 day and 2 days are both below
that**; 3 is the smallest whole-day value above it, with ~23 h of margin. Shortening it converts a
gate whose shards all passed into `Artifact not found` in the fold — a red manufactured by the
retention.

**The general rule, and it applies to every `retention-days` on this page:** the floor for a
*same-run* handoff is *(the consumer's worst-case queue wait) + (the producer's worst-case wait and
run)*, not "how long a human thinks the run takes". The bytes are better recovered by the sweeper,
which deletes at **expiry** — the one moment that provably cannot break a consumer that could still
have run.

## The seam: `ci-artifact-store.py`

`.github/scripts/ci-artifact-store.py` gives a lane one place to say *where bytes go*:

```
gha                                      GitHub artifacts — the caller's own upload/download steps
file:<dir>                               a writable share the runner pod mounts (no credential)
azblob:<account>/<container>[/<prefix>]  Azure Blob via the ambient azure/login (--auth-mode login)
```

`resolve` · `put` · `get` · `probe` · `prune`, and a `--self-test` that exercises all of it against a
fake `az` and a real directory. A locator is `<store spec>/<key>#sha256=<hex>`, so a reader that
resolved a *different* store refuses it rather than fetching the wrong bytes, and `get` verifies the
digest the record attests before the bytes are used.

### The degrade rule — the one thing to get right

🚨 **A caller that declares no store gets `gha` and behaves exactly as it did before the file
existed.** That is not a fallback, it is the default: the public repo, a fork PR and any runner
without our infra have no credential and must keep working. `resolve` prints the mode and the reason
into the job summary, so *"it used GitHub artifacts"* is never something you infer from an absence.

🚨 **But a store that is DECLARED and cannot be used is RED.** Every `put`/`get` failure is red,
naming the phase. There is no path where a store is configured, fails, and the lane quietly writes
somewhere else or rebuilds from source — that is the fault-becomes-fact defect
([#2695](https://github.com/Systemorph/MeshWeaver/issues/2695)), and it is how *unchanged ⇒ no
compile* turns into *sometimes compiles, nobody knows*.

### 🚨 The path is not the identity — a `file:` store is per-MOUNT, and mine may not be yours

**`file:/ci-artifacts` names a directory, and the two runner pools mount two different Azure Files
shares there.** `MW_ARTIFACT_STORE` was set to `file:/ci-artifacts` on 2026-09-18 and the shared
module-pack lane stopped completing for every repository that calls it
([#4761](https://github.com/Systemorph/MeshWeaver/issues/4761)). The producer wrote a 1,459,865,600-byte
`workspace-build.tar` and **succeeded**; eight consumers asked for that exact path **13 seconds**
later and got *"is not there — the record names an object this store does not hold"*. Both sides
printed byte-identical `ARTIFACT_STORE` and `STORE_RUN_PREFIX`, the same run, the same attempt.

The cluster manifests say why, in the header of the file that declares them — *"ONE Azure Files
share per runner namespace"*:

| | runner scale set | namespace | `ci-artifacts` PVC |
|---|---|---|---|
| producer (`prepare`, `build-workspace`) | `aks-silos-dind` | `arc-runners-dind` | its own dynamically-provisioned share |
| consumers (`pack`) | `aks-silos` | `arc-runners` | **a different** dynamically-provisioned share |

Two `PersistentVolumeClaim`s with the same *name* in two namespaces are two different claims, and
with `storageClassName: azurefile-csi` and no `volumeName` each one provisions its own share. So the
write genuinely succeeded and the read genuinely found nothing: **the `file:` backend never had a
successful cross-pool precedent at all.** (The green runs cited as proof it worked had
`ARTIFACT_STORE: gha` — a control on the other side of the variable.)

The fleet already owns the cure one volume over. `ci-platform` reaches both namespaces as **two
static `PersistentVolume`s carrying ONE `volumeHandle`**, i.e. one share addressed twice; the
`node-repo-module-pack.yml` `select → prepare` edge is cross-pool by construction
(`MW_RUNNER` → `MW_RUNNER_DOCKER`), so nothing in that lane can hand bytes over until `ci-artifacts`
is wired the same way or the variable goes back to `gha`. That half is a cluster change and is
tracked as **Systemorph/Memex#420**, which states both routes: one share behind two static PVs, or
the move to `azblob:` this page already recommends.

#### The stopgap that is in place, and what lifts it (measured 2026-09-20)

**`MW_ARTIFACT_STORE=gha` is set as a REPOSITORY variable on eight repositories** — `MeshWeaver`,
`.Plugins`, `.Education`, `.Reinsurance`, `.SocialMedia`, `.Manufacturing`, `.Crm`, `.Feedback` —
since 2026-09-19T05:35Z, on the maintainer's instruction, after ~9 h in which every
`MeshWeaver.Plugins` run was red and 20 pull requests could not go green. A repository variable
overrides the organisation one, so each is back on the GitHub-artifact path. It was verified on both
sides of the change: Plugins run `35418370866` attempt 1 (org value) went red at 93 s at the first
`put` with the two-share refusal, attempt 2 (repo override) completed; and the control still holds —
run `35534037490` (2026-09-20T19:58Z) ran the same topology, producer `aks-silos-dind` → consumers
`aks-silos`, on `ARTIFACT_STORE: gha`, every `Module bundle` green.

Three facts about it that are not in the variable's value:

- **The organisation variable is untouched**: `file:/ci-artifacts`, created 2026-09-18T21:04:23Z,
  never modified. Deleting a repository override puts that repository straight back on the two-share
  path, where it is RED at the run's first `put` — not silently broken, but not working either.
- **Its reach is two repositories, not eight.** The organisation variable is `visibility: selected`,
  and the selection is `MeshWeaver.Plugins` and `MeshWeaver.SocialMedia` (measured 2026-09-20 over
  REST). The other six overrides therefore override nothing today; they are there so that widening
  the selection later cannot re-expose a repository without a change *in that repository*, and the
  public repository's is correct for a second reason — it must never be pushed onto private
  infrastructure (the script's header says so).
- **Nothing will go red when the stopgap outlives its cause.** Once `ci-artifacts` is one share the
  organisation value is correct again, and the eight overrides will keep every repository on `gha`
  with nothing to warn — the object store simply never gets used by exactly the repositories whose
  storage bill it exists to remove. A stopgap has to be removed by the same act that closes the
  cause.

So the lift is three steps, in order, and the third is not optional:

1. **Systemorph/Memex#420 closes** — `ci-artifacts` served by one share addressed from both runner
   namespaces (the `ci-platform` shape: two static `PersistentVolume`s over one `volumeHandle`, one
   `claimRef` each; a PVC's `volumeName` is immutable, so the claim that moves is recreated), or the
   move to `azblob:`. The proof that it worked is `resolve` printing **one** `store-id` on both pools
   (`//<account>.file.core.windows.net/<share>`), where today it prints `pvc-ebde47ec-…` on
   `aks-silos-dind` and `pvc-42c3b45b-…` on `aks-silos`.
2. **One run proves the handoff on `file:`** — a run whose `ARTIFACT_STORE` line reads
   `file:/ci-artifacts` and whose `Module bundle` consumers are green. A green run on `gha` proves
   nothing about it; that is the control above, on the other side of the variable.
3. **Delete the eight overrides in the same change set**, one command per repository
   (`gh variable delete MW_ARTIFACT_STORE --repo Systemorph/<repo>`), and only then close
   [#4761](https://github.com/Systemorph/MeshWeaver/issues/4761).

**Why nobody could see it: `put`'s success line could not be wrong.** The byte count and the sha256
both came from the **source** file and `dst` was never stat-ed or read back, so the producer was
green *by construction* and the failure necessarily presented as a consumer problem — the same
family as a gate that cannot fail on its own input. Both halves are now closed:

- **`put` reads the destination back.** After the rename it asserts that `dst` exists, is a regular
  file, has the source's length, re-hashes to the digest, appears in its own directory listing, and
  left no staging file behind. The bytes are `fsync`ed before the rename, because on a network
  filesystem the *server* — and so every other mount — holds them only once the client has flushed.
  Four deliberate breakages of the swap (the destination vanishes, is truncated, holds different
  bytes, or the "rename" was really a copy) are self-test rules, each proved red.
- **A store has an IDENTITY, read from the kernel.** `store_id()` returns the mount source from
  `/proc/self/mountinfo` — for cifs `//<account>.file.core.windows.net/<share>` — which is identical
  for two pods on one share and different for two shares. `resolve` emits it as `store-id`, and the
  lane hands it to every `put`/`get` as `--expect-store-id`, so **a runner standing on a different
  share is RED at the first store operation of the run**, naming the mechanism and both remedies,
  instead of writing a handoff that reads as an absence eight jobs later. An *empty*
  `--expect-store-id` is red too: a check wired up with nothing to check against is a check that
  passes on no evidence.

The identity is derived, never minted — no marker file to lose, nothing to keep in sync, and a
symlinked or trailing-slash spelling of the same directory is the same store (a self-test rule,
because that negative control is what stops the check reddening a correct run).

🚨 **A mount source is only an identity for a SHARED filesystem.** `overlay` is the source of every
container's root filesystem and `tmpfs` of every tmpfs, so comparing sources alone would answer
*"same store"* for two pods that share nothing — the exact false pass the check exists to refuse. If
`/ci-artifacts` were ever a plain directory on the pod's own root (a volume that never mounted, a
spec that lost its `volumeMounts` entry), `reachable()` accepts it, because it exists and is
writable. So an identity outside `SHARED_FSTYPES` (cifs/smb3/nfs/…) carries **this machine's node
name**: two pods can then never agree about a node-local directory, one process always agrees with
itself, and the refusal says *check volumeMounts* rather than *unify the shares* — a different fault
with a different remedy.

🚨 **The named-artifact layer inherits the constraint and is NOT yet protected by it.**
`ci-run-artifacts.py` and the `upload-artifact` / `download-artifact` composites ride on the same
`FileStore`, and their manifest lives *in the store* — so a consumer on the other pool cannot read
it either, and for a pattern or whole-collection download an empty result is a valid success by
design (it mirrors `actions/download-artifact`). Nothing wires those composites to a store yet;
until `ci-artifacts` is one share, **do not hand a `store:` to them across pools**. Their
single-name refusal now at least names which share it is standing on.

**The general form, for the third time on this volume class:** a *publish atomically, then swap*
protocol is only atomic if the swap is a real rename **on that filesystem**, and a *shared* store is
only shared if both ends are on the same one. The portal learnt the first half as `File.Move`
copying on Azure Files ([#2190](https://github.com/Systemorph/MeshWeaver/issues/2190) →
[#4547](https://github.com/Systemorph/MeshWeaver/issues/4547)); this is the second half, in CI.

### How the module bundle splits in two

`node-repo-module-pack.yml` takes an `artifact-store` input. When it names a store:

- the **durable copy** goes to the store, keyed `modules/<repo>/<module>/<build-key>/<file>` — the
  ledger's build key is a content address, so identical bytes are written **once** however many runs
  want them, and `put` probes by sha256 before uploading anything;
- the record gains `bundleStore.locator` beside `bundleArtifact`, **never instead of it**;
- the **GitHub artifact stays**, at **1-day** retention, because a handoff between the jobs of one
  run is all it still is. Its consumers — gate, compile-check, publish-bake — are untouched;
- the reuse leg fetches from the store when the record names one *and* this run resolved the same
  store *and* the object is actually there (`module-build-ledger.py` asks the store, never the
  record's word); otherwise it downloads the artifact exactly as it always has, and past that it
  rebuilds.

### The same-run handoffs go too — `runs/`, keyed by run AND attempt

`platform-refs-<lane>`, `workspace-build-<lane>` and `module-pack-tool-<lane>` exist only to cross a
job boundary inside one run. They declare the shortest retention GitHub allows and are still **30% of
the fleet's storage bill**, because of the deletion lag above. Each has exactly one producer and one
consumer, so each now has two paths: the GitHub artifact when no store is named, the store when one
is — guarded by the same expression, so exactly one runs.

Their key needs no plumbing: `runs/<repo>/<run id>/<attempt>/<name>.tar` is derivable by both ends.
🚨 **The ATTEMPT is part of it**: a re-run that read the previous attempt's handoff would compile
against bytes this attempt did not produce. `ModuleBuildLedgerLaneGuard` holds all three pairs and
the key shape — a producer that lost its store path would send the consumer looking for bytes nobody
wrote, and one that lost its artifact path would break every caller without our infra, and neither
shows up in a green run of the other mode.

The `platform-refs` fetch keeps its fallback semantics exactly: the runner's `/opt/platform` mount
first, then whichever handoff this run used, and a refusal that now names both.

🚨 **One expression, two readers.** The `retention-days:` on the ten upload slots and the
`--retention-days` the ledger record states were two independent literals until 2026-09-17. They now
read one expression, so **a record can never outlive the artifact it names**.

## Where the bytes should live — blob, not a share

| | Azure Blob | Azure Files RWX mount |
|---|---|---|
| cost, 400 GB | ~$7.4/month (hot LRS) | ~$24/month (transaction-optimized) |
| throughput for ~12 GB/h | trivial; same-region, no egress charge | fine, but a standard share caps at 300 MiB/s and is shared |
| pruning | **native server-side lifecycle policy** | a CronJob, like `ci-nuget-cache-prune` |
| credential | a role assignment on ONE container | **none** — ambient to every runner pod |
| blast radius | scoped, revocable, per-repo federated | ambient: any CI pod can read or delete any repo's bundles |
| debugging | `az storage blob download` from a laptop | needs cluster access |
| works on a GitHub-hosted runner | yes | no |

**Recommendation: Azure Blob.** Three times cheaper per GB, a lifecycle rule that cannot be
forgotten because it is server-side, a credential that is scoped and revocable rather than ambient
to every pod, and it is readable from a laptop when something needs explaining. The share's one
advantage — no credential at all — is also its weakness.

The `file:` backend exists anyway, and is the reason the seam is worth having: it is what an Azure
Files mount would use, it needs no credential, and it is the backend the self-test exercises against
a real directory.

### Lifecycle

Two prefixes, two lifetimes, so growth is bounded by a rule rather than a habit:

| prefix | holds | delete after |
|---|---|--:|
| `modules/<repo>/<module>/<key>/` | the cross-run reuse copy | 14 days |
| `runs/<repo>/<run id>/<attempt>/` | a handoff between jobs of one run | 2 days |

## What the migration still needs (maintainer)

Neither of these is a repository change, so neither is in this design's PRs:

1. **A container and a role assignment.** A storage account (or a container on an existing one) plus
   `Storage Blob Data Contributor` for the CI identity, scoped to that container, and a blob
   lifecycle-management policy with the two rules above.
2. **Federated credentials for non-`main` refs.** `github-actions-bake` today federates
   `repo:Systemorph/<repo>:ref:refs/heads/main` only — in both the classic and the immutable-id
   subject forms. Roughly half the module-bundle bytes are produced by `pull_request` runs, which
   hold no federated credential at all, so until a `pull_request` subject is added the store can
   only serve trunk runs. 🚨 Both subject formats, per repo — GitHub presents either, and which one
   varies within the org at the same moment.

Until both land, every caller leaves `artifact-store` unset and the lanes behave exactly as they
did. That is the degrade rule doing its job, and it is why the seam can land first.

3. **Arm the nightly sweeper.** Memex's `actions-cleanup.yml` is fixed, self-tested and still a dry
   run on every scheduled night until the repository variable says otherwise:
   `gh variable set MW_ACTIONS_CLEANUP --body arm --repo Systemorph/Memex`. It is the only lever on
   this page that needs no credential and no container — it deletes what already exists, and it is
   what ends the deletion lag.

## See also

- [ArtifactRetentionInterlock](../ArtifactRetentionInterlock) — the same sentence for the container
  registry: a cleanup whose protection set is stale deletes something still in use.
- [ModuleBuildArchitecture](../ModuleBuildArchitecture) — the lane this seam sits inside.
- [BuildCoordination](../BuildCoordination) — the ledger that decides what is rebuilt at all.
