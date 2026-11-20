---
Name: Data Retention
Category: Architecture
Description: The configurable retention of what grows without bound in a portal's database — finished activities, node version history and log-entry rows — declared per instance in the deployment record, resolvable per partition (a pricing tier), and applied by a bounded, scheduled sweep that names what it removed.
---

# Data Retention

A portal's database grows on every write: each node update stores a full version, every unit of
work leaves an activity, and every Logs instance action lands its window as rows. Before this
page's sweep existed, only three things were ever removed: notifications
([Notification Retention](../NotificationRetention)), compile activities (`CompileActivityRetention`)
and recycle audits (`ActivationRecycleRetention`). Everything else grew without bound.

**Why it was needed (measured 2026-11-20 on the control instance).** Its Postgres server holds
that one database and grew 256 → 512 → 1024 GB in under two weeks. Instance actions under
`Ops/Actions` are 70–120 KB each, Roll actions reach 69–108 versions, and 170 actions were
modified in one day. Activities, versions and `Hosting/LogEntry` rows all accumulate.

> One action rewritten every few minutes, forever (v2 564), is a separate defect and retention is
> not its fix. Retention bounds history by age; a hot loop stays inside any age window.

## What is pruned

Three classes, each switched and sized on its own:

| class | what is removed | never removed |
|---|---|---|
| **Activities** | `Activity` nodes (`{node}/_Activity/{id}`) whose status is a stamped terminal one (`Succeeded`, `Warning`, `Failed`, `Cancelled`) and which have not been written for `MaxAge`. Removal is recursive, so the activity's `_Log` segments go with it. | any status that is not one of those four (`Running`, the default, or a value this build does not know); a **compile** activity (category `Compilation`, owned by `CompileActivityRetention`); a **failed** activity shown in the inbox (it is someone's open item); content that does not read as an `ActivityLog`; an undated row |
| **Versions** | recorded versions in the version store that are older than `MaxAge` **and** not among the node's newest `KeepLast` | the current version; every version inside the window; the newest version written before the window opened |
| **Log entries** | nodes of `NodeType` (default `Hosting/LogEntry`) not written for `MaxAge` | any other node type, including the `Ops/Logs/…` node a query's rows sit under; an undated row |

**The version rule keeps every state inside the window rebuildable.** Undo reads the version just
before a change (`GetVersionBefore`), and a point-in-time restore reads the newest version at or
before the instant. Keeping the newest version written before the cutoff (the state the node was
in when the window opened) means both still work for every instant and every change inside the
window. The rule is one record, `VersionHistoryPrune`, and every store applies it: the file-system
store calls `VersionHistoryPrune.Select`, and the Postgres store (MeshWeaver.Plugins) states the
same rule in SQL. A redline comparison pinned to a version older than the window shows the text
without a redline once that version is gone.

**Versions are kept at least as long as activities.** While both classes are enabled, the
resolver raises the version window to the activity window, so every retained activity can still
be undone. Switching activity retention off while version retention stays on gives that up for
activities older than the version window.

## Configuration

Declared, not derived (policy `declared-not-derived`): every value comes from configuration, which
the deployment record supplies through `ExtraPortalConfig`; each `:` is `__` in the environment
variable (`Retention__Activities__MaxAge`). An absent or malformed value falls back to the shipped
default, and a configured window below its floor is clamped to the floor, so a typo never widens
what is deleted.

| key | default | floor |
|---|---|---|
| `Retention:Enabled` | `true` | — |
| `Retention:Interval` | `06:00:00` | 10 minutes |
| `Retention:InitialDelay` | `00:15:00` | — |
| `Retention:Activities:Enabled` | `true` | — |
| `Retention:Activities:MaxAge` | `90.00:00:00` | 7 days |
| `Retention:Activities:MaxDeletionsPerRun` | `200` (per partition per pass) | 1 |
| `Retention:Versions:Enabled` | `true` | — |
| `Retention:Versions:MaxAge` | `90.00:00:00` | 7 days |
| `Retention:Versions:KeepLast` | `20` | 1 |
| `Retention:Versions:MaxDeletionsPerRun` | `5000` | 1 |
| `Retention:LogEntries:Enabled` | `true` | — |
| `Retention:LogEntries:MaxAge` | `30.00:00:00` | 1 day |
| `Retention:LogEntries:NodeType` | `Hosting/LogEntry` | — |
| `Retention:LogEntries:MaxDeletionsPerRun` | `1000` | 1 |

The defaults are deliberately conservative; an owner who wants activities kept 30 days sets
`Retention__Activities__MaxAge: "30.00:00:00"` on that instance's record. Because of the floor
above, the version window then also stays at least 30 days.

## Per partition: the rule chain

On a multi-tenant instance a partition's subscription decides its windows (a free plan keeps
history for fewer days than a paid one). Core exposes the seam; the rules live where the data
that decides them lives.

- An **`IDataRetentionRule`** (a mesh singleton any module registers) answers, for one partition,
  a `DataRetentionOverride` naming a replacement for any of the three classes, or nothing.
- The **`DataRetentionResolver`** applies the rules in ascending `Order` (then `Name`, never
  registration order). For each class independently, the first rule that speaks wins; where none
  does, the instance policy applies; where that is not configured, the shipped default.
