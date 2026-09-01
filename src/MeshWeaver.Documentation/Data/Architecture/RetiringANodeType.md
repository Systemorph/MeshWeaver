---
Name: Retiring a NodeType
Category: Architecture
Description: How to withdraw an in-mesh NodeType, and the prune asymmetry that strands its definition on every two-way-synced partition — the source nodes are deleted, the definition is kept, and the type is parked in CompileError forever.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/></svg>
---

# Retiring a NodeType

[Adding a New Node Type](/Doc/Architecture/AddingANewNodeType) covers a **compiled** type — a
content record and a definition class in `src/`. This page is about the other kind: an **in-mesh**
NodeType, shipped by a package or a node repo as `{Type}.json` plus `{Type}/Source/*.cs`, compiled
at runtime by [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation). Withdrawing one is not
the mirror image of adding one: the package can stop shipping the type, and the live mesh keeps it
anyway — as a permanent `CompileError` nobody authored.

## The rule

> **A NodeType is retired only when its DEFINITION is gone from every live mesh — not when the
> package stops shipping it.** Deleting `{Type}.json` and `{Type}/Source/**` in one commit removes
> the sources from every deployment and leaves the definition standing on the two-way-synced ones.
> What remains is a `configuration` lambda naming types that no longer have source: a permanent
> `CompileError` no re-import can clear.

## What a retirement has to remove

| Artifact | Where | Removed by |
|---|---|---|
| `{Type}/Source/*.cs`, `{Type}/Test/*.cs` | the package / node repo | the import's prune |
| `{Type}.json` — the `NodeTypeDefinition` | the package / node repo | the import's prune — **except see below** |
| Instances (`nodeType:{Type}`) | anywhere on the mesh, incl. user partitions | nothing automatic |
| `{Type}/Release/**` | mesh-minted | nothing — `IsMeshMintedRelease` spares it by design |

Only the first row is reliable. The rest need a decision.

## 🚨 The prune cannot reach the definition — but reaches its sources

The importer's prune is guarded by a **sync baseline**. `ImportConflictPolicy.PreservesFromPruneOf`
(`src/MeshWeaver.Graph/StaticRepoImporter.cs`) keeps any node the repo no longer carries when it was
changed on the server since the last sync:

```csharp
public bool PreservesFromPruneOf(MeshNode? target) =>
    (PreserveServerNewer || PreserveServerAdditions) && !Force && Since is { } since
    && target is not null && target.LastModified > since;
```

That guard exists for a good reason — a node someone *added* on the server is a local addition to be
committed back, not a stale extra (see [Static Repo Import](/Doc/Architecture/StaticRepoImport)). But
it reads `LastModified`, and `LastModified` does not distinguish an author from the framework:

- **A NodeType definition is written by the framework on every compile.** `compilationStatus`,
  `lastCompileStartedAt`, `lastCompileSucceededAt`, `latestReleasePath`, `latestAssemblyMvid`,
  `compiledSources`, `requestedReleaseAt` all live on the definition's content and are stamped by
  `system-security`. Its `LastModified` therefore sits *after* any sync baseline.
- **Its `Source`/`Test` Code nodes are written only by real edits.** Nothing framework-generated
  touches them, so their `LastModified` stays where the last human edit left it — *before* the
  baseline.

So on the import that drops the type, the prune deletes the sources and keeps the definition:

```
↩ Kept Edu/Course (added on the server — commit to sync it back).
```

The protection is **self-perpetuating**: the sync baseline is deliberately not advanced past a node
it has just protected (otherwise the next import would prune the server addition one cycle later —
`TwoWaySyncTest.TwoWay_ServerAddition_SurvivesEveryLaterRepoPush`). The definition is kept on this
import, and on every import after it.

## What the orphan looks like

The type keeps serving its last good assembly until the framework or module hash changes; then the
cached build is invalidated, the compile runs against an empty source set, and the generated
attribute at line 58 cannot resolve the content type or the layout extension:

```
Compilation failed for 'Edu/Course':
CS0246 Error (line 58): The type or namespace name 'CourseContent' could not be found …
CS1061 Error (line 58): 'LayoutDefinition' does not contain a definition for 'AddCourseLayoutAreas' …
--- Source discovery ---
Executed source queries (2):
  - namespace:Edu/Course/Source scope:subtree nodeType:Code
  - namespace:Edu/Course/Test scope:subtree nodeType:Code
Matched Code nodes (0):
  (none) — the configuration lambda cannot reference types because no source files were included.
```

**`Matched Code nodes (0)` is the signature.** A `CS0246` with a non-empty match list is an ordinary
broken edit; a `CS0246` with an empty one means the source set itself is gone, and no amount of
recompiling will change it. The definition's `failedBuildInputs` carries the same fact as `src=0`.

