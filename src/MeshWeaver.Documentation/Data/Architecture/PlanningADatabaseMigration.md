---
Name: Planning a Database Migration
Category: Architecture
Description: The process every database schema change follows, enforced by code and gates — expand-only migrations, the Db-migration declaration and its rehearsal against a real Postgres, ExpectedDbVersion published with the release, every roll path migrating first or refusing, and old pods serving until new ones are ready.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/><path d="M4 11v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6"/><path d="M9 11h6"/><path d="M12 8v6"/></svg>
---

# Planning a Database Migration

**A schema change is planned before it merges, and the plan is checked by code, not by memory.**
This page is the process. It is policy [`db-migration-planned`](../PolicyNotProse), and it builds on
[`roll-migrates-first`](../PolicyNotProse). The routine for moving `db_version` on one instance is
the [Database Migration Procedure](../DatabaseMigrationProcedure). What an instance cannot take by
itself is on [The Self-Update Schema Wall](../SelfUpdateSchemaWall).

## Why this exists

`DbVersion.Latest` (MeshWeaver.Plugins, `src/Memex.Database.Migration/DbVersion.cs`) went from 57 to
58 with V58 (`V58_ReapplySatelliteAuthorshipColumns`, Plugins #2353). Routed self-update `Roll`s on
the control instance `memex` and on `memex-cloud` then moved the portal image without running the
migration Job. The new pods refused to start on `DbVersionGate` (`db_version=57 < expected 58`). One
pod on ci.9332 restarted 91 times in about 24 hours, the old ReplicaSet kept answering 200, and
every fix was stuck behind the roll that could not complete. The migrate-first `Roll` plan (Plugins
#2219) was already merged. It never reached the control instance, because the Hosting content that
carries it was held behind a seal, so the control instance kept planning its own rolls with the old
code. A governed `Reconcile` broke the loop by hand. Related: #4764, #1122, Plugins #2153, #2367,
#2370.

Four gaps made that possible, and each now has a mechanism:

| Gap | Mechanism | Where |
|---|---|---|
| Nothing forced a migration to be planned | The `Db-migration:` declaration and a rehearsal against a real Postgres | MeshWeaver.Plugins CI |
| No planner could see a schema bump coming | `ExpectedDbVersion` published with every release | core CD, `ReleaseSchemaMarker` |
| An image could move without its migration | Every roll path migrates first or refuses | operator `run.sh` interlock, `hosting-deploy`, the Hosting `Roll` plan, the self-updater |
| The migrate-first planner could be stranded behind a seal | The interlock lives in the operator image, which floats on `main`. Content sync is decided per module, by the module's manifest hash | `deploy/aks/operator`, policy `module-sync-per-manifest-hash` |

## 1. Every migration is EXPAND-ONLY

**The old image must run correctly against the new schema.** A roll keeps the old pods serving
until the new ones are fully ready (see section 5), and the migration runs *before* the image moves.
So for a while the previous build serves traffic against the schema the next build asked for. A
migration that breaks the previous build breaks the portal during every roll, and breaks it again
if the roll is refused or stalls.

| Allowed in one release (expand) | Needs two releases (expand, then contract) |
|---|---|
| `ADD COLUMN` that is nullable or has a constant default | `DROP COLUMN` / `DROP TABLE` / `DROP SCHEMA` |
| `CREATE TABLE`, `CREATE INDEX`, a new function or trigger the old code never calls | `RENAME` of a column, table or schema |
| Backfilling a new column | `ALTER COLUMN … TYPE`, `SET NOT NULL` on a column the old code writes without a value |
| A data repair the old code reads correctly | `DELETE`/`UPDATE` of rows the old code still reads in the old shape |

**The two-release plan for a destructive change:**

1. **Expand (release N).** Add the new shape next to the old one. The code writes both and reads
   the new one, falling back to the old one. The migration is additive.
2. **Contract (release N+1, or later).** Once no running instance can still be on a build older
   than N, remove the old shape. This migration is destructive, and it is safe only because the
   previous image (N) no longer reads what it removes.

A contract migration names its expand release in its declaration (below). Without that reference
the gate rejects it.

## 2. The declaration and the rehearsal (MeshWeaver.Plugins CI)

Every pull request that bumps `DbVersion.Latest` or adds a `Migrations/V##_*.cs` declares its
rollout in its body. `scripts/check-db-migration-declaration.py` enforces this and runs in the
Plugins repo's required policy gates:

```text
Db-migration: V58 — additive; old image compatible; rolls migrate-first
```

- `V<N>` is the new `DbVersion.Latest`. A bump of more than one lists each version (`V57, V58`).
- The kind is `additive`, `expand`, `contract` or `data-repair`.
- The compatibility clause is `old image compatible`, or
  `old image incompatible — <the expand release: #PR or V<M>>`. The second form is only valid for
  `contract`.
- Where it can, the gate reads the migration's own source. If it finds destructive SQL
  (`DROP …`, `RENAME`, `ALTER COLUMN … TYPE`, `SET NOT NULL`, `DELETE FROM`, `TRUNCATE`) under a
  declaration of `additive` or `expand`, it fails and names the file and line.

The declaration is what a reviewer reads. The **rehearsal** is what proves it:
`Memex.Database.Migration.Test` starts a real Postgres, puts the database at
`DbVersion.Latest - 1`, runs the migration the way the Job does (rehearse, then run), asserts that
the database reaches `DbVersion.Latest`, and runs it again to prove it is idempotent. The test is
generic, so every future bump is rehearsed without anyone writing a new test.

> 🚨 **Schema DDL outside a versioned migration is not covered.** `PostgreSqlSchemaInitializer`
> runs on every migration and does not bump `DbVersion.Latest`, so a change there triggers neither
> the declaration nor the version comparison below. The expand-only rule still applies to it, and
> a reviewer has to enforce it.

## 3. `ExpectedDbVersion` on the release marker (#4764 (b3))

The schema a build expects used to be only a constant compiled into that build, so no roll planner
could see a bump before the crash-loop showed it. Every release now publishes the number:

| Where | Shape | Written by |
|---|---|---|
| The artifact shares | `<base>/prebuilt-bundles/_releases/_db/<version>`; the whole content is the integer | `main-cd.yml` (`portal-image` reads `DbVersion.Latest` from the Plugins commit it builds) → `publish-bake-bundles.sh`; `release.yml` copies it to the clean version |
| The fleet registry | `expectedDbVersion` in the config of `plugins/releases:<version>` | `push-bundle-publication.sh` |
| Readers | `ReleaseSchemaMarker.Read` / `.Step` (MeshWeaver.PluginCatalog) | the self-updater and the Updates tab |

It lives in a **subdirectory** of `_releases` on purpose. Every reader of `_releases`, including
portals that are already deployed, enumerates the directory's *files* and reads each body as a
framework identity. A second line in the marker, or a sibling file, would be misread by every image
built before this field existed. **An absent file means UNKNOWN, never zero.** A release published
before the field existed carries none, and a planner then decides the way it did before.

With both numbers published, a roll compares them instead of guessing
(`SelfUpdateVerdict.MayPatchAfter(outcome, step)`):

| Migration outcome | Target keeps the schema | Target moves the schema | Unknown |
|---|---|---|---|
| `Completed` | roll | roll | roll |
| `Failed` / `TimedOut` | refuse | refuse | refuse |
| `Forbidden` (403 on the Job) | **roll**: nothing to establish | refuse | refuse |
| `NotSupported` (no mechanism) | **roll**, not recorded as blind | **REFUSE, naming both numbers** | roll, recorded `UNMIGRATED` |

## 4. Every roll path migrates first, or refuses

These paths were verified against the code on 2026-09-25:

| Path | What moves the image | Migration before the swap |
|---|---|---|
| Operator `Roll` (Hosting `InstanceActionPlan.RollSteps`) | `kubectl set image` | ✅ `hosting-migrate` step, then `set image` (Plugins #2219) |
| Self-update routed `Roll` (`selfupdate-roll-…`, origin `self-update`) | the same planner as `Roll` | ✅ same plan. **And** the operator interlock below, whatever Hosting generation composed the plan |
| Any other plan that emits a portal `set image` (e.g. `RepairRemedy.RollToPinnedImage`) | `kubectl set image` | ✅ operator interlock |
| `Reconcile` / `Provision` re-run / `InstallAddOn` (`hosting-deploy` → `helm upgrade`) | `portal.image` in the upgrade | ✅ `hosting-deploy` runs the target's migration first **when the upgrade moves the image of an installed release**. A keep-image upgrade and a first install need none |
| In-pod self-updater (`SelfPatch`) and the Updates tab's **Apply** | a strategic-merge patch | ✅ `RunMigrationAsync` first; the table in section 3 decides |
| `HelmRelease` (Systemorph/Memex `helm-release.yml`, break-glass) | `helm upgrade` | ⚠️ **not migrate-first**: the chart's migration Job is not a hook, so it runs *alongside* the new pods. It converges, because the new pods crash-loop on `DbVersionGate` until the Job finishes while the old ones serve, but it is a race. Use `Reconcile` instead; tracked for the Memex lane |
| `Restart`, `Reactivate`, `Suspend`, `RotateRegistryKey`, `SetSecrets` | none (same image) | n/a |

**The operator interlock** (`deploy/aks/operator/bin/run.sh`) is the one piece of the roll path that
does not depend on the build being rolled. The operator image is `hosting-operator:main`, pulled
with `Always`. Before it runs any step that moves the portal image (`set image … memex-portal=<ref>`),
it checks that the same plan already ran `hosting-migrate` for that namespace and tag. If not, it
runs `hosting-migrate --namespace <ns> --tag <tag>` itself. That form reads the helm release off the
portal Deployment and moves the release's own migration repository to the tag. If the migration
fails, the run stops before the image moves. A move whose namespace or tag it cannot read is
refused before anything runs. This closes the bootstrap loop from 2026-09-24/25: a control instance
whose Hosting generation still plans image-only rolls of itself now gets them migrated first.

## 5. Old pods serve until new pods are fully ready

The portal Deployment (`deploy/helm/templates/memex-portal/deployment.yaml`) rolls with
`maxSurge: 1`, `maxUnavailable: 0`, readiness on `/ready` and a startup probe on `/health`. A new
pod receives traffic only once it is ready, and no old pod is removed before that.
`PreWarmGateReadinessGuard` pins the strategy. This is also why section 1 is a rule and not a
preference: during every roll the previous image serves against the new schema.

## 6. Sync never waits on the roll it plans

The migrate-first planner was stranded because the control instance's Hosting sources were held
wholesale behind a per-identity seal (`SealedSyncGate`), and only a roll could release them. Two
changes remove that dependency:

- **The operator interlock** (section 4) enforces migrate-first even when the plan comes from stale
  Hosting code.
- **Per-module sync by manifest hash** (policy `module-sync-per-manifest-hash`): every module the
  instance has is synced, and each module decides for itself by the `moduleVersion` hash in its
  `manifest.lock`. An unchanged module is a no-op, a changed one syncs, and no Space is held as a
  whole.

## Checklist for a schema change

1. Write the migration as expand-only. If it cannot be, plan the expand release and the contract
   release separately.
2. Bump `DbVersion.Latest` and register the `V##` (`MigrationRegistry.VerifyComplete` refuses a
   mismatch).
3. Put the `Db-migration:` line in the PR body. Let the rehearsal and the declaration gate go green.
4. After merge, check that the release published its number (`_releases/_db/<version>`) and that
   the next roll's plan shows the migration step, or the interlock's `migrate_interlock=` line,
   before `set image`.
5. Verify on the instance that the Job logged `Database migration completed. Version: N` and that
   the new pods became ready. A green CD run does not tell you either.
