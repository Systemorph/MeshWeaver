---
NodeType: Markdown
Name: "Setting Up Data Sync"
Abstract: "The practical manual for seeding/syncing MeshNodes into a partition from a sync source — a platform static repo, a node on another instance, or a GitHub repo. Covers the source→target model, version gating, and the golden rule: a sync source participates ONLY in sync (never query or persistence at runtime); only the target partition does."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00897b'/><path d='M4 8a5 3 0 0 0 10 0' fill='none' stroke='white' stroke-width='1.8'/><path d='M4 5v3M14 5v3' stroke='white' stroke-width='1.8' stroke-linecap='round'/><ellipse cx='9' cy='5' rx='5' ry='2' fill='white'/><path d='M14 14h4l-2-2M20 17h-4l2 2' stroke='white' stroke-width='1.8' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Data Sync"
  - "Provisioning"
  - "Partitions"
  - "Setup"
---

# Setting Up Data Sync

This is the **how-to manual** for getting MeshNodes into a partition by *syncing*
them from a source. For the synchronization *protocol* (versions, conflict
resolution, the monotonicity guard) see [DataSyncAndCrdt.md](/Doc/Architecture/DataSyncAndCrdt);
for the static-repo import *mechanism* (fingerprint, content-addressed Activity
lock) see [StaticRepoImport.md](/Doc/Architecture/StaticRepoImport). This page tells you how to
**set one up** and the one rule you must not break.

---

## 1. The model: source → target

```
        SYNC SOURCE                         SYNC TARGET
  (transient, init-only)                (persisted partition)
  ┌────────────────────┐   seed/sync    ┌────────────────────┐
  │ platform static repo│  ───────────▶  │ partition nodes in │
  │ node on another mesh│   (gated by    │ the DB — the owning │
  │ MeshNodes in GitHub │    version)    │ hub is authoritative│
  └────────────────────┘                └────────────────────┘
        ▲ NOT queried                          ▲ queried + persisted
        ▲ NOT persisted                        ▲ served to clients
        ▲ discarded after sync                 ▲ the single runtime source
```

- A **sync source** is something you use to *synchronize other nodes, then throw
  away*. It is consulted at **initialization only**, and only when the target is
  out of date (§3). It is **never the live serving copy**.
- A **sync target** is the partition's persisted nodes. The **owning hub is
  authoritative** — the per-node hub at the node's path for mesh nodes (see
  [DataSyncAndCrdt.md §1](/Doc/Architecture/DataSyncAndCrdt)). This is the single runtime source
  for query and persistence.

> These are MeshNodes we ship/own — built-in **agents**, **language models**,
> **documentation**, sample graphs. They are authored *outside* the live mesh and
> synced *into* it.

---

## 2. 🚨 The golden rule

> **A sync source participates in SYNC ONLY — never in query, never in
> persistence, at runtime. Only the sync target participates in query and
> persistence.**

Today a synced collection's source *and* target both answer queries (and both get
persisted). That double-source is why a value-equality **dedup** exists on the
sync stream at all — a band-aid that once also swallowed a legitimate roll-back
`Full` (that hole is closed: `SetCurrent` value-dedups patches only — see
[DataSyncAndCrdt.md §6, §10](/Doc/Architecture/DataSyncAndCrdt)). Fix the *source*,
not the symptom:

| Role | Sync | Query | Persistence |
|---|:--:|:--:|:--:|
| **Sync source** (static repo / remote node / GitHub) | ✓ | ✗ | ✗ |
| **Sync target** (persisted partition, owning hub) | ✓ | ✓ | ✓ |

Single-source ⇒ no redundant value-equal frames ⇒ **no dedup needed**.

Declare participation per type/source on its storage-adapter registration
(*"I am only a sync source"* vs *"I participate in the mesh query / persistence"*).
The query provider and the persistence write-back each skip sources whose
participation excludes them.