The type is then parked, and its per-node hub never activates — every read of the definition's path
costs a full `GetMeshNode` timeout:

```
Unavailable: Edu/Course — GetMeshNode('Edu/Course') timed out after 15.0s …
Target: NO LOCAL HUB at 'Edu/Course' — it never activated here.
```

## Diagnosing one

Three reads, in this order. All are read-only.

1. **`get_diagnostics @{Type}`** — `status: Error` with `Matched Code nodes (0)`.
2. **`search namespace:{Type} scope:subtree`** — only `Release/**` comes back; no `Code` nodes.
3. **`search nodeType:{Type}`** — how many instances would be affected by removing it.

Two false passes to refuse:

- **`lsp_diagnostics_for_node` answers `ok:false, status:"Unavailable"`** on exactly these types,
  because the per-node hub cannot activate. That is a FAILURE to check, never a clean check — see
  [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation).
- **A point `get` on the definition returns `Unavailable`, not `Not found`.** The node exists; the
  read reached no verdict. Confirm existence with a `scope:children` listing instead, per the
  [CQRS rules](/Doc/Architecture/CqrsAndContentAccess).

## Repairing one

The orphan is the defect, so the repair is to finish the retirement — **not** to reconstruct source
the product deliberately deleted. Reconstruction is almost never possible anyway: a retired type's
layout areas usually reference an equally retired supporting cast, and a fresh look-alike nobody can
diff against the original is worse than leaving the node alone.

1. **Establish the instance count is zero** (`search nodeType:{Type}`). If it is not, the instances
   must be migrated to the replacement type first — retiring a type under live instances leaves
   pages with no renderer.
2. **Delete the definition subtree** — `delete {Type}` is recursive and takes the mesh-minted
   `Release/**` with it.
3. **The delete needs a human identity.** Node types live in package-managed partitions whose
   `_Activity` satellite is not writable by an ordinary account, so the API refuses:
   *"Access denied: user 'x' lacks Delete permission on '{Type}/_Activity/compile-…'"*. That refusal
   is a real gate, not an obstacle to route around — open `/{Type}/Delete` and confirm under an
   identity that holds Delete.

Capture the definition JSON before deleting. `{Type}.json` is also still in the package repo's git
history at the retiring commit, which is the faithful record of what the type was.

## Worked example — `Edu/Course`

The `Edu` package retired its course-root type on 2026-07-17
(`MeshWeaver.Plugins@5cb3ed02`, *"slim Edu to the internals"*): course roots became `Store/Plugin`
nodes in the `Education` category, and the commit removed `Edu/Course.json`,
`Edu/Course/Source/CourseContent.cs`, `Edu/Course/Source/CourseLayoutAreas.cs` and
`Edu/Course/Test/CourseTests.cs` together. `CourseCatalogLayoutAreas` still names both shapes and
calls the old one what it is:

```csharp
public const string CourseType = "Edu/Course";     // the legacy type — its lane may be empty
public const string PluginType = "Store/Plugin";   // the modern course-root shape
```

One production deployment carries no such node at all. On another, the import kept the definition
(`↩ Kept Edu/Course (added on the server …)`, still logged on every run six weeks later) while its
sources were pruned; the type went on serving its 2026-08-12 release until a framework-hash change
on 2026-09-01 invalidated the cached assembly and forced the recompile that surfaced it
(issue #2951). Neither deployment had a single `nodeType:Edu/Course` instance — the catalog had
already moved to the plugin-root lane — so the type was dead code with a live error.

## The design question this leaves open

The prune guard cannot currently tell a framework bookkeeping write from an authored one, so **no
two-way-synced partition can ever retire a NodeType by dropping it from the package**. Making the
guard read *authored* change rather than `LastModified` would close that — but the same guard is
what stops a genuine server-side addition from being deleted a cycle later, so it is a change to
make deliberately and with `TwoWaySyncTest` in hand, not as a side effect of clearing one orphan.
Until then, retiring an in-mesh NodeType has a manual step: **delete the definition on each live
mesh**, in the same change set that drops it from the package.

## Related

- [Adding a New Node Type](/Doc/Architecture/AddingANewNodeType) — the compiled counterpart
- [NodeType Compilation & Releases](/Doc/Architecture/NodeTypeCompilation) — source discovery, the failure cascade, the pre-prod sweep
- [Static Repo Import](/Doc/Architecture/StaticRepoImport) — sync modes, claims, and what the prune removes
- [Import Write Ordering](/Doc/Architecture/ImportWriteOrdering) — a type lands before the instances that name it
- [Managing Partition Sync](/Doc/Architecture/PartitionSyncGuide) — the admin-facing view of the same controls
