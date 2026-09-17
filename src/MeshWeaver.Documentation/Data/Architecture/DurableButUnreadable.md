---
Name: Durable But Unreadable
Category: Architecture
Description: A write that is acknowledged, versioned and permanently invisible. Three confirmed live instances across two portals - a plugin partition, a plain user partition, and a system partition on the control instance. How to tell it apart from the two read seams disagreeing, and why a mint-time read-back is the only acknowledgement worth trusting.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 1.7 3.6 3 8 3"/><path d="M20 5v6"/><path d="m16 16 6 6"/><path d="m22 16-6 6"/></svg>
---

There is a failure mode in which a write **succeeds in every observable way except the one that
matters**: the caller gets an acknowledgement, a version row is created with the right content and
`state: Active`, and no reader — not a point read, not a query, not a children listing — will ever
see the node again.

It is the most expensive class of defect the platform has, because every gate on the write path is
green. This page is how to recognise it, how to tell it from its lookalike, and what the three
confirmed live instances have in common.

## The three-seam test

A node has **three** independent things that can be asked about it, and this failure is defined by
them disagreeing in one specific way:

| Seam | What it reads | In this failure |
|---|---|---|
| `search namespace:X scope:descendants` / a children listing | the query index | **absent** |
| `GetMeshNodeStream(path)` — a point read | the live node store | **absent** |
| the version history (`get_versions` / `get_version`) | the durable version store | **present, `Active`, full content** |

Run all three before concluding anything. Two of them agreeing is not enough, in either direction:

