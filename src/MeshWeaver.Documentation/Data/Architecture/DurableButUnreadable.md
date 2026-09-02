---
Name: Durable But Unreadable
Category: Architecture
Description: A write that is acknowledged, versioned and permanently invisible. Two confirmed live instances on two different portals, one of them in a plain user partition with no plugin involved. How to tell it apart from the two read seams disagreeing, and why a mint-time read-back is the only acknowledgement worth trusting.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 1.7 3.6 3 8 3"/><path d="M20 5v6"/><path d="m16 16 6 6"/><path d="m22 16-6 6"/></svg>
---

There is a failure mode in which a write **succeeds in every observable way except the one that
matters**: the caller gets an acknowledgement, a version row is created with the right content and
`state: Active`, and no reader — not a point read, not a query, not a children listing — will ever
see the node again.

It is the most expensive class of defect the platform has, because every gate on the write path is
green. This page is how to recognise it, how to tell it from its lookalike, and what the two
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

## The two confirmed instances (measured 2026-09-01)

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

## Open

*Why the AgenticEngineering rows specifically are unreachable* is not settled from the MCP surface
alone — it needs the partition schema inspected on that portal (`main_node`, `partition_access` and
`user_effective_permissions` for `agenticengineering`, against the same three columns for
`agenticprimer`, which works). Both instances above are still live and reproduce on demand, so the
evidence has not decayed. Do not repair either by restoring versions until the read seam is
understood; a restore takes the same path and can land the same way.

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
