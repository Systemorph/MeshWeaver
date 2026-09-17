---
Name: CiArtifactStorage
Category: Architecture
Description: Where CI's big build outputs live — the measured GitHub Actions storage bill, which artifacts are cross-run and which are a handoff, the object-store seam and its degrade rule, and the two grants the migration still needs
Icon: CloudArchive
---

# CI artifact storage — the bytes, the bill, and where they belong

**Compute left GitHub; storage has not.** Every private-repo job now runs on our ARC scale sets
(`aks-silos` / `aks-silos-dind`) and the org's Actions budget is **$0 with
`prevent_further_usage`** — nothing in a private repo can start a GitHub-hosted runner any more.
What is still billed is **Actions storage**, and it is the whole of the remaining Actions line.

> Maintainer, 2026-09-17: *"we still incur cost for github actions … please see that it goes to 0"*
> · *"disable for any private repo"* · *"and when free capacity gone => defer to our infra"*.

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
| 36.7 GB | `bake-<sha>[-shard-*]` | 3 d | **nobody** — removed 2026-09-17 |
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

## What was removed outright

`node-repo-gate.yml` uploaded the gate's bake twice — per shard, then folded — *"for publish-bake to
reuse (`bake-run-id`)"*. **That reuse was never built**: `bake-run-id` exists in no workflow, script
or input anywhere in the fleet, and `node-repo-publish-bake.yml` uploads no artifact at all and
bakes its own mount. Two copies of identical bytes (every shard bakes the whole mount, so the fold
is a union of duplicates), held three days, read by nothing — **66.8 GB across the fleet**, and
~100% of the Reinsurance, Manufacturing, Crm and Education bills.

> 🚨 **An upload whose reader does not exist is not a contract, it is a bill.** If a cross-run bake
> reuse is ever wanted, it lands *with* its consumer.

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

## See also

- [ArtifactRetentionInterlock](../ArtifactRetentionInterlock) — the same sentence for the container
  registry: a cleanup whose protection set is stale deletes something still in use.
- [ModuleBuildArchitecture](../ModuleBuildArchitecture) — the lane this seam sits inside.
- [BuildCoordination](../BuildCoordination) — the ledger that decides what is rebuilt at all.