- **Index absent + point read present** is a *different* defect — the two read seams disagreeing
  (MeshWeaver#2939 / #2970, Plugins#1053). The node is fine; the index is behind or lossy.
- **Index absent + point read absent** still says nothing until the version store is asked. That is
  the only seam that distinguishes "the write never happened" from "the write happened and is
  unreachable", and those have opposite repairs.

## The three confirmed instances (the first two measured 2026-09-01)

**`AgenticEngineering` on memex.systemorph.com** — a Store-plugin partition, 371 nodes written by the
2026-08-28 install/import.

- `search namespace:AgenticEngineering scope:descendants` → **2** results (`_GitSync`, `_Policy` —
  both satellite-routed).
- `get AgenticEngineering/Introduction` → *Not found*. `get AgenticEngineering/*` → the same 2.
- `get_versions AgenticEngineering/Introduction` → **v1, 2026-08-28T22:40:38Z, `system-security`,
  `Edu/Lesson`**; `get_version … 1` returns the complete node, `state: Active`.
- A probe write made on 2026-08-31 by an interactive user (`AgenticEngineering/WriteLaneProbe`,
  `Markdown`) landed the same way: v1 exists, the node is invisible.

**`sglauser/MeshWeaverInstance/sglauser-local-3` on memex.meshweaver.cloud** — a plain USER
partition, one node written by self-service instance registration.

- `get_versions sglauser/MeshWeaverInstance/sglauser-local-3` → **v1, 2026-08-31T15:44:49Z,
  `system-security`, `MeshWeaverInstance`**.
- `get @sglauser/MeshWeaverInstance/sglauser-local-3` → *Not found*.
- `search namespace:sglauser scope:descendants` → 3 nodes, all Markdown from April; the whole
  `MeshWeaverInstance` subtree is absent. `search nodeType:MeshWeaverInstance` → 22, every one of
  them under `rbuergi/…`.
- The **sibling** write of the same registration — `Admin/_PluginGrant/sglauser-local-3`, three
  seconds later, same `system-security` identity, different partition — **is readable**, v1,
  `Active`.

**`Deployments/pearl-provision-20260914` on memex.systemorph.com** (measured 2026-09-15, re-measured
2026-09-16) — a **system partition on the control instance**, one `Hosting/InstanceAction` created by
an approved operation request. Filed as
[#4513](https://github.com/Systemorph/MeshWeaver/issues/4513), from
[MeshWeaver.Plugins#1922](https://github.com/Systemorph/MeshWeaver.Plugins/issues/1922).

- `get_versions Deployments/pearl-provision-20260914` → **53 rows, v1 … v62, every one
  `system-security`**, name and `nodeType` intact. v62 carried `state: Running`, step 14/18, log
  through 10:45:18Z — so the control plane ran it for half an hour against a current row it kept
  writing.
- `get @Deployments/pearl-provision-20260914` → *Not found*, a day later, read as a **global admin**.
- `search namespace:Deployments scope:children nodeType:Hosting/InstanceAction` → 12 siblings,
  `coverage.partitions: ["deployments"]`, the same call and the same credential. That is the
  negative control: the partition is readable, the query shape works, this one node is not in it.
- The replica logged `MeshNodeContentDegradedException … stayed an untyped JsonElement` for this path
  at 10:16:50Z — which is why the degrade was the obvious suspect, and it is still not the cause
  (see below).

## What that pair rules out

The second instance is decisive, and it costs the first instance its stated root cause:

- **It is not plugin-install shadowing.** `sglauser` is an ordinary user partition. No `Store/Plugin`
  root, no `installPaths`, no half-completed install, nothing to shadow the durable layer with.
- **It is not the two-seams-disagree defect.** Both read seams agree; it is the *version store* that
  disagrees with both.
- **It is not identity.** Both a service identity (`system-security`) and an interactive user
  (`rbuergi`) produced it, and the same `system-security` write into `Admin` in the same operation
  landed visibly.
- **It is not "everything written that day".** `rbuergi/MeshWeaverInstance/ci-crm`, written
  2026-08-28, reads back fine.

What the two DO share is the destination: a partition the writer does not live in, whose node rows
the reader cannot find while its version rows exist. Note also that neither partition appears in the
partition index — `autocomplete @/sglauser` → 0 results, while `search namespace:sglauser
scope:descendants` returns its content, and the same split was reported for every partition created
on 2026-08-28 on the other portal.

**And what the third adds.** `Deployments` is not a partition created in that window, not a plugin
partition and not a user's — it is a long-lived system partition on the control instance whose other
rows read fine in the same call. So "a young or unindexed partition" is out as a precondition; the
shared destination property survives only in its weaker form (*the writer does not live there*). The
third instance also carries the **first per-node negative control**: a sibling created the same way,
in the same partition, read in the same query, at the same second. Use that shape when measuring the
next one — it is what separates "this partition is unreadable" from "this row is".

## Why it costs so much more than one node

The write is not merely lost — it is lost **while reporting success**, so everything downstream of
it treats it as done:

- **Instance-key resolution answers 503 forever.** The registry authenticator's read of the instance
  node reaches no verdict, which is the documented `unavailable` path — 503 with *"This is NOT a
  statement about your key or your grant"*. It is correct about the key and the grant and wrong
  about the transience: nothing will change on retry, because the node it needs will never be
  readable. That is MeshWeaver#2915 in full, and it is why a freshly minted key has coin-toss
  integrity: the mint hands out a credential whose index it never read back.
- **An import manifest latches the false green.** A per-file manifest that records success on the
  acknowledgement rather than on a read-back will never retry those files — `force: true` does not
  bypass it. On `AgenticEngineering` that permanently pinned 365 files as imported.
- **A wrapper activity reports `Succeeded`** while its child attempt ends `Warning` with the
  failures in it.

## The rule this yields

> **An acknowledgement is only worth what the layer that reads can confirm.** A write whose success
> is asserted by the write path alone has asserted nothing about whether anyone can read it.

Concretely, for any write that mints a durable identity someone will later resolve — an instance key,
an API token index, an install ledger entry — the mint must **read the node back through the same
seam the resolver will use** before handing out the credential, and fail loudly when that read comes
back empty. A failed registration a user can see and retry is strictly better than a credential that
will 503 forever with a message telling them to retry.

The same applies to a per-file import manifest: record success on a read-back-visible write, or do
not record it at all.

## The core-side mechanism, found and fixed (2026-09-02)

One producer of this signature lived in core, is now fixed, and is pinned by
`AcknowledgedWriteIsReadableTest` (`test/MeshWeaver.Hosting.Test`). It is worth reading even if your
instance turns out to have a different cause, because it shows the shape in full.

`MeshNode.IsDefinitionOnly` is the marker `serveFromPartition` stamps on a static entry to say *the
durable row owns this path; I am only the type definition*. **Six seams answer "which node is served
here", and five honoured it** — `FindServedStaticNode`, `MeshDataSource.WithMeshNodes`,
`MessageHubGrain.TryResolveStaticNode`, the `CreateNode` existing-node probe,
`PartitionWriteGuardValidator` and `StaticNodeQueryProvider`. `FindServedStaticNode`'s own contract
states the invariant outright: keeping them on one resolution is what guarantees *"served static" ⇔
"not persistence-backed"* can never drift apart.

**`StaticNodeStorageAdapter` — the sixth, and the one `PersistenceService` reads — did not.** It
served definition-only entries from `Read`, `Exists`, `ListChildPaths` and `FindBestPrefixMatch`.
That single gap produces the whole signature, because of how the provider chain is ordered:

- `StaticNodePartitionStorageProvider` carries a fixed namespace, so it sorts into
  `PersistenceService`'s **first** provider band — ahead of every wildcard durable backend.
- It is `IsReadOnly`, so it is **absent from the write chain** (`Write` walks writable providers
  only; `Read` walks *all* of them and takes the first non-null).

So on a DB-synced static partition the write was claimed by the durable backend and acknowledged
with the non-null try-then-claim ACCEPT sentinel; `VersionWritingStorageAdapter` chained the
version-history row off that same acknowledgement; and every read from then on returned the
in-memory definition instead of the row. **Read and Write disagreed about which provider owns the
path, and only the write side was ever asked.**

The fix is one predicate — the adapter is a *serve* surface, so it holds only nodes that are
actually served. Definitions stay reachable as definitions through
`StaticNodeProviderExtensions.FindStaticNode`, which enumerates the providers directly and never
goes through the adapter.

> **Residual, deliberately not widened:** the adapter sees one provider's node list, not the
> cross-provider precedence `ResolveStaticNodes` applies. If a *higher*-precedence provider marks a
> path definition-only while a *lower*-precedence one still offers a served node there, the storage
> seam serves the lower one while `FindServedStaticNode` answers "nothing serves this" — the
> MeshWeaver#2908 divergence, one layer down. That collision is reported by name today
> (`DescribeStaticServeCollision`); closing it at this seam needs the adapter to consult the
> resolution rather than a flat list.

## The storage-side mechanism, found and fixed (2026-09-17)

A **second** producer of this exact signature lived in the Postgres backend, is now fixed, and is
pinned by `DurableButUnreadableTest` (`MeshWeaver.Hosting.PostgreSql.Test`, MeshWeaver.Plugins). It
is the first one that reproduces **on demand**, in a test, end to end — acknowledgement, version row,
and three empty read seams — so it is the worked example of the class rather than another instance
of it.

**The write resolved its table from `(path, nodeType)`; every read resolves it from the `path`
alone.** A reader only ever has a path, so `Read`, `ReadMany`, `Exists`, `ListChildPaths`,
`ListDescendantPaths`, `FindBestPrefixMatch` and `DeleteMany` all call
`PartitionDefinition.ResolveTable(path)`. The two WRITE sites — `PostgreSqlStorageAdapter.Write`
(through `BuildUpsertAsync`) and `WriteMany` — passed the node's `NodeType` as well, and when the
path resolved to `mesh_nodes` the adapter fell back to `ResolveTableByNodeType`. So a node whose
**NodeType** maps to a satellite table but whose **PATH** carries no satellite segment — a `Comment`,
`Thread`, `Notification`, `Activity`, `UserActivity`, `Approval` or `TrackedChange` filed at an
ordinary path — was written into the satellite table and looked for in `mesh_nodes` by every single
reader.

Each seam then answers exactly as the three-seam test describes:

| Seam | Why it misses | |
|---|---|---|
| the index | a `namespace:X scope:children` listing reads the table the PATH resolves to | absent |
| the point read | `SELECT … FROM mesh_nodes WHERE path = $1` | absent |
| the version store | `mesh_node_history` is matched on the stored `path` column and is **not** table-routed | **present** |

And the write says it worked, twice over: `PostgreSqlStorageAdapter.Write` returns the saved node,
`PersistenceService` reads any non-null emission as *a provider claimed this*, and
`VersionWritingStorageAdapter` chains the `mesh_node_history` row off that same acknowledgement.

Two fixes, because the mechanism has two halves:

1. **Placement follows the path, because retrieval does.** `ResolveTable(string path)` takes a path
   and nothing else, and the write goes through `ResolveWriteTable(node)`, which is that same
   resolution plus ONE narrow, reasoned exception (below). `ResolveTableByNodeType` keeps its
   documented job — the table for a QUERY that carries a `nodeType` filter and *no path*, where
   there is no path to disagree with.
2. **An unconfirmable write is raised, never acknowledged.** The version-conditional upsert can only
   decline against a row that EXISTS (`#971`), so a refusal whose read-back finds nothing means the
   two halves disagree about where the row lives. That case used to `return stored ?? node` — handing
   back the caller's own node, which is the acknowledgement that turns a routing bug into an
   invisible loss. It now throws `UnconfirmedWriteException`, on the single and the batch path alike.

> **The exception, and why it is one.** An `AccessAssignment` still follows its NODE TYPE into the
> access table even when its path does not resolve there. That placement is a **database-trigger
> requirement**, not a locality choice: `trg_access_changed` lives on the access table and is what
> rebuilds `user_effective_permissions`, so a grant written as a direct child (`Acme/rbuergi_Access`,
> no `_Access` segment) that landed in `mesh_nodes` silently granted nothing — the production defect
> `AccessAssignmentRoutingTests` pins. Grants are resolved by type-filtered query and never by a
> point read of their path, so the placement costs nothing that is used. **The residual is real and
> deliberate: such a grant is not readable BY PATH.** The durable fix is where every current writer
> already puts it — `{owner}/_Access/{id}`, where the two resolutions agree. The exception is derived
> from the partition's own `_Access` mapping rather than a hard-coded table name, and it is the only
> one: measured against the whole Postgres suite (1,138 tests), removing the fallback for every other
> type broke exactly this one case and nothing else.

> **A second residual, on the INDEX half.** Restoring the point read does not by itself put a
> satellite-TYPED node at a main path back into listings: `MeshExtensions.NormalizeSatelliteMainNode`
> re-points such a node's `MainNode` at its namespace on the create/upsert path, and `is:main` is
> SQL `n.main_node = n.path`. That is deliberate — a node of a satellite type belongs under its
> satellite container, which is where `CreateLayoutArea` files it — but it means the two halves of
> this failure have two different owners, and only the storage half is closed here.

> 🚨 **Why the whole suite was blind to it.** Every satellite test — `SatelliteRoutingExhaustiveTest`,
> `SatelliteNodeTests` — writes to a path that ALREADY contains the satellite segment, where the two
> resolutions agree by construction. The asymmetry lived in the *argument*, so no test that never
> varied the argument could see it. The new cases put the divergence there.

### What it does NOT explain, and how you can tell

**Not the third instance.** `mesh_node_history` has two writers and they stamp differently, which
turns out to be the cheapest discriminator on this page:

- the **trigger** `mesh_node_copy_to_history` is installed on `mesh_nodes` ONLY (satellite tables get
  the `pg_notify` trigger and no history trigger) and copies `changed_by` from `last_modified_by`;
- the app-side `PostgreSqlVersionQuery.WriteVersion` — the one `VersionWritingStorageAdapter` chains
  off the acknowledgement — binds **no `changed_by` at all**.

So **a history row with a non-null `changed_by` proves a committed `mesh_nodes` row of that version**
(the trigger runs inside the write's transaction), and a history row with `changed_by IS NULL` proves
only that the adapter acknowledged the write. Every one of `Deployments/pearl-provision-20260914`'s
53 rows carries `system-security`, so its row **was** in `deployments.mesh_nodes`, at `v62`, with
`main_node` equal to its path and `state: Active` (`get_version … 62`, measured 2026-09-17). The
write path is exonerated by its own evidence: what #4513 has left to explain is what REMOVED or
REPLACED that row, not where the write went. The caveat runs the other way too — a node all of whose
writes were unauthored leaves unstamped history as well, so read the stamp as positive evidence of a
commit, never its absence as proof of none.

**And the newest version row does not describe the current row.** The upsert applies at an EQUAL
version by design (`WHERE target.version <= EXCLUDED.version` — re-persisting an unchanged node is a
legitimate, common shape) while the history trigger is `ON CONFLICT (namespace, id, version) DO
NOTHING`. A write at the same version therefore replaces the row's content, `state` and `main_node`
and leaves the version store holding the OLD snapshot under that number. `get_version <max>` is the
last DISTINCT version, not necessarily what the row held when it vanished — which is why the pearl
measurements above bound the row's state at `v62` and not at the moment it disappeared.

## Finding the ones already out there

Three instances were found one at a time, each by someone noticing a node they expected. The sweep
that does not need anyone to notice is `DurableButUnreadableDetector`
(`MeshWeaver.Hosting.PostgreSql`, MeshWeaver.Plugins): **it compares a partition's node tables
against its version store**, reads them directly as the system, and reports per row.

- **`MisplacedRow`** — a row in a satellite table whose path does not resolve there. Unreachable by
  every reader, whatever put it there. This is the population the fix above stops GROWING and does
  not move: rows written while the nodeType fallback was in force are still where it put them.
- **`AcknowledgedButAbsent`** — the version store holds the path, no table in the partition does, and
  no history row carries an author stamp. Nothing proves the row ever reached `mesh_nodes`.
- **`RemovedAfterCommit`** — the same, but an author-stamped history row exists, so the row WAS
  committed and is gone now. **A deleted node looks exactly like this**, which is why it is a
  separate kind and never a defect on its own: it is the population to reconcile against deletes.
- **`RowBehindItsHistory`** — the current row's `Version` is below the newest version its own history
  records. Readable, so not this failure — reported by the same sweep because it is the other way the
  two disagree, and no query can see it either.

The report carries its **denominators** (satellite tables scanned, history paths with no `mesh_nodes`
row, and whether the partition keeps history at all — an unversioned one has no control to compare
against and says so), because a zero means nothing without them. It is read-only: what to do with a
misplaced row — move it into `mesh_nodes`, or delete it as a duplicate of a node that was rewritten
since — is a decision about content, not a mechanical one.

**Running it is an operational step, from the portal.** The provider exposes it as
`PostgreSqlPartitionStorageProvider.DetectUnreadableRows("<Namespace>")`, so an executable `Code`
node resolves the provider from the mesh's services, subscribes it per partition, and writes the
findings up. Start with the partitions that carry a known instance — `deployments` and
`agenticengineering` on memex.systemorph.com, `sglauser` on memex.meshweaver.cloud — and compare its
`RemovedAfterCommit` set against what was deliberately deleted.

## What the 2026-09-02 re-measurement settled, and what it falsified

Re-measured read-only on memex.systemorph.com. Two hypotheses died on evidence that could have gone
the other way — record them so nobody spends the day again:

| Probe | Answer | What it kills |
|---|---|---|
| `autocomplete @/AgenticEngineering/Introduction` | the node **and six of its children**, correct names and node types | **The write is not lost.** A row no reader can see cannot be returned by autocomplete. The failure is a read seam, not the write lane — which is what the issue title says and what three days were spent on. |
| `get_version AgenticEngineering/WriteLaneProbe 1` | `mainNode == path` — and the node is **invisible** | The `is:main` / `n.main_node = n.path` filter (#2939) is **not** the discriminator here. |
| `get_version AgenticEngineering/_Policy 1` | `mainNode != path` — and the node is **visible** | …and the correlation is exactly *inverted* from that filter, so it cannot be the cause. |
| `search namespace:AgenticPrimer scope:descendants` | full content, same portal, same `_Access` shape (`Public — Viewer` + `Anonymous — Viewer`, nothing else) | It is not the grant shape, not the install date, and not a portal-wide query regression. |

What survives, 5 cases out of 5 including one that could have falsified it: **a row in that
partition passes the read filter iff its `main_node` is the partition root `AgenticEngineering`** —
which is precisely the prefix its two grants project at (`COALESCE(main_node, namespace)` in
`rebuild_user_effective_permissions`) and precisely the column the per-schema access clause folds
the caller's effective permissions against (`n.main_node`, not `n.path`). So the surviving candidate
for *that* instance is the access projection, and its SQL lives in
`MeshWeaver.Plugins/src/MeshWeaver.Hosting.PostgreSql`, not here.

The three hypotheses the issue posed, resolved: **transport (the oversized Orleans frame) — ruled
out** (see the Related link below: a transport refusal is loud, terminal, and leaves no version row,
and the frames post-date the import by three days); **the untyped-content degrade (#2952/#3006) —
ruled out** (a degrade leaves the node *in* the listing with unusable content, and `get_version`
returns fully-typed content here, so the discriminator resolves); **the listing/index path — ruled
in.**

> 🚨 **The degrade is ruled out by the CODE as well, so nobody need re-test it.** It keeps being the
> obvious suspect because the replica does log `MeshNodeContentDegradedException` for the very path
> (it did for the third instance too), but that exception is **constructed as a logger argument and
> never thrown** — its own doc comment says so. Every seam returns the node after logging:
> `ObjectPolymorphicConverter` catches the four deserialization exceptions and returns
> `cleanedElement.Clone()` (`src/MeshWeaver.Messaging.Hub/Serialization/ObjectPolymorphicConverter.cs`),
> and both `MeshNodeStreamCache` seams (`GetStream`, `GetQuery`) return the node. A degrade can make
> content unusable; it cannot make a row absent.
>
> An exception CAN still produce a missing row — what a **read fault** cannot do is produce one
> silently, and that is the property to use. Three conversions turn one into absence:
>
> | Where | When | What is lost | Logged |
> |---|---|---|---|
> | `FindMatchingNodes`, the `try`/`catch` around `persistence.ReadMany(...)` | `ReadMany` throws **synchronously** | the whole exact-path arm (`Observable.Empty`) | `Warning`, with the paths — no teardown special case |
> | `PipelineFaultOrStopped` on the composed sequence | a fault arrives **asynchronously** | only the REMAINDER — the default `ReadMany` is a `Merge` over per-path reads, so rows already emitted stand, and the arm is `Concat`-ed with the scope arm, which keeps emitting | `Warning` with the query — **except** a teardown cancellation, which is `Debug` |
> | `SwallowedReadOrStop`, on any PER-PATH read (the scope walk, `SourceActivity.ReadMain`, `NextLevel.Read`) | one path's read faults | exactly that node, `null` in its place | `Warning` with that path — **except** a teardown cancellation, which it RETHROWS rather than converting, so the walk ends and the line above records it at `Debug` |
>
> So do not reason from the shape of the loss to "not an exception" — a one-path `path:X` read has no
> "rest" to leave behind, and a scope walk drops single nodes by design. **Reason from the log**, and
> mind the one exemption: a *teardown* cancellation (the adapter's I/O pool drained as the mesh goes
> down) is `Debug`, not `Warning`. That exemption cannot explain any instance on this page anyway —
> it is scoped to a process on its way out, and the next process reads the row afresh, whereas these
> rows are absent on every read since. For every OTHER read fault the replica said so at `Warning`,
> once per failing read — though **what** it names differs, so grep for the site and not for the
> node: only `SwallowedReadOrStop` carries the single failing path; the synchronous batch catch
> carries the list of paths it was given, and `PipelineFaultOrStopped` carries the query and
> `basePath`, not a node. For a permanently absent row, a line from any of the three is one that
> should be everywhere.
> 🚨 That question has NOT been put to any of the three: the Loki window taken for the third instance
> was searched for *delete* and *prune*, not for these three Warnings. It is the cheapest unasked
> question on this page — ask it before inspecting a schema.

## Open

**For the third instance the question has MOVED** (2026-09-17). Its history rows all carry
`changed_by = system-security`, and only the `mesh_nodes` trigger writes that column — so the row was
committed to `deployments.mesh_nodes`, 53 times, with `main_node` equal to its path and `state:
Active` at `v62`. Nothing about the WRITE is unexplained any more; what is unexplained is what
removed or replaced that row afterwards. Two shapes can do it and only one leaves a trace: a DELETE
(the history trigger is `AFTER INSERT OR UPDATE`, so a delete leaves the version rows untouched and
writes nothing), or an EQUAL-version overwrite (which applies, and which the history trigger's
`ON CONFLICT … DO NOTHING` silently declines to record). The Loki window taken for it covered
10:03–11:09Z and was searched for *delete* and *prune*; the action's own observation deadline was
11:16:56Z, and the three read-fault Warnings named above were never searched for at all.

*Why the first two instances' rows specifically are unreachable* is not settled from the MCP surface
alone — it needs the partition schema inspected on that portal (`main_node`, `partition_access` and
`user_effective_permissions` for `agenticengineering`, against the same three columns for
`agenticprimer`, which works; and for `deployments`, where the control is one ROW rather than one
partition — `pearl-provision-20260914` against a sibling that reads, [#4513](https://github.com/Systemorph/MeshWeaver/issues/4513)).
All three instances above are still live and reproduce on demand, so the evidence has not decayed.
Do not repair any of them by restoring versions until the read seam is understood; a restore takes
the same path and can land the same way.

**The one thing every caller can do meanwhile is the read-back**, and it now has a worked call site:
`Essentials/OperationRequest`'s plan DSL confirms a created node is readable before its step reports
`created`, and fails the step naming the inconsistency when it is not
([MeshWeaver.Plugins#1972](https://github.com/Systemorph/MeshWeaver.Plugins/pull/1972)). A step that
says `created` because the create call returned cannot distinguish this failure from success, which
is how the third instance surfaced as thirty minutes of `not yet visible` instead of one named
error.

### Candidates from the code (not yet confirmed against a live schema)

The durable write and the mesh-wide **announcement** are two separate steps: the row goes to
storage, and `IMeshChangeFeed` separately tells the running mesh the path exists. Everything that
decides *reachability* keys off the announcement, not the row — `PathResolutionService` caches path
resolution, and a path cached as a miss stays a miss for the life of the process, while a live
children listing runs its SQL once at `Initial` and re-queries only on a change notification.
That shape produces exactly the three-seam signature above, and it is size-independent. Places the
announcement can be lost while the row (and its history row) commits:

- `MeshNodeTypeSource` announces via `WriteAndPublishCreated` **only** when the incoming node's
  `Version` is 0; a re-add of already-durable content is a bare `Write`. The class documentation
  calls this the #817/#824 announce-loss class and states the consequence outright.
- The `pg_notify` trigger's dedup suppresses NOTIFY entirely for updates touching only
  `description`/`category`/`icon`/`display_order`/… while the history trigger still writes a row.
- The Orleans cross-silo change-feed broadcast logs-and-skips a failed broadcast; the cross-process
  route silently counts a discard.

Two adjacent defects found while looking, worth their own issues: `public.top_level_index` is a
**materialized view that is never rebuilt on partition CREATE** (only by the migration Job and by
`DeletePartition`), which is why partitions created since the last migration are absent from `@/`
autocomplete while their content is searchable; and the runtime and migration `ExcludedSchemas`
lists are **inverted on `agent` and `auth`**.

## Related

- [Oversized Delivery Refusal](/Doc/Architecture/OversizedDeliveryRefusal) — the *other* way an
  acknowledged write goes missing, and explicitly NOT this one: a transport refusal is loud and
  terminal and leaves no version row behind. Rule it in or out by date and by whether anything was
  logged, before attributing an invisible write to message size.
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a stale negative from the
  index is not evidence of absence, and why the point read is the authoritative seam.
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — where a partition's
  node rows actually live.
- [Data Versioning](/Doc/Architecture/DataVersioning) — the version store the third seam reads.
