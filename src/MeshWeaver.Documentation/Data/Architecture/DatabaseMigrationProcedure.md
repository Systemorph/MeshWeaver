---
Name: Database Migration Procedure
Category: Architecture
Description: The routine for moving db_version — who mints the migration Job (helm, and now the self-updater), the one-time grant each install needs, what a stuck rollout looks like from the front door (it doesn't), the exact recovery, and why a migration deadlocks under load. Written from the 2026-09-03 memex + memex-cloud wedge.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/><path d="M4 11v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6"/><path d="m15 15 2 2 4-4"/></svg>
---

# Database Migration Procedure

**The rule:** the schema moves *before* the image that expects it, every time, by the same
mechanism, and a roll that cannot move the schema says so loudly instead of rolling anyway.

## How the schema moves

There is one migration program (`Memex.Database.Migration`, built as `memex-migration:<tag>`
beside `memex-portal-ai:<tag>` from the same commit — the two share `DbVersion.Latest`). It reads
`admin.mesh_nodes.db_version`, applies every `V##_*` above it, writes the new version, then runs the
always-on reconcile (partition access, doc backfill, embeddings, Orleans clustering, searchable
schemas) and exits. It is idempotent: at latest it applies nothing and reconciles.

Two things mint the Job that runs it:

| Path | Job name | When |
|---|---|---|
| `helm upgrade` | `memex-migration-<release revision>` (`deploy/helm/templates/memex-migration/job.yaml`) | Every chart upgrade |
| **Self-update** (`SelfUpdateHostedService` → `IDeploymentUpdater.RunMigrationAsync`) | `memex-migration-su-<tag>` | Every automatic roll, **before** the portal image is patched |

The portal's `DbVersionGate` is the backstop, not the mechanism: it refuses to start a pod whose
build expects a `db_version` the database has not reached. That refusal is correct — and it is
**invisible from the front door**: the crash-looping pod is out of the Service endpoints, the old
ReplicaSet keeps serving, and `https://…/` answers 200 the whole time.

## 🚨 The wedge this procedure exists for (2026-09-03)

Both AKS portals had been rolling images by `kubectl set image` (the self-updater) for days without
a `helm upgrade` — memex since revision 28 (Aug 30), memex-cloud since revision 21 (Aug 30). Plugins
PR #1216 added V55 (the `pg_notify` payload carries `node_type`; `DbVersion.Latest = 55`). The next
self-update rolled `memex-portal-ai` to a build expecting 55; nothing minted a migration Job; the DB
stayed at 54:

```
crit: Memex.Portal.Distributed.DbVersionGate[0]
      DB migration incomplete: admin.mesh_nodes.db_version=54 < expected 55. … Refusing to start.
```

memex: 45 restarts over ~7 h behind a 200. memex-cloud: the same, while its eight *old* pods kept
running pre-#1216 code — the very fan-out storm #1216 fixes (1,917 slow 201-schema UNIONs per
30 min per pod) — so the site was up and unusable at the same time.

`kubernetes.io/change-cause` still read *"roll to 3.0.0-rc8.ci.5083 … 2026-08-23"* at deployment
revision 641: `set image` never updates it. Read `meshweaver.io/self-update-rolled-at` and the
container image instead.

## The routine

**1. Adding a migration (developer).** Drop `V##_*.cs` under `Memex.Database.Migration/Migrations`
and bump `DbVersion.Latest`; `MigrationRegistry.VerifyComplete()` refuses a mismatch at startup. Say
so in the PR title — a reviewer should see "schema" without opening files. Nothing else: the
self-updater runs it.

**2. One-time grant per install (operator).** The self-updater creates Jobs under
`memex-portal-sa`; the chart's `memex-portal/rbac.yaml` grants `batch/jobs create,get,list,delete`.
An install that has not been `helm upgrade`d since that rule landed answers the Job POST with 403 —
the self-updater logs, at Warning, that it is rolling *without* the migration and names this
paragraph. Do the `helm upgrade` once and it stops.

**3. Every automatic roll.** `RunMigrationAsync(tag)` creates `memex-migration-su-<tag>` (same
ConfigMap and Secret as the helm Job, same image tag as the portal it precedes), waits for
`status.succeeded` within `SelfUpdate:MigrationJobTimeout` (30 min), and only then patches the
portal image. `Failed` or timed out ⇒ **the roll is refused** and recorded as
`SelfUpdateOutcome.MigrationFailed` on `Admin/UpdatePolicy`; the schema demonstrably did not move,
so the image must not. `NotSupported` (a host whose `IDeploymentUpdater` predates the seam) or
`Forbidden` (step 2 not done) ⇒ the roll proceeds as it always did, at Warning, with `DbVersionGate`
as the only net.

**4. Verifying.** `helm list -n <ns>` against `kubectl get deploy memex-portal-deployment -n <ns>
-o jsonpath='{.spec.template.spec.containers[0].image}'`: divergence means the schema and the code
are on different clocks. A migration Job's success line is literal:
`Database migration completed. Version: N`. Anything else is not a pass.

## Recovery when a pod is refusing on `db_version`

Do not `helm upgrade` with the values on file — they pin the image the chart last knew (rc8 on both
portals that day), and an upgrade would roll the portal *back*. Mint the Job by hand at the tag the
deployment is already on:

```yaml
apiVersion: batch/v1
kind: Job
metadata: { name: memex-migration-v55-manual, namespace: <ns> }
spec:
  backoffLimit: 6
  ttlSecondsAfterFinished: 3600
  template:
    spec:
      restartPolicy: Never
      containers:
        - name: memex-migration
          image: meshweaver.azurecr.io/memex-migration:<the deployment's tag>
          envFrom:
            - configMapRef: { name: memex-migration-config }
            - secretRef: { name: memex-migration-secrets }
```

`az aks command invoke … --file job.yaml --command "kubectl apply -f job.yaml"`, then wait for
`Database migration completed. Version: N`. The gate is `db_version < expected`, so migrating
forward never breaks the pods still serving on the older build.

## 🚨 Why a migration deadlocks under load — and what to do until the fix ships

The migration runner's schema initialisation drives `mw_auth_mirror_heal_batch` — per partition
schema it `CREATE OR REPLACE`s the access trigger functions, re-installs triggers on `mesh_nodes`,
and re-runs `rebuild_user_effective_permissions()` (an `ACCESS EXCLUSIVE` rename-swap of the
permission table). A live pod meanwhile holds `ACCESS SHARE` on those same tables across a
multi-schema UNION, or a row lock on `access` inside the very trigger being replaced. Opposite
orders ⇒ `40P01 deadlock detected`, and Postgres kills the migration. On memex-cloud it died five
times out of five at eight replicas, *before* it had even read `db_version`; at three replicas it
completed in 11 minutes. Two consequences:

- A migration image can fail under load **even with no new `V##`** — the heal batch runs regardless.
- **Until the heal batch yields instead of waiting** (`lock_timeout` + retry per batch,
  MeshWeaver.Plugins), run a contended migration in a quieter window: pause the rollout, scale the
  portal down (the site stays up on fewer pods, and fewer pods also means fewer fan-out queries), run
  the Job, scale back, resume.

## Related

- [Deployment — AKS](/Doc/Architecture/DeploymentAKS) — the runbook this procedure is the schema half of.
- [Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) — what rolls, when.
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — one schema per partition, which is why every migration step is a loop.