> **Status.** The participation flag (excluding the source from query + persistence)
> is the agreed design; today the static-repo source is consulted only at import
> time and the importer writes through the *target's* canonical pipeline, so the
> source already isn't a runtime serving copy — wiring the explicit flag is the
> remaining step that lets the dedup be deleted.

---

## 3. Version gating — sync only when out of date

A sync must be **idempotent and cheap on the hot path** (every boot). What records
"this exact source content is already installed" is a **content-addressed Activity
node** — `{Partition}/_Activity/import-{fingerprint}` — whose `Succeeded` status is
the durable checkpoint. The id *is* the fingerprint, so the marker doubles as the
cross-replica lock: concurrent replicas racing the same import converge on one
execution.

On init, compute the source's fingerprint and look for that marker:

- **Succeeded marker present** → done. No sync, no work — **and the source isn't
  loaded/served** (the shipped DLL / remote pull / git clone never happens for
  content). The importer still verifies the partition root + governance nodes, and
  self-heals with a full re-import if a content sentinel is missing.
- **Absent (or a forced re-import)** → run the sync (upsert + prune per the
  source's `SyncMode`), then write the marker.

The fingerprint is order-independent and changes iff a node is added, removed, or
modified — `PartitionSourceFingerprint.Compute(nodes, versioned)`. Versioned
sources hash `(path, version)`; unversioned ones hash `(path, contentHash)`. Full
mechanism in [StaticRepoImport.md](/Doc/Architecture/StaticRepoImport).

---

## 4. Source kinds

The source is an abstraction (`IStaticRepoSource`: a `Partition`, a `Versioned`
flag, a `SyncMode`, and `EnumerateSourceNodes()` returning authored nodes **with
content**). The same target pipeline accepts any source that can enumerate nodes.
`SyncMode` decides what the import **prunes** after upserting: `FullReplace`
(default — mirror the partition to the repo) or `Additive` (leave unmatched nodes
alone, which is what lets a user's own skills survive a re-import).

### a. Platform static repo — *available today*
MeshNodes shipped in an assembly (models, skills, harnesses, docs). Implement
`IStaticRepoSource`, enumerate from your in-memory provider:

```csharp
public sealed class SkillStaticRepoSource(BuiltInSkillProvider provider) : IStaticRepoSource
{
    public string Partition => SkillNodeType.RootNamespace;
    public bool Versioned => false;            // skill .md has no version → hash content
    public PartitionSyncMode SyncMode => PartitionSyncMode.Additive;  // user skills survive
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        provider.GetStaticNodes()
            .Where(n => !n.Segments.Skip(1).Any(s => s.StartsWith('_'))) // content only; skip _Access governance
            .ToArray();
}
```

Real examples: `ModelStaticRepoSource`, `SkillStaticRepoSource`,
`HarnessStaticRepoSource` (registered together by
`AiContentSources.AddBuiltInAiContentSources`) and `DocumentationStaticRepoSource`.
There is deliberately **no** Agent source in the framework: the built-in agents
moved to the `Agent` plugin, so the `Agent` partition is served from the DB but
filled by the plugin, not by this binary.

### b. A node/partition on another instance — *same abstraction, planned*
The source enumerates nodes pulled from a **remote mesh** (another portal/instance)
instead of an embedded provider — e.g. `GetRemoteStream<MeshNode>` / a mesh query
against the remote address. Everything downstream (fingerprint gate, canonical
upsert into the target, prune) is identical. `Versioned = true` when the remote
nodes carry meaningful versions.

### c. MeshNodes in a GitHub repo — *the "sync from anywhere" target*
The source enumerates nodes read from a **public GitHub repository** over HTTP —
list the tree, fetch each authored MeshNode file's content, map file→node. Pin the
ref to an **immutable git tag** (`v$(PlatformVersion)`, e.g. `v3.0.0-rc1`) and set
`Versioned = true`, so the fingerprint changes exactly when the release tag changes
and a boot at the same tag is a no-op (§3). No clone, no working copy: the GitHub
REST API (`git/trees/{ref}?recursive=1` for the listing) + `raw.githubusercontent.com`
(for content) is enough for a public repo, and all HTTP goes through `IIoPool`
(never `Observable.FromAsync` — see [ControlledIoPooling.md](/Doc/Architecture/ControlledIoPooling)).

> 🥚 **The chicken-and-egg: pin a TAG, not the build's own commit.** A build cannot
> embed *its own* commit SHA — the hash does not exist until after the commit, and a
> commit cannot reference itself. So a deployed binary syncs from the **release tag**
> derived from its `PlatformVersion` (`v3.0.0-rc1`), not a baked-in SHA. The tag is
> created at release time, *after* the commit, and GitHub resolves tag→commit at sync
> time. The tag must be **immutable** (annotated, never force-moved) so the fingerprint
> is sound. Tag-triggered GitHub Actions ([ReleaseProcess.md](/Doc/Architecture/ReleaseProcess))
> build the clean-versioned image on the same `v*` tag — so code, image, and synced
> content all key off one tag.

**This is the goal: sync from anywhere.** Once docs (and samples, agent/model
templates) are synced from GitHub into the partition, the platform **no longer
needs to compile/embed them** — the `MeshWeaver.Documentation` embedded-resource
build step becomes unnecessary; the partition is seeded from the repo and served
from the DB like any other node.

> ⚠️ **The "stop compiling docs" cutover is gated, not global.** Adding a GitHub
> *source* is additive and safe. *Demoting* the embedded doc-serving path is the
> separate Phase-4 cutover in [StaticRepoImport.md](/Doc/Architecture/StaticRepoImport): the
> monolith serves docs in-process from the embedded overlay today and must keep
> working, while the distributed/PG path is the one that needs the DB-materialized
> copy. So switch serving to the partition **opt-in on the distributed path**,
> verified end-to-end — never a global demote in one step.

---

## 5. Setup steps

1. **Implement the source.** A class implementing `IStaticRepoSource` for your
   target `Partition`, enumerating the authored nodes *with content*.
2. **Register it in DI** (it's discovered via
   `hub.ServiceProvider.GetServices<IStaticRepoSource>()`):
   ```csharp
   builder.ConfigureServices(s => s.AddSingleton<IStaticRepoSource, SkillStaticRepoSource>());
   ```
3. **Let init run it.** `StaticRepoImporter.ImportAll(hub)` ("sync context init")
   imports every registered source on boot — no-op when none is registered, and a
   no-op per source when the fingerprint already matches (§3).
4. **Keep the source out of the runtime read/write path** (§2): serve the
   partition from the DB target, not from the source provider/overlay. Governance
   nodes you intend to keep in-memory (e.g. `_Access` policy) are simply excluded
   from `EnumerateSourceNodes()`.

That's it: implement → register → boot. Idempotent, distributed-replica safe,
served from the DB like any other node.

---

## 6. Declarative sync config — what exists, and the proposed shape

Hard-coding sources in DI (§5.2) is the bootstrap path, and it is what the
**`IStaticRepoSource`** pipeline described above uses today.

**What exists today.** Config-as-data already works for the *pull-from-elsewhere*
sync engines, one config node per source, edited through the standard node-content
editor: GitSync's config satellites (`gitsync-cfg:{path}`) and instance sync's
`{space}/_Sync/{sourceId}` registrations ([InstanceSync](/Doc/Architecture/InstanceSync)).
Both surface on the platform-admin **Partitions** page through the
`IPartitionSyncSourceProvider` seam (`PartitionSyncAdminLayoutArea`), which is also
where a partition is flipped between *Synced* and *Not synced* — that flip sets the
partition root's `SyncBehavior` to `ExcludeThisAndChildren`, decoupling it from
static-repo import.

**What is proposed** (design, not implemented): the same treatment for the
static-repo pipeline — a `PartitionSync`-shaped config node in the **admin
partition**, so you add, change, or stop a *partition* sync at runtime with no
redeploy:

```jsonc
// PROPOSED — MeshNode { Namespace="Admin", Id="sync-doc", NodeType="PartitionSync" }.Content
{
  "targetPartition": "Doc",
  "source":  "github",
  "url":     "https://github.com/Systemorph/MeshWeaver",
  "ref":     "v3.0.0-rc1",   // immutable release tag = the version gate (§3, §4c)
  "path":    "src/MeshWeaver.Documentation/Data",
  "enabled": true
}
```

Pin an **immutable release tag** (`v$(PlatformVersion)`), not a moving branch and
not the build's own commit (§4c chicken-and-egg), so the fingerprint (§3) is exact
and a re-boot at the same tag is a guaranteed no-op. The `ref` resolves to a single
canonical GitHub tree URL:

```
https://github.com/Systemorph/MeshWeaver/tree/v3.0.0-rc1/src/MeshWeaver.Documentation/Data
```

Bumping the sync to a newer release is one edit to the config node's `ref` (or, for
the default, it falls out of the deployed binary's `PlatformVersion`) — the next
boot sees a new fingerprint and re-syncs; everything in between is a no-op.

In that shape the sync engine would **query the admin partition** for
`PartitionSync` nodes and run each through the same source→target pipeline (§1),
fingerprint-gated (§3). A config node says *"synchronize this partition from
there"* — it is itself an ordinary **target** node (queried + persisted in the
admin partition); it *configures* a source, it is not one.

### Breaking the sync — taking over a partition

Because a declarative sync is data, it is **revocable**. To **take over** a
partition (own it locally, stop tracking the upstream), break the sync:

- **Today, for static-repo import:** flip the partition to *Not synced* on the
  Partitions page — that sets the root's `SyncBehavior = ExcludeThisAndChildren`,
  and the importer leaves the whole partition alone. The persisted nodes stay; they
  are now locally authoritative and your edits survive. (Per-node `SyncBehavior`
  claims/protects individual nodes in any mode.)
- **In the proposed config-node shape:** `enabled: false` (or delete the config
  node) does the same for that source.
- **Re-enable** → sync resumes; the next out-of-date fingerprint prunes + upserts
  according to the source's `SyncMode`, so under `FullReplace` any local
  "take-over" edits to synced nodes are overwritten. That's the contract: a
  partition is *either* synced-from-source *or* locally owned — breaking the link
  is how you switch from the former to the latter.

This is the clean ownership switch: ship a partition synced from a repo, and any
deployment can **break the sync and make it its own**.

---

## 7. Pitfalls

- **Source bleeding into runtime reads.** If queries still return the source copy
  *and* the target copy, you re-introduce the double-source the dedup hides. The
  source must be sync-only (§2).
- **No version gate.** Without the fingerprint-marker check, you re-import on
  every boot — wasted work and write amplification across replicas.
- **Enumerating from the live mesh.** `EnumerateSourceNodes()` must read the
  authored content (assembly / remote / git) — never the live mesh you're writing
  into, or the fingerprint chases its own tail.
- **Importing governance/user content.** Only ship platform-owned content; never
  full-replace a partition that also holds user-authored nodes without scoping the
  prune.

---

## 8. See also

- [GitHubSync.md](/Doc/Architecture/GitHubSync) — the user-facing manual for connecting a
  Space to GitHub: export ("sync back"), import / re-import at a commit, the per-user OAuth
  connection, and operator setup.
- [DataSyncAndCrdt.md](/Doc/Architecture/DataSyncAndCrdt) — the sync protocol: owning-hub
  authority, versions, conflict resolution, why single-sourcing removes the dedup.
- [StaticRepoImport.md](/Doc/Architecture/StaticRepoImport) — the import mechanism: fingerprint,
  content-addressed Activity lock, canonical upsert, prune.
- [ExtensibleDefaults.md](/Doc/Architecture/ExtensibleDefaults) — system defaults + mesh-level
  extensions (agents, models) the static repo seeds.
