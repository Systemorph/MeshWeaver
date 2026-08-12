---
Name: The Continuous Delivery Contract
Category: Architecture
Description: What main-cd.yml guarantees about a published image set — all-or-nothing publication via unselectable staging tags, a promote job whose ordering makes rollback unnecessary, and a 3-hourly reconciler that heals main's HEAD. Plus the standing rule: verify the IMAGE, never the green tick.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7l9-4 9 4v10l-9 4-9-4z"/><path d="M3 7l9 4 9-4"/><path d="M12 11v10"/><path d="M8.5 15.5l3.5 1.6 3.5-1.6"/></svg>
---

A merge to `main` publishes **five** container images to `meshweaver.azurecr.io`, and every
self-updating install rolls itself forward by reading one of them. [Release &
Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) describes what that set is and how installs
consume it. This page is about the *guarantee* — what `main-cd.yml` promises about the set, and the
two properties that make a partial or missing release impossible rather than merely loud.

## The set, and why "partial" is the failure that hurts

| Repository | Architectures | What consumes it |
|---|---|---|
| `memex-portal-ai` | linux/amd64 + arm64 | **the self-updater** — the only repository it lists |
| `memex-migration` | linux/amd64 + arm64 | rolled to the same version as the portal, applies schema |
| `mw-plugin-test` | linux/amd64 + arm64 | the plugins repo's CI (ACR **and** a GHCR mirror) |
| `memex-portal-next` | linux/amd64 (by design) | pinned explicitly by deployments |

`.github/scripts/check-image-set.sh` **is** the definition of that set. It is asked by two jobs —
`gate` on the reconcile path, `verify-images` after a publish — and lives in one file precisely so
those two can never disagree. It asserts the **architectures**, not just tag existence: an image
index that lost a leg still resolves for one architecture, and a swallowed cancellation in
`Microsoft.NET.Build.Containers` is exactly how a leg goes missing while its job reports success.

A partial set is worse than no set: the self-updater sees a new `memex-portal-ai` version and rolls
the portal onto it, while the migration image or the bake certification for that commit does not
exist. That is how a portal increment the bake gate never certified reached production.

## Property 1 — all-or-nothing publication

Each of the five build legs pushes its layers under a per-run **staging tag** and nothing else:

```
staging-<short-sha>-<run_id>
```

The staging tag is **unselectable by construction, not by convention.**
`VersionSelect.PlatformVersionTag` is

```csharp
private static readonly Regex PlatformVersionTag =
    new(@"^\d+\.\d+\.\d+([-+].*)?$", RegexOptions.Compiled);
```

and `PickTarget` filters every candidate tag through it, so a `staging-…` tag can never be selected
by any self-updater on any install, whatever the update policy. That regex is not decorative — it
exists because an all-digit git-sha tag once parsed as version `6943991.0.0`, sorted above every real
`3.x` release, and froze every portal on `ci.122`.

Only after **all five legs succeed** does the `promote` job apply real tags, and promotion is a
manifest-only operation (`docker buildx imagetools create`) — the layers are already in the registry,
so it costs seconds.

**Why staging-then-promote rather than build-to-archive:** the bytes have to reach the registry for a
push to happen at all, so pushing them to a staging tag costs exactly what pushing them to the real
tag costs. The only thing worth withholding is the **tag**, because the tag is the entire consumer
contract.

### Rollback by ordering, not by compensation

Tagging five repositories is not one atomic act, and a compensating delete cannot work anyway — for a
moving pointer like `latest`, untagging *destroys* it rather than reverting it. So `promote` is
**ordered so that a mid-flight failure is unobservable**:

| Phase | What it writes | If it fails here |
|---|---|---|
| **A** | identity tags (`<version>`, `<sha>`) on every repo **except** `memex-portal-ai:<version>` | no consumer-visible release; the residue is inert tags nothing resolves |
| **B** | moving pointers — `main` everywhere, `latest` on `mw-plugin-test`, the GHCR mirror | same: still nothing selectable |
| **C** | **`memex-portal-ai:<version>`** — one manifest PUT, the last thing the pipeline does | a single PUT either happened or did not; there is no half-armed state |

Phase C is the **arming write** and nothing else is, because `SelfUpdateHostedService` lists tags for
`memex-portal-ai` **only**, picks the newest `^\d+\.\d+\.\d+` one, and patches the portal *and*
migration Deployments to it. Everything the roll will need — the matching
`memex-migration:<version>`, the bake image that certified these node types — is already tagged by
phase A. **Do not move that step, and do not add anything after it.**

One deliberate coupling: the GHCR mirror sits in **phase B**, so a GHCR outage *blocks* the release
rather than silently leaving the plugins repo's `latest` stale. `check-image-set.sh` only observes
ACR, so a GHCR miss placed after the arming write would never be reconciled and nothing would notice.

## Property 2 — self-healing, and what it heals *to*

A failed CD run used to be terminal. Nothing re-attempted it, so the commit simply never got an image
and the hole closed only incidentally, when some later PR merged — which publishes the *later*
commit, so the failed commit's set is never completed, only superseded. The state did not heal, it
was papered over.

A 3-hourly `schedule` (plus `workflow_dispatch`) now enters the same job graph through `gate`. It is
a **reconciler, not a retry**: it asks *"does main's current HEAD have a complete image set in
ACR?"* — observed state, answered by the same script `verify-images` uses — and drives a publish only
when the answer is no. It wraps no failing operation and suppresses no failure signal.

