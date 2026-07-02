---
Name: Image Cleanup
Category: Architecture
Description: How to safely reclaim space by deleting old container images from ACR and pruning the local Docker daemon — without ever deleting an image a live deployment depends on
Icon: Delete
---

# Image Cleanup — ACR & local Docker

Every AKS code update builds a portal (and sometimes migration) image with a unique tag and
**pushes it straight to ACR** (`meshweaver.azurecr.io`). `dotnet publish -t:PublishContainer` talks to
the registry directly — it does **not** create a local Docker image — so dev tags pile up on **ACR**,
not on your machine. A busy week leaves dozens of `featurefix-<sha>` tags in `memex-portal-ai`. This
page is how to clear them out safely.

> 🚨 **The golden rule:** never delete an image that any deployment references. A tag that looks "old"
> by name or date may still be **live** in another namespace. Always build the keeper list from what is
> *actually deployed across every namespace* — never from the tag name or push date.

The repos under `meshweaver.azurecr.io`:

| Repo | What it holds | Cleanup posture |
|---|---|---|
| `memex-portal-ai` | The portal image — one tag per deploy | Where the bloat is; prune aggressively |
| `memex-migration` | The DB-migration image | A few tags; keep the live one + `latest` |
| `memex-portal-ai-base` | The custom runtime **base** image every portal build layers on | **Never delete `latest`** — it breaks every future build |

---

## Step 1 — Build the keeper list (do this FIRST)

List every image referenced by a live Deployment in **all** namespaces on the shared cluster. The
cluster is private — `kubectl` only via `az aks command invoke`.

```bash
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "\
  for ns in memex memex-cloud; do \
    echo \"[\$ns]\"; \
    kubectl -n \$ns get deploy -o jsonpath='{range .items[*]}{.spec.template.spec.containers[0].image}{\"\n\"}{end}'; \
  done"
```

Everything that prints is a **hard keeper**. Example output shape:

- `memex-portal-ai:nicepicker-09149ea0d` (a customer portal)
- `memex-portal-ai:settingsfix-bfdd797ae` (**memex** portal — looks old, is live!)
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
  on a cron, e.g. keep the 10 newest `memex-portal-ai` tags and drop the rest. Encode the keeper
  protection with a `--keep` count and a `--filter` that never matches `latest`.

Automation is good for the untagged/old long tail; the **live keeper rule still stands** — a retention
window must be long enough that no currently-deployed tag ages out, or pin keepers with an exclusion.

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
