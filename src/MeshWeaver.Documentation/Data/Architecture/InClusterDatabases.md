---
Name: InClusterDatabases
Category: Architecture
Description: Each instance's PostgreSQL as its own Helm release in the cluster — a CloudNativePG Cluster on a dedicated db node pool, primary and standby in two zones, backups to Blob — and why not the chart's bundled Postgres or the shared Flexible Server
Icon: Database
---

# In-cluster databases — one release per instance

**Decision (maintainer, 2026-09-15):** *"the database should run in the cluster and get its own
helm ==> we probably need separate node pool for this."* This page is the platform pattern.
Deployment specifics (which instance runs where, the commands on the Systemorph cluster) live in
the deployment repositories — for the Systemorph estate, Memex `docs/in-cluster-databases.md`.

## Why the status quo does not serve

| Today | What is wrong with it for a client instance |
|---|---|
| **A database on the shared Azure Flexible Server** (`<pg-server>`, one database per instance) | The client's data sits on a server that also holds Systemorph's own databases and every other client's. It cannot move with the instance, cannot be handed over, and its sizing, backups and maintenance window are shared. |
| **The chart's bundled Postgres** (`postgres.enabled` in the portal chart: one `pgvector` pod, one PVC) | It lives **inside the portal release**: a portal `helm uninstall`, an `--atomic` rollback, or a re-render that flips the flag takes the database with it. One pod, no standby, no backup. It is right for self-host and local k3s, and wrong for anything with data someone depends on. |

## The options compared

| | Split the chart's own `memex-postgres` into a separate release | **CloudNativePG** (operator + one `Cluster` per instance) |
|---|---|---|
| Lifecycle apart from the portal | yes | yes |
| Standby in a second zone, failover | no — a StatefulSet of one; replication and promotion would have to be written | **yes** — streaming replication, automated failover, switchover on update |
| Backups | none — would have to be written (CronJob + `pg_dump`, no WAL, no point-in-time) | **WAL archiving + base backups** to Blob through the Barman Cloud plugin, point-in-time recovery |
| pgvector | the `pgvector/pgvector` image | the operand's `standard` flavour carries pgvector |
| Credentials | a password the chart must be given | **generated** in-cluster into `<release>-app`; nothing to store in a vault |
| Cost to us | writing and owning HA, backup and restore | installing and upgrading one operator per cluster |

**Recommendation: CloudNativePG.** The maintainer's shape — two nodes, one per zone, primary and
standby — *is* replication with failover, and the only way the chart's own StatefulSet gets there is
by re-implementing what CloudNativePG already is. A thin per-instance chart over a `Cluster` gives
the separate release, and the operator gives HA, backups and generated credentials.

## The shape

```
cluster  ── db node pool ─────── Standard_E4ds_v5 × 2, zones 1+2, fixed, workload=db:NoSchedule
         ── platform (once) ──── CloudNativePG operator · Barman Cloud plugin · StorageClass memex-db-premiumv2
namespace <id>
         ── release <id>-db ──── memex-db chart → Cluster <id>-db (primary + standby)
                                  → Services <id>-db-rw / -ro / -r, Secret <id>-db-app
         ── release <id> ─────── memex portal chart, database.release: <id>-db
```

- **The chart** is `deploy/helm-db` (`memex-db`), shipped in the hosting-operator image at
  `/opt/hosting/chart-db` beside the portal chart, for the same reason: the operator installs the
  templates it was built with. It renders the `Cluster` (and, with backups on, an `ObjectStore` and a
  daily `ScheduledBackup`). It **refuses** a release with no database name, a storage class left blank,
  or backups switched on without a destination and a workload identity.
- **The node pool** is memory-optimised, one node per zone so a primary and its standby never share a
  failure domain, tainted so nothing else lands there, and fixed-size: the autoscaler must never
  remove the node a primary's zonal disk is attached to. Several instances' databases share it; the
  per-database resources (`resources` in the chart) are sized so they do.
- **Storage** is zonal Premium SSD v2 (`memex-db-premiumv2`, `WaitForFirstConsumer`,
  `reclaimPolicy: Retain`). Zonal disks are why the two instances must be in different zones rather
  than merely on different nodes.
- **The portal release** names the database release with `database.release`. The pods connect to
  `<release>-rw` with the owner's generated credentials, composed into `ConnectionStrings__memex` (and,
  under AdoNet, `__orleans`) in the containers' `env` from the Secret `<release>-app`. `env` outranks
  every `envFrom` source, so a Key Vault class still mapping the key cannot point the pod elsewhere.
  The chart's own Secret carries a credential-free string naming the same host, so the
  `wait-for-postgres` gate is derived exactly as for every other shape (MeshWeaver#4173). The chart
  refuses `database.release` together with `postgres.enabled` or a values `ConnectionStrings__memex`
  — two answers to "which database". Chart invariant 19 asserts the credentials are defined before the
  strings that expand them, and that the gate probes the host those strings name.
- **The record** says `inClusterDatabase: { release, instances, size, storageClass }` on the
  `Hosting/Deployment`. Its presence switches the Provision: *Deploy database release*
  (`hosting-db-release`) replaces *Create database* on the Flexible Server, the vault step no longer
  composes a `db-connection` object, and the values render `database.release` with `MEMEX_HOST =
  <release>-rw`. `hosting-db-release` checks the platform layer first — the CRD, the StorageClass, a
  node labelled `workload=db`, enough zones for the instances — and refuses by name with nothing
  created when one is missing.

## Backups and restore

With `backup.enabled` the `Cluster` archives WAL continuously and takes a daily base backup to
`<destinationPath>/<namespace>` through the Barman Cloud plugin, as the pods' workload identity (no
storage key in the cluster). Recovery is a new `Cluster` bootstrapped from that object store,
optionally to a point in time — CloudNativePG's `bootstrap.recovery`.

🚨 **Not wired yet:** the Hosting plugin's `Backup`, `Restore`, `Suspend` and `Teardown`-with-backup
actions still take a `pg_dump` from a **Flexible Server** and refuse an in-cluster record rather than
dump the wrong host. Their in-cluster form (a CloudNativePG `Backup` object, a recovery bootstrap) is
the follow-up before an instance with data depends on this path.

## Moving an existing instance

1. Create the database release (the Provision's *Deploy database release* step) — an empty `Cluster`.
2. Stop the portal (`replicas: 0`), `pg_dump` the Flexible Server database, `pg_restore` into
   `<release>-rw` as the owner.
3. Switch the record to `inClusterDatabase`, drop `databaseServer`/`databaseHost` and the
   `db-connection` vault mapping, and Reconcile.
4. Keep the Flexible Server database until the instance has run on the new one; then drop it.

## Open decisions

- **The operand image pin** — a full `MM.mm-TS-standard-bookworm` tag or a digest per environment; the
  chart default is the rolling major tag.
- **The backup identity** — one user-assigned identity per instance (federated for its namespace) or
  one per cluster; the chart takes a client id either way.
- **The first backup container** — which storage account, in the client's own subscription.