Four properties worth knowing before you rely on it:

**It heals HEAD, never the failed commit — and that is soundness, not laziness.** The version tag
comes from the *building* run's number, so re-publishing older code would mint a **higher**
`-ci.<n>` for it. Every install would then roll "forward" onto older code, breaking the monotonic
build-number invariant `VersionSelect` depends on to mean "pick the newest". HEAD is also the
declared desired state, and reconcilers converge on desired state rather than on history.

**It does not cry wolf.** It asks the registry *first* — a complete set means nothing is wrong
whatever the check says — and when the set is incomplete it reads the required check's **status as
well as its conclusion**. Those are not interchangeable: `conclusion` is `null` both while
Build-and-Test is running and when it never ran at all, so a conclusion-only reading would post "main
cannot be published" for a perfectly healthy commit on any tick landing shortly after a merge.
Running, or merged minutes ago with no check yet, means **wait silently**.

**It terminates.** Each tick is one attempt and does not re-trigger itself. Persistent failure is
bounded at **3 attempts per commit**, with the `ci-failure` issue as the ledger — no new state store.
The slot is consumed when an attempt *starts*, so a run that dies without reporting cannot buy
infinite retries; a new HEAD resets the budget naturally (the marker carries the SHA); on exhaustion
it stops, says so **once** (`🛑 Automatic healing STOPPED …`) and labels the issue `cd-unhealed`. A
successful heal comments `✅ Healed <sha>` on the same issue, so the ledger cannot only grow.

**It cannot publish an untested tree.** The `workflow_run` path still requires
`event == 'push' && head_branch == 'main'` — that gate is what stops a **fork's** `pull_request` run
(whose `head_branch` can also be `"main"`) from publishing untrusted code with this repo's secrets,
so it must never be relaxed. The reconcile paths do not read the event payload at all: they resolve
the target as the tip of *this* repo's `main` through the API and re-check `Consolidate test results`
on it. A fork's code can never be the tip of `Systemorph/MeshWeaver`'s `main`, so the safety property
is preserved by a stronger check rather than by a proxy.

## The standing trap — verify the IMAGE, never the tick

CD's `workflow_run` trigger reacts to a **real push**. Two consequences trip people up, and both are
silent:

1. **No Build-and-Test run on the merge commit at all** (a CI incident, a stalled queue). CD reacts
   to that workflow completing; with nothing to react to it sits `SKIPPED`.
2. **`workflow_dispatch` of Build-and-Test can never ship on its own.** `gh workflow run "MeshWeaver
   Build and Test" --ref main` runs, and genuinely tests the merge commit — so main shows a **green
   Build-and-Test**. But its `event` is `workflow_dispatch`, not `push`, so the `workflow_run` gate
   still skips. It is the most convincing possible "it shipped" signal, with no image behind it.

**Neither is terminal now**, because the reconciler reads the *check on the commit* rather than the
event that produced it — so a dispatched Build-and-Test does eventually lead to an image, within one
reconcile tick. To kick CD by hand, use its own door:

```bash
gh workflow run main-cd.yml --ref main     # heals HEAD; cannot publish an untested tree
```

But the rule underneath is unchanged and applies to every "did it deploy?" question:

```bash
# 1. Does the image exist, for the commit you care about?
.github/scripts/check-image-set.sh <short-sha>      # the exact assertion CD itself makes

# 2. What is actually in the registry, newest first?
az acr repository show-tags -n meshweaver --repository memex-portal-ai --orderby time_desc --top 5 -o tsv

# 3. What is the cluster actually running?
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl get deploy -A -o custom-columns=NS:.metadata.namespace,IMAGE:.spec.template.spec.containers[0].image --no-headers | grep memex-portal-ai"
```

Installs then self-update on their 6 h poll (`SelfUpdateOptions.PollInterval`) — comfortably longer
than the reconciler's 3 h cadence, so a healed image is picked up on the very next poll — or
immediately after a `kubectl rollout restart`, since the poll fires on startup via `StartWith(-1L)`.

## Changing the pipeline

Adding a sixth image touches **three** places, and missing any one of them recreates the exact hole
this contract closes:

1. its own build job, pushing **only** the staging tag;
2. the `promote` job — identity tags in phase A, pointers in phase B (never after phase C);
3. `check-image-set.sh` — otherwise nothing ever asserts it shipped.

One known, deliberate wart: `memex-portal-next` hand-writes `3.0.0-ci.<n>` while every .NET leg
computes `3.0.0-rc1.ci.<n>`, so its version tag has never matched its siblings'. Nothing selects it
(the self-updater reads `memex-portal-ai` only; deployments pin portal-next explicitly), and changing
a published tag shape would break whatever is pinned today. It is documented at the line that
produces it rather than silently "fixed". This is also why `check-image-set.sh` identifies the set by
**short SHA** and not by version tag: the SHA is the one identity all five images share.

## See also

- [Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) — the two channels, the update policy node, and how each install applies an update.
- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) — where the version number comes from.
- [Deployment](/Doc/Architecture/Deployment) — the route router (AKS vs Container Apps).
- [Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges) — what the `mw-plugin-test` leg is for.