- The chain is open (policy `open-vocabulary-string-constants`): core ships no rule, and a rule reads
  a declaration (a plan node's retention content), never a switch over plan names.
- 🚨 **A rule that faults or does not answer in 15 s skips its partition for that pass.** It never
  falls through to the instance policy, because the rule may keep the data longer (a paid plan)
  and falling through would delete it early.

**The pricing-tier rule is the Plugins half, stacked after this.** It reads the retention a
`Store/Tier` node at `Admin/Tiers/{id}` declares on its content, and resolves a partition to the
tier of the subscription that owns it. Until it lands, every partition resolves to the instance
policy.

## The sweep

`DataRetentionHostedService` (registered by `AddGraph`) runs `DataRetentionSweep.RunPass` first
after `InitialDelay`, then `Interval` after the previous pass ended, so passes never overlap on one
process.

- **Partitions** come from `namespace:Admin/Partition nodeType:Partition`, read as System. A
  partition whose record says `Versioned: false` gets no history prune.
- **A partition name must be a single path token** (`VersionHistoryPrune.IsPartitionToken`: letters,
  digits and `_ - . @`, no leading `-` or `.`, at most 128 characters). Its name goes into queries
  (`path:{partition} …`) and store walks that run as System, where whitespace, query grammar or a
  traversal name (`.`, `..`) would change what they select. Any other name is skipped with a
  Warning and listed in the pass line; the file-system store refuses it as well.
- **Bounded and paced.** Partitions run one after another, the classes of a partition one after
  another, the deletes of a class one at a time. Each class removes at most its
  `MaxDeletionsPerRun` per partition per pass, so a backlog drains over successive passes.
- **The query bounds; the predicate decides.** Activities and log entries are read as the oldest
  window (`sort:LastModified-asc`, compile and unfinished activities filtered in the query so they
  cannot fill it), then each row is judged by the class's `IsExpired`. A backend that ignored the
  sort would drain slower; it could never delete a row the policy did not call expired.
- **Through the framework.** Nodes are removed with `IMeshService.DeleteNode` (recursive, cache in
  step). History is removed with `IVersionQuery.PruneHistory`, the store-level operation each
  store implements. History rows are not nodes and no cache holds them. Never a raw SQL statement
  against a live portal.
- **As System.** This is infrastructure acting on records nobody owns, with no user on the thread.
- **Failure is per class, per partition.** A failed window read fails that class for the
  partition; a selected row whose delete fails or times out is logged at Warning, counted in the
  partition's `DeleteFailures` and the pass line, and stays for the next pass. A delete answering
  "already gone" is neither. The next class and partition still run.
- **Every replica runs it.** The plan is idempotent (two replicas select the same rows; a row
  already gone is an idempotent delete), so no lease is needed. The cost is the repeated window
  reads, which the long interval keeps small.

### What the log says

One Information line per partition where something was removed:

```
Data retention: pruned Admin — 37 finished activities older than 90.00:00:00 (instance),
  5000 versions older than 90.00:00:00 beyond the newest 20 (instance),
  812 Hosting/LogEntry rows older than 30.00:00:00 (instance)
```

and exactly one Warning outcome line per pass (`PassLedger`), the **what-was-pruned** line:

```
DataRetention: pass outcome — COLLECTED: what was pruned — 37 finished activities,
  5000 recorded versions, 812 log entries, over 41 partitions; 0 selected rows whose delete failed
  (kept for the next pass); skipped (unusable name or policy unresolved): []; failed classes: [];
  version history not prunable by this store: []
```

A store that cannot prune history (one that has not implemented `PruneHistory`) is named in the
last list: its history is untouched, and the line says so rather than reporting a prune.

### Stores

| store | `PruneHistory` |
|---|---|
| `FileSystemVersionStore` (core) | applies `VersionHistoryPrune.Select` over `.versions/{partition}/**`; a snapshot's age is its file's write time |
| `NoOpVersionQuery` (core) | retains nothing: pruned, 0 removed |
| `RoutingVersionQuery` (core) | forwards to the partition's provider |
| Postgres (MeshWeaver.Plugins) | the same rule as one bounded `DELETE` over the partition schema's `mesh_node_history` |
| Cosmos, Snowflake (MeshWeaver.Plugins) | not implemented: reported as not prunable |

**Deleting rows does not shrink Postgres files.** A `DELETE` frees space that later writes reuse,
so the database stops growing; returning the space to the server is a separate maintenance step
(`VACUUM FULL` or a repack) and is not part of this sweep.

## What a first run deletes on an existing instance

With the shipped defaults, the first pass (15 minutes after a replica starts) removes, per
partition and capped per class:

- finished, non-compile, non-inbox-failure activities not written in the last **90 days** (up to 200
  per partition per pass);
- recorded versions older than **90 days**, except each node's newest 20 and the version in force
  90 days ago (up to 5000 per partition per pass, on a store that implements the prune);
- `Hosting/LogEntry` rows not written in the last **30 days** (up to 1000 per partition per pass).

Nothing inside those windows, no running activity, no compile activity and no current version is
touched. Set `Retention__Enabled: "false"` on the record before rolling to opt out entirely.
