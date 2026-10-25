---
Name: Image Cleanup
Category: Architecture
Description: How to safely reclaim space by deleting old container images from ACR and pruning the local Docker daemon — without ever deleting an image a live deployment depends on
Icon: Delete
---

# Image Cleanup — ACR & local Docker

Images pile up on **ACR** (`meshweaver.azurecr.io`), not on your machine. Two sources feed it: every
green merge to `main` publishes a `3.0.0-ci.<n>` version tag (plus a git-sha and per-RID tags) via
`main-cd.yml`, and any manual `dotnet publish -t:PublishContainer` pushes whatever tag you named. That
publish talks to the registry directly — it does **not** create a local Docker image. This page is how
to clear the accumulation out safely.

> 🚨 **The golden rule:** never delete an image that any deployment references. A tag that looks "old"
> by name or date may still be **live** in another namespace. Always build the keeper list from what is
> *actually deployed across every namespace* — never from the tag name or push date.

The repos under `meshweaver.azurecr.io`:

| Repo | What it holds | Cleanup posture |
|---|---|---|
| `memex-portal-ai` | The portal image — one tag per deploy | Where the bloat is; prune aggressively |
| `memex-migration` | The DB-migration image | A few tags; keep the live one + `latest` |
| `memex-portal-next` | **Retired** (policy `portal-next-in-platform-image`) — `/next` ships inside `memex-portal-ai` now | No overlay or record names it any more (Systemorph/Memex#726, `check-no-pins.py` rule 7 keeps it so). Ready to delete — [Retiring `memex-portal-next`](#retiring-memex-portal-next) |
| `mw-plugin-test` | Plugin-CI test image (also published to GHCR) | Keep the newest; not deployment-critical |
| `memex-portal-ai-base` | The custom runtime **base** image every portal build layers on | **Never delete `latest`** — it breaks every future build |

> 🚨 `memex-portal-ai`, `memex-migration` and `mw-plugin-test` form the
> **three-image set** that `.github/scripts/check-image-set.sh` asserts is complete for a given commit, and
> CD's reconciler republishes when it is not. Deleting one leg's tag for a commit makes that commit look
> unpublished to the reconciler. Prune whole versions, never one repo's tag in isolation.

---

## Retiring `memex-portal-next`

The repository and its push credential are an infrastructure change. **Nobody runs these from a
laptop**: it is a registry mutation, so it goes through the control instance's governed registry
lane (the `RegistryRetention` path in `.github/scripts/acr-retention-tasks.sh` is the precedent —
one approval, the registry name typed back) or a reviewed operator run. What is prepared here is the
exact sequence and the checks that make each step safe.

**Preconditions (read-only, all must hold):**

1. Nothing pulls it. `kubectl get deploy,sts,ds,job,cronjob -A -o jsonpath='{..image}' | tr ' ' '\n' | grep memex-portal-next`
   prints nothing, and no namespace still has a `portal-next-deployment`. CronJobs count, suspended
   ones included: a schedule that names the image creates a pulling Job after this check.
2. Nothing names it. In Systemorph/Memex no overlay or record contains `memex-portal-next`
   (`git grep memex-portal-next -- deployments/aks mesh/Deployments` finds comments only), and every
   other deployment repository passes the same grep.
3. Nothing builds it. MeshWeaver.Plugins `portal-next-image.yml` is retired (#3350), so no lane can
   push a new tag behind the deletion.

**Steps (each one a governed run, in this order):**

1. **Narrow the push credential FIRST — do NOT delete it.** A principal that still holds
   `content/write` on the repository recreates it with its next push, and the steps are separate
   runs with a gap between them, so the deletion is durable only once nothing can push there. The
   scope-map token behind MeshWeaver.Plugins' `ACR_PUSH_USERNAME`/`ACR_PUSH_PASSWORD`
   (`plugins-portal-next-push`) is SHARED: `log-watcher-image.yml` pushes with the same secret pair.
   Remove only the `memex-portal-next` actions from its scope map
   (`az acr scope-map update -n <scope-map> -r meshweaver --remove-repository memex-portal-next content/read content/write metadata/read metadata/write`)
   and keep the log-watcher repository and the `dockerhub/node` read entry. Deleting the token would
   turn the log-watcher lane red with a 401 that names neither the token nor the reason.
2. **Release the lock.** `lock-pinned-digests` locks a pinned digest and never unlocks it, so
   `memex-portal-next:3.0.0-next.43` is delete-disabled. Re-enable DELETION only, on the tag the lock
   covers; `writeEnabled` is not this runbook's to change (the lock job owns it):
   `az acr repository update -n meshweaver --image memex-portal-next:3.0.0-next.43 --delete-enabled true`.
   If the tag no longer resolves, read the manifest's full digest first
   (`az acr manifest list-metadata -r meshweaver -n memex-portal-next`) and pass `--image memex-portal-next@<that full digest>`.
3. **Delete the repository.** `az acr repository delete -n meshweaver --repository memex-portal-next --yes`.
4. **Retention record.** After the deletion, drop `--filter 'memex-portal-next:.*'` from
   `.github/acr-retention/purge-old-images.yaml` and `purge-old-ci-releases.yaml` and apply them through
   the governed `RegistryRetention` action — a filter on a deleted repository is harmless, but the record
   should describe the registry that exists.

**Afterwards:** `az acr repository list -n meshweaver | grep memex-portal-next` prints nothing, and
the next `log-watcher-image.yml` run still pushes.

## Step 1 — Build the keeper list (do this FIRST)

List every image referenced by a live Deployment in **all** namespaces on the shared cluster. The
Fleet Console (`/Hosting/Console`) shows the RUNNING version per recorded instance; the cluster
read below is break-glass — it covers namespaces no record describes, which is exactly why a
cleanup must not trust the record list alone ([OperatingFromThePortal](/Doc/Architecture/OperatingFromThePortal)).
The cluster is private — `kubectl` only via `az aks command invoke`.

**Query every namespace, never a hand-written list** — the cluster runs at least three portal
namespaces (`portalNamespaces` in `infra/platform/main.bicep` (Systemorph/Memex) lists them), and a
loop that names only some of them silently omits a live image from the keeper list, which is exactly how
you delete something in use:

```bash
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command "\
  kubectl get deploy,statefulset,job,cronjob -A \
    -o jsonpath='{range .items[*]}{.metadata.namespace}{\"\t\"}{.spec.template.spec.containers[*].image}{\"\n\"}{end}' \
  | sort -u"
```

`-A` covers namespaces added since this page was written. Include Jobs and StatefulSets too — the
migration ships as a **Job**, so a `get deploy`-only sweep misses the `memex-migration` tag entirely.

Everything that prints is a **hard keeper**. Example output shape:

- `memex-portal-ai:nicepicker-09149ea0d` (a customer portal)
- `memex-portal-ai:settingsfix-bfdd797ae` (a working portal — looks old, is live!)
- `memex-migration:settingsfix-bfdd797ae` (both envs' migration)

Add to the keeper list:

- **`memex-portal-ai-base:latest`** — the build base. Deleting it breaks every build.
- **`memex-migration:latest`** — the conventional moving tag.
- **One or two recent rollback tags** per environment (e.g. the deploy immediately before the current
  one), so you can `kubectl set image` back if a new rollout misbehaves.
- **Any image you are about to deploy** (a tag still mid-`docker push` from an in-flight build).

Everything in ACR that is **not** on this list is safe to delete.

---

## Step 2 — List the ACR tags (newest first)

```bash
az acr repository show-tags -n meshweaver --repository memex-portal-ai \
  --orderby time_desc --detail --query "[].{tag:name,updated:lastUpdateTime}" -o tsv
```

> `az` output can carry non-ASCII bytes that crash the Windows console (cp1252). Pipe through
> `tr -cd '\11\12\15\40-\176'` to strip them. The same applies to `az aks command invoke` output.

---

## Step 3 — Delete the old tags

Deleting a tag removes its **manifest**; ACR ref-counts layers, so layers shared with a kept image
survive — you only reclaim what nothing else points at. Deletion is irreversible (no recycle bin),
which is why Step 1 comes first.

Delete one tag:

```bash
az acr repository delete -n meshweaver --image memex-portal-ai:deploy-9a3488ed4 --yes
```

Delete many — keep the list explicit so a keeper can never be swept in by a pattern:

```bash
KEEP="nicepicker-09149ea0d settingsfix-bfdd797ae cmdux-fdaa94971 fixall-f560d20d6"
for tag in $(az acr repository show-tags -n meshweaver --repository memex-portal-ai -o tsv | tr -cd '\11\12\15\40-\176\n'); do
  case " $KEEP " in
    *" $tag "*) echo "keep   $tag" ;;
    *)          echo "delete $tag"; az acr repository delete -n meshweaver --image "memex-portal-ai:$tag" --yes >/dev/null ;;
  esac
done
```

Repeat for `memex-migration` with `KEEP="settingsfix-bfdd797ae latest"`. **Skip `memex-portal-ai-base`
entirely** — its only tag, `latest`, is a keeper.

---

## Optional — automate ongoing hygiene

On a **Premium** ACR you can stop the pile-up at the source instead of hand-pruning:

- **Untagged-manifest retention** — auto-delete manifests that lost their tag after N days:
  ```bash
  az acr config retention update -r meshweaver --status enabled --days 30 --type UntaggedManifests
  ```
- **Scheduled purge task** — `az acr task` running [`acr purge`](https://github.com/Azure/acr-cli)
  on a cron, with a `--filter` per repository and a `--filter` that never matches `latest`.

Automation is good for the untagged/old long tail; the **live keeper rule still stands** — a retention
window must be long enough that no currently-deployed tag ages out, or pin keepers with an exclusion.

### 🚨 "Is the purge running?" — and the two things that answer it wrongly

```bash
az acr task list --registry meshweaver -o table         # ← the TASK's status is the answer
```

**A DISABLED TASK STILL REPORTS AN ENABLED TIMER TRIGGER.** Measured 2026-09-12: both tasks read
`status: Disabled` while `trigger.timerTriggers[0].status` still read `Enabled` at `0 3 * * *`. Read
the task's `status`; the trigger's is about the trigger, and a reader who checks the schedule
concludes a paused purge is live.

**And `.github/acr-retention/` is a RECORD, not the registry.** Nothing deploys from it, so a file
saying `Enabled` is evidence of what someone last wrote down. `acr-retention-tasks.sh verify` is what
relates the two.

**State as this is written: `purge-old-images` is PAUSED** while artifact protection is completed
(Memex#219). The condition for re-enabling is recorded in `.github/acr-retention/tasks.json` under
`pause.reEnableWhen` — re-enabling before it restores the exposure the pause is holding off.

### 🚨 `--keep N` is not "keep the N newest tags"

It is applied **after** `--ago`, over the already-eligible set: *of the tags older than the window,
spare the N newest*. A tag younger than `--ago` is never eligible however many newer builds exist —
and one older than it is spared only until N more tags cross the same line behind it.

So `--keep` is not keeper protection, and on a busy repository it is worth almost nothing. Measured
on `memex-portal-ai`: **193.1 tags/day**, so a `--keep 10` window is consumed in **~1.2 hours**, and
the oldest tag in the repository was **exactly 7 days old** — `--ago 7d` was the entire policy. The
durable protection is a lock, and it has to cover the tag, the manifest **and** the platform
manifests under a multi-arch index:
[ArtifactRetentionInterlock](/Doc/Architecture/ArtifactRetentionInterlock).

---

## Local Docker cleanup

Because the portal/migration images live on ACR (never pulled locally), your local Docker holds only
**reusable infrastructure** images — the Aspire/testcontainers dependencies (`pgvector/pgvector`,
`dpage/pgadmin4`, `mcr.microsoft.com/azure-storage/azurite`, `testcontainers/ryuk`). Deleting those
just forces a re-pull (~2 GB) next `aspire run`/test run, so leave them unless you are truly out of
disk.

What is always safe to reclaim:

```bash
docker container prune -f     # stopped containers (left over from old aspire runs / testcontainers)
docker builder prune -f       # build cache
docker image prune -f         # DANGLING (untagged) images only — never touches tagged infra images
```

Check what is reclaimable first, and how much:

```bash
docker system df              # TYPE / SIZE / RECLAIMABLE per category
```

For a full sweep (stopped containers + unused networks + dangling images + build cache) in one go:

```bash
docker system prune -f        # safe: does NOT remove tagged images that are in use
```

⚠️ **Do not** run `docker system prune -a` (or `docker image prune -a`) unless you intend to drop the
reusable infra images too — `-a` removes every image not attached to a running container, forcing the
multi-GB re-pull. The non-`-a` forms above are the routine cleanup.

---

## See also

- [DeploymentAKS.md](/Doc/Architecture/DeploymentAKS) — how the tags get built + rolled out in the first place
- [Deployment.md](/Doc/Architecture/Deployment) — the deploy-route index
