---
Name: DatabaseBackups
Category: Architecture
Description: How the MeshWeaver databases are backed up — managed point-in-time restore, geo-redundancy for regional DR, and the runbook to enable it on an existing live server
Icon: CloudArrowUp
---

# Database Backups & Disaster Recovery

All portal instances on the shared AKS cluster (`memex`, `memex-cloud`, `atioz` — see
[Instances.md](/Doc/Architecture/Instances)) store their data in **one private Azure Database for
PostgreSQL Flexible Server** (`memexaks-pg`, swedencentral, PG 16 + pgvector). Backing up that one
server backs up **every database on it**, so the whole platform's data is covered by a single policy.

## What is backed up today

Azure PG **Flexible Server managed backups** are on by default and cannot be turned off:

| Property | Value | Meaning |
|---|---|---|
| Mechanism | Automated snapshots + continuous WAL | Point-in-time restore (PITR) to **any second** in the window |
| Retention | `backupRetentionDays: 14` (range 7–35) | Restore to any moment in the last 14 days |
| Scope | The whole server | Every database (`memex`, and each per-environment DB) at once |
| Storage | Azure-managed backup storage, **billed only above 100 % of provisioned storage** | No storage account to manage |

This covers the everyday case ("someone deleted a space yesterday — restore to just before").

## Geo-redundancy (regional DR) — now on in infra

The gap the managed backup did **not** cover: the backups were **locally-redundant** — a single
region. A regional outage would take the server *and* its backups. The infra now provisions the
server **geo-redundant** so a read-only backup copy lives in the Azure-paired region
(swedencentral → germanywestcentral):

- `deploy/aks/infra/modules/postgres.bicep` → `geoRedundantBackup` param (**default `true`** →
  `backup.geoRedundantBackup: 'Enabled'`).
- `deploy/aks/infra/main.bicep` → `postgresGeoRedundantBackup` param (default `true`), wired through.

Any **newly provisioned** server (a fresh environment, or a rebuild) is geo-redundant automatically.

### 🚨 Enabling geo-redundancy on the EXISTING live server is NOT an in-place flip

`geoRedundantBackup` is **immutable after server creation** on Flexible Server. Re-running the bicep
against the running `memexaks-pg` will **not** turn it on — ARM rejects the change. To make the
*live* server geo-redundant you must create a **new** geo-redundant server from a restore and cut
over. This is a disruptive prod operation — schedule a short maintenance window; it is **not** run
as part of a normal code deploy.

Runbook (paired-region DR enablement for the live server):

```bash
# 1. PITR-restore the live server into a NEW server WITH geo-redundancy enabled.
#    (Restore is the only create path that lets you set a different backup option.)
az postgres flexible-server restore \
  --resource-group memex-aks-rg \
  --name memexaks-pg-geo \
  --source-server memexaks-pg \
  --restore-time "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
# 2. Turn geo-redundant backup on for the NEW server (settable because it's a create/restore).
#    If the restore CLI does not accept --geo-redundant-backup on your version, pass it at restore
#    time; otherwise recreate from a geo-restore. Verify:
az postgres flexible-server show -g memex-aks-rg -n memexaks-pg-geo \
  --query "backup.geoRedundantBackup"           # → "Enabled"
# 3. Re-inject into the same delegated subnet + private DNS zone, re-apply pgvector allowlist
#    (azure.extensions = VECTOR,UUID-OSSP), and re-create/verify each per-env database.
# 4. Cut over: patch the connection string for EVERY portal namespace to the new FQDN, then restart.
#    (memex, memex-cloud, atioz — one at a time; confirm HTTP 200 before the next.)
# 5. Decommission the old server once the cutover is verified and a fresh geo-backup exists.
```

Confirm current live setting before/after any change:

```bash
az postgres flexible-server show -g memex-aks-rg -n memexaks-pg \
  --query "{geo:backup.geoRedundantBackup, retentionDays:backup.backupRetentionDays}"
```

## Restoring (PITR)

```bash
az postgres flexible-server restore \
  --resource-group memex-aks-rg \
  --name memexaks-pg-restored \
  --source-server memexaks-pg \
  --restore-time "2026-07-08T09:00:00Z"
```

Restore always lands in a **new** server; you then re-point the connection string (or copy the one
database you need out). Never restore *over* the live server.

## Not yet configured: long-term / cold archival

Managed PITR caps at **35 days**. There is currently **no** archival retention beyond that. If/when
multi-month or multi-year "cold" retention is needed, the two clean options are:

1. **Azure Backup vault long-term retention (LTR)** for Flexible Server — native, up to 10 years,
   restore is native; vault storage is the cold tier. No dump code.
2. **Scheduled `pg_dump` → Blob (Cool/Archive tier)** — a k8s `CronJob` dumps each DB nightly to a
   storage account with a lifecycle rule to **Archive** (true cold storage, cheapest). Portable;
   also covers any database not on the managed server.

Neither is wired today — track as a follow-up if the retention requirement grows past 35 days.
