---
NodeType: Markdown
Name: "CQRS — Queries, Reads, Writes, Operations"
Abstract: "Queries find sets of nodes; GetMeshNodeStream reads a single node's live content; writes go through PatchDataChangeRequest on the owning hub. Operations are named request types handled on the owning hub — the implementation stays private."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#c62828'/><path d='M12 5v5M9 8l3-3 3 3' stroke='white' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/><path d='M5 19l4-4M19 19l-4-4' stroke='white' stroke-width='2' stroke-linecap='round'/><circle cx='6' cy='18' r='1.5' fill='white'/><circle cx='18' cy='18' r='1.5' fill='white'/><circle cx='12' cy='12' r='1.5' fill='white'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "CQRS"
  - "Queries"
  - "Streams"
  - "Consistency"
---

MeshWeaver applies CQRS at every layer: **queries** route through a read-side index optimised for fan-out search; **reads** of a specific node go directly to the owning hub for authoritative, lag-free state; **writes** are RFC 7396 JSON-merge patches applied by that same hub; and **operations** are named request types that keep implementation details private. Picking the wrong channel produces subtle consistency bugs — stale content, lost updates, or silent overwrites. This page tells you exactly which channel to use, when, and why.
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 310" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <polygon points="0 0, 8 3, 0 6" fill="#90a4ae"/>
    </marker>
    <marker id="arr-blue" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <polygon points="0 0, 8 3, 0 6" fill="#1e88e5"/>
    </marker>
    <marker id="arr-green" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <polygon points="0 0, 8 3, 0 6" fill="#43a047"/>
    </marker>
    <marker id="arr-orange" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <polygon points="0 0, 8 3, 0 6" fill="#f57c00"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="310" rx="12" fill="#1a1f2e"/>
  <rect x="20" y="20" width="160" height="60" rx="10" fill="#5c6bc0"/>
  <text x="100" y="46" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff" text-anchor="middle">Caller</text>
  <text x="100" y="63" font-family="sans-serif" font-size="11" fill="#c5cae9" text-anchor="middle">hub / Blazor view</text>
  <rect x="20" y="130" width="160" height="60" rx="10" fill="#37474f"/>
  <text x="100" y="156" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">Read-side Index</text>
  <text x="100" y="173" font-family="sans-serif" font-size="11" fill="#b0bec5" text-anchor="middle">Query / QueryAsync</text>
  <text x="100" y="188" font-family="sans-serif" font-size="10" fill="#78909c" text-anchor="middle">eventually consistent</text>
  <rect x="20" y="230" width="160" height="55" rx="10" fill="#1b5e20" stroke="#43a047" stroke-width="1.5"/>
  <text x="100" y="254" font-family="sans-serif" font-size="11" fill="#a5d6a7" text-anchor="middle">Sets / shell projections</text>
  <text x="100" y="270" font-family="sans-serif" font-size="10" fill="#81c784" text-anchor="middle">path · name · nodeType · version</text>
  <line x1="100" y1="190" x2="100" y2="228" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)" stroke-dasharray="4,3"/>
  <text x="108" y="213" font-family="sans-serif" font-size="10" fill="#81c784">project only</text>
  <rect x="300" y="110" width="180" height="90" rx="10" fill="#0d47a1" stroke="#1e88e5" stroke-width="2"/>
  <text x="390" y="136" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff" text-anchor="middle">Owning Hub</text>
  <text x="390" y="153" font-family="sans-serif" font-size="11" fill="#90caf9" text-anchor="middle">per-node actor</text>
  <text x="390" y="170" font-family="sans-serif" font-size="11" fill="#90caf9" text-anchor="middle">authoritative state</text>
  <text x="390" y="187" font-family="sans-serif" font-size="10" fill="#64b5f6" text-anchor="middle">GetMeshNodeStream</text>
  <rect x="560" y="110" width="170" height="60" rx="10" fill="#4a148c" stroke="#8e24aa" stroke-width="1.5"/>
  <text x="645" y="136" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">Persistence</text>
  <text x="645" y="153" font-family="sans-serif" font-size="11" fill="#ce93d8" text-anchor="middle">Postgres / Cosmos / Memory</text>
  <rect x="560" y="220" width="170" height="55" rx="10" fill="#bf360c" stroke="#f57c00" stroke-width="1.5"/>
  <text x="645" y="244" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">Patch Write</text>
  <text x="645" y="261" font-family="sans-serif" font-size="11" fill="#ffcc80" text-anchor="middle">RFC 7396 JSON-merge patch</text>
  <line x1="180" y1="42" x2="297" y2="140" stroke="#37474f" stroke-width="1.5" marker-end="url(#arr)" stroke-dasharray="5,3"/>
  <text x="215" y="83" font-family="sans-serif" font-size="10" fill="#78909c" transform="rotate(-28,215,83)">Query</text>
  <line x1="180" y1="50" x2="298" y2="145" stroke="#1e88e5" stroke-width="2" marker-end="url(#arr-blue)"/>
  <text x="207" y="74" font-family="sans-serif" font-size="10" fill="#64b5f6" transform="rotate(-28,207,74)">GetMeshNodeStream</text>
  <line x1="100" y1="80" x2="100" y2="128" stroke="#37474f" stroke-width="1.5" marker-end="url(#arr)" stroke-dasharray="5,3"/>
  <line x1="480" y1="155" x2="558" y2="143" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="495" y="143" font-family="sans-serif" font-size="10" fill="#90a4ae">index sync</text>
  <line x1="558" y1="155" x2="482" y2="155" stroke="#1e88e5" stroke-width="1.5" marker-end="url(#arr-blue)"/>
  <line x1="480" y1="170" x2="558" y2="232" stroke="#f57c00" stroke-width="2" marker-end="url(#arr-orange)"/>
  <text x="490" y="212" font-family="sans-serif" font-size="10" fill="#ffb74d" transform="rotate(30,490,212)">PatchDataChangeRequest</text>
  <text x="380" y="295" font-family="sans-serif" font-size="11" fill="#90a4ae" text-anchor="middle" font-style="italic">Queries find sets (eventually consistent); GetMeshNodeStream reads a single node's live content from its owning hub.</text>
</svg>

## The five primitives at a glance

| Intent | Primitive |
|---|---|
| **Bind a UI control to a node** | Declare a path-bound control (`new MeshNodeThumbnailControl { NodePath = path }`) or `JsonPointerReference`. The Blazor view subscribes via `IMeshNodeStreamCache` — layout-area code never loads the node. See [Data Binding](xref:GUI/DataBinding). |
| **Find a set of nodes** | `mesh.Query<T>(request)` — reactive, live, composes with `Select`/`Where`/`Subscribe`. (The one-shot `QueryAsync` form is discouraged in hub-reachable code; see below.) |
| **Read a known node's content (one-shot)** | `hub.Post(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(addr))` + `hub.Observe` |
| **Subscribe to a node's live updates** | `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())` |
| **Write to a node** | `hub.Post(new PatchDataChangeRequest(...), o => o.WithTarget(addr))` (or `DataChangeRequest` for full updates) |
| **Perform an operation on a node** | Named request type handled on the owning hub — e.g. `ExecuteScriptRequest`, `MoveNodeRequest`, `ImportRequest` |

> **Read this once and remember it:** *queries are for sets*. A query that happens to return exactly one row is still a query — and still carries the same consistency caveats.

---

## Why queries are not for reading content

Queries route through a **read-side index** — a cached projection that is eventually consistent. In production the lag is single-digit to tens of milliseconds, but that window is long enough to break any pattern that requires read-your-writes:

- **Patch operations** (read current → merge → write) will merge against a stale base and silently lose concurrent changes.
- **Auditing** ("did my change take?") will return the old value and mislead the caller.
- **Decision flows** ("is this already configured?") may act on information that is moments out of date.

That lag is *acceptable* for browsing and autocomplete. It is *lethal* for content access.

> **Layout areas should bind, not fetch.** The lag problem disappears entirely when the GUI subscribes directly to `GetRemoteStream<MeshNode, MeshNodeReference>` — the view shows the authoritative current state and re-renders on every change. See [Data Binding](xref:GUI/DataBinding) for the bind-by-path pattern.

`GetDataRequest(new MeshNodeReference())` goes straight to the **owning hub's workspace** — the source of truth. No staleness. It also activates the hub if it was cold; no pre-subscribe needed.

---

## 🚨 Query `.Content` is always stale — never read it

`mesh.Query<MeshNode>`, `mesh.QueryAsync<MeshNode>`, and the lower-level `IStorageAdapter.Query(...)` enumerate MeshNodes by reading the read-side index. The returned objects technically have a `.Content` property — **but it must never be read**. The catalog is eventually consistent and the `Content` column lags every committed write by the index-refresh window.

**Bright-line rules — no exceptions:**

| What you have | What you do | What you must NOT do |
|---|---|---|
| A query to enumerate paths / names / nodeTypes | `await foreach (var n in adapter.Query(queries)) yield return n.Path;` | Read `n.Content` |
| A known **path**, want the live MeshNode | `workspace.GetMeshNodeStream(path)` | `adapter.Query($"path:{path}")` and read `.Content` |
| A known path, want a one-shot read | `hub.Post(GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))` + `hub.Observe(...)` | Anything that goes through the index |
| Recursive subtree operation (Copy, Move, Delete…) | `hub.Post(CopyNodeRequest / MoveNodeRequest / DeleteNodeRequest, WithTarget(sourcePath))` — the owning hub uses `GetMeshNodeStream` internally | Load every node from the query result and write each one |

**Treat `MeshNode.Content` on a query row as if the column does not exist.** Project to the metadata you need — `Path`, `Name`, `NodeType`, `Icon`, `LastModified`, `Version`, `State` — and stop. If your call site needs `Content`, you are at the wrong layer: either reshape it to use `GetMeshNodeStream`, or send the work to the owning hub via a named request type.

### 🚨 Select only what you need — no whole-node loads

A query is a **shell projection**, not a node loader. Before writing `Query<MeshNode>` or `QueryAsync`, ask "which fields do I actually consume?" and add a `select:` clause to pull only those. The whole-`MeshNode` shape is a historical convenience that defeats partition routing, balloons memory, and invites the stale-`Content` antipattern.

The most common consumer — "is this set up to date?" — needs only `(path, version)`. That is enough to compare against a cached snapshot and decide "nothing changed, skip the work" vs. "something changed, recompile." You do **not** load the nodes themselves to answer this question.

```csharp
// ❌ Wrong — loads every descendant node to ask one yes/no question.
await foreach (var n in mesh.QueryAsync<MeshNode>(
    $"namespace:{root} scope:descendants nodeType:Code"))
{
    if (n.Version != cachedVersions[n.Path]) needsRecompile = true;
}

// ✅ Right — project (path, version), compare against snapshot.
var stale = false;
await foreach (var row in mesh.QueryAsync<MeshNode>(
    $"namespace:{root} scope:descendants nodeType:Code select:path,version"))
{
    if (!cachedVersions.TryGetValue(row.Path, out var prev) || row.Version != prev)
    { stale = true; break; }
}
```

**Field cheat-sheet:**

| Question | `select:` clause |
|---|---|
| "Does it exist?" | `select:path` |
| "Is anything stale?" | `select:path,version` |
| "Render a tree / list / picker" | `select:path,name,nodeType,icon` |
| "Show last-modified column" | `select:path,name,lastModified` |
| "Compute access shells" | `select:path,nodeType,mainNode` |

When the projection is not enough — you actually need `Content` for a specific path (compiler input, document viewer, edit form) — fetch *that one node* through `workspace.GetMeshNodeStream(path)`. One authoritative read per path, never a subtree-wide content load.

The recompile design that this rule supports is in [project_recompile_via_synced_versions](xref:Architecture/RecompileViaSyncedVersions) — the NodeType keeps a `{sourcePath → version}` snapshot from the synced query, and a divergent emission triggers re-fetch and recompile. Nothing in the catalog row's `Content` is consulted.

### 🚨 Staleness lives on the owner — never query to check "is this stale?"

A query is for finding **sets** of things. "Is *this specific thing* up to date?" is a question about one thing, and the answer belongs **on that thing** as a property — never re-derived by querying.

| Pattern | Where it lives |
|---|---|
| `IsDirty` / `NeedsRebuild` / `IsStale` flag | Property on the owning node (set by its own hub) |
| Synced subscription that maintains the flag | The owning hub's `Initialize` hook |
| Snapshot the flag is computed against | Stored on the node itself (survives restart) |
| Consumer wanting to know "is X stale?" | **Read the property. Never query.** |

The cleanest demonstration is the NodeType recompile detector:

```csharp
// In the NodeType's hub WithInitialization — observable pattern, no await,
// no Take(1) on the source subscription (we want to keep listening!).
config.WithInitialization(hub =>
{
    var workspace = hub.GetWorkspace();
    var self = hub.Address.ToString();

    // Two synced queries — Source files and Test files. Path-keyed dedup,
    // Replay(1).RefCount(), provider fan-out. select:path,version keeps
    // the rows light. Persistent subscription — every emission recomputes.
    var sources = workspace.GetQuery($"{self}:sources",
        $"nodeType:Code namespace:{self}/Source scope:descendants select:path,version");
    var tests = workspace.GetQuery($"{self}:tests",
        $"nodeType:Code namespace:{self}/Test scope:descendants select:path,version");

    Observable.CombineLatest(sources, tests, (s, t) =>
            s.Concat(t).Select(n => (n.Path!, n.Version))
                       .ToImmutableSortedSet())
        .Subscribe(current =>
        {
            // Compute IsDirty against the snapshot stored on the node itself.
            workspace.GetMeshNodeStream(self).Update(node =>
            {
                var snapshot = (node.Content as NodeTypeDefinition)?.CompiledSources
                    ?? ImmutableSortedSet<(string, long)>.Empty;
                var dirty = !current.SetEquals(snapshot);
                return node with { /* IsDirty = dirty */ };
            }).Subscribe(_ => { },
                         ex => logger.LogWarning(ex, "dirty flag update failed"));
        });
});
```

**Why this is load-bearing:**

- **One source of truth.** The dirty flag lives where the answer is computed. A separate `InvalidateCache(path)` dictionary keyed by path is a duplicate truth that drifts.
- **Restart-safe.** The hub's `Initialize` runs at activation; the synced query's first emission IS the recompute. No "did we miss a change-feed event" gap.
- **No `Take(1)`** on the dependency subscription. The persistent subscription is the whole point — a source edit while the hub is running must flip `IsDirty` without anyone polling.
- **Consumers read a property.** Asking "is this stale?" by re-querying the dependencies every time is forbidden. The property carries the answer.

A central `InvalidateCache(path)` invalidator outside the owning hub — even when wired to the change feed — is the wrong layer. Move the watcher into the owning hub and let it maintain its own dirty flag.

Reference design: `project_recompile_via_synced_versions.md` → "Sharper design (2026-05-11)".

### The "send the work to the owning hub" pattern (Copy / Move / Delete)

Recursive subtree operations look superficially like "query → load each → do something" — that is the pattern that leaks `Content` reads and stale state. The correct shape sends one request to each affected node's hub, where the handler uses `GetMeshNodeStream` (or the workspace's `MeshNodeReference` reducer) to obtain the **authoritative** state before acting.

```csharp
// Caller — fires one request per descendant, never touches Content from the query.
public IObservable<Unit> DeleteSubtree(string rootPath, IMessageHub hub, IMeshService mesh) =>
    Observable.Create<Unit>(async (observer, ct) =>
    {
        // 1. Enumerate descendant PATHS only — never read .Content from the iteration.
        var paths = new List<string>();
        await foreach (var shell in mesh.QueryAsync<MeshNode>(
            $"namespace:{rootPath} scope:subtree").WithCancellation(ct))
            paths.Add(shell.Path);                         // ← project to path; .Content untouched.
        paths.Add(rootPath);

        // 2. Fan out: one DeleteNodeRequest per address. Each owning hub
        //    handles its own delete — uses workspace.GetMeshNodeStream(self)
        //    if it needs current state, NOT the stale catalog row.
        Observable.Merge(paths.Select(p =>
                hub.Observe(new DeleteNodeRequest(p),
                    o => o.WithTarget(new Address(p)))))
            .Subscribe(_ => { },
                       ex => observer.OnError(ex),
                       () => { observer.OnNext(Unit.Default); observer.OnCompleted(); });
    });
```

```csharp
// Handler — registered on the owning per-node hub. Reads its OWN content via
// the workspace's MeshNodeReference reducer (the source of truth), not via
// any storage adapter or query.
private static IMessageDelivery HandleCopyNodeRequest(
    IMessageHub hub, IMessageDelivery<CopyNodeRequest> request)
{
    var targetPath = request.Message.TargetPath;
    hub.GetWorkspace().GetStream(new MeshNodeReference())!
        .Select(change => change.Value)
        .Where(node => node is not null)
        .Take(1)
        .Subscribe(self =>
        {
            // Use `self` to materialise the target — never query for it.
            hub.Post(new CreateNodeRequest(self! with { /* re-target */ }),
                o => o.WithTarget(new Address("mesh")));
            hub.Post(CopyNodeResponse.Ok(self!), o => o.ResponseFor(request));
        });
    return request.Processed();
}
```

The `DeleteNodeRequest` / `MoveNodeRequest` / `CopyNodeRequest` types are defined in `src/MeshWeaver.Mesh.Contract/CreateNodeRequest.cs`. They route to the source-node's address (or to the mesh hub which forwards). The handler **never** reaches back through the index for content — it reads its own state through the workspace's `MeshNodeReference` reducer, which is the only non-stale view of the node.

> **Summary in one line:** `Query` gives you paths and shells; `GetMeshNodeStream` gives you live content. There is no third channel.

---

## 🚨 No "pedestrian queries" — use synced queries

If a component needs to **react** to a set of MeshNodes (a list, a filter, a catalog, a picker, a compiler input set), do **not** call `meshService.QueryAsync` / `IMeshQueryCore.Query` directly. Use the synced-query pattern from [Synced Mesh Node Queries](SyncedMeshNodeQueries.md):

```csharp
IObservable<IReadOnlyList<MeshNode>> stream = workspace.GetQuery(
    "stable-cache-id",
    "namespace:Agent nodeType:Agent",
    "namespace:Model nodeType:LanguageModel");

stream.Subscribe(snapshot => …);
```

This is the **only** correct way to consume a live MeshNode collection. For free, you get:

- Path-keyed dedup across queries.
- All-Initial gating (no empty-flash before the slowest provider settles).
- Provider fan-out (static-node providers **and** storage providers). Direct `IMeshQueryCore.Query` skips static providers — symptom: "empty Agent dropdown even though MCP `nodeType:Agent` returns 9 entries".
- `Replay(1).RefCount()` upstream sharing — one upstream subscription, many subscribers.
- Hub-level delete fast-path so the view drops the row the moment the owning hub publishes a delete.

A direct `meshService.QueryAsync` / `mesh.Query<MeshNode>` call from application code is a **pedestrian query** and is almost always wrong: either you don't need a live subscription (one-shot — use `GetMeshNodeStream` per path), or you do (use `workspace.GetQuery`).

The *only* legitimate uses of `IMeshQueryCore.Query` are inside the synced-query implementation itself and inside the query engine. Everything user-facing — UI lists, pickers, settings tabs, compiler inputs, recursive operation enumeration — goes through `workspace.GetQuery`.

**Canonical patterns to copy** (read these before writing your own):

| Use case | File |
|---|---|
| API tokens list | `ApiTokenService.GetTokensForUser` — `workspace.GetQuery($"api-tokens:{userId}", …)` |
| Chat agents + models | `AgentChatClient.Initialize` — `workspace.GetQuery("agents-and-models", …)` |
| Access control list | `AccessControlLayoutArea` — `workspace.GetQuery($"access:{nodePath}", …)` |

If you find yourself reading `MeshNode.Content` out of a one-shot `meshService.QueryAsync` to render a UI or feed a compiler, you are at the wrong layer. Wrap the query in `workspace.GetQuery` and subscribe — the recompile or re-render fires automatically when the underlying nodes change.

---

## `GetStream` is access-checked

`workspace.GetMeshNodeStream(path)` (server-side) and `IMeshNodeStreamCache.GetStream(path)` (cache-side, the canonical Blazor read path) both gate on the **caller's** effective Read permission. The cache posts a `GetPermissionRequest` to the owning node hub on first read, caches the `Permission` flags per `(path, userId)` for 30 seconds, and returns an observable that fails with `UnauthorizedAccessException` when Read is not granted. The shared upstream subscription stays system-owned (one handle per path, opened under `ImpersonateAsSystem`); per-user enforcement happens at the subscriber boundary.

Revocation propagates within the TTL window. The permission cache is not invalidated reactively — subscribers can keep listening past a revocation event for up to 30 s before the next `GetStream` issues a fresh probe and surfaces the denial.

Full propagation model: [AccessContextPropagation.md](AccessContextPropagation.md).

---

## 🚨 `Content` is always typed at the `GetMeshNodeStream` boundary

Every emission and every `Update` lambda passing through `workspace.GetMeshNodeStream(path?)` is round-tripped through the workspace's `JsonSerializerOptions` — so `node.Content` is **always** the registered domain type (e.g. `MeshThread`, `NodeTypeDefinition`, `AgentConfiguration`), **never** a raw `JsonElement`. The handle's read path runs a `TypedContentObserver` between the underlying sync stream and the subscriber; the write path wraps the caller's lambda so the deserialised value goes in and the (re-)serialised `JsonElement` comes out before the patch lands on the wire.

```csharp
// ✅ Right — `Content` is the typed MeshThread no matter where the data
//    source stores it (InMemory keeps typed instances; file-system /
//    Postgres / Cosmos round-trip through JSON and would otherwise land
//    as JsonElement).
workspace.GetMeshNodeStream().Update(node =>
{
    if (node.Content is not MeshThread t) return node;   // pattern match Just Works
    return node with { Content = t with { Status = ThreadExecutionStatus.Executing } };
});
```

**Why this matters — the anti-pattern this rule eliminates:**

```csharp
// ❌ WRONG — silently lossy. When Content arrives as JsonElement, the cast
//    fails, the `?? new MeshThread()` fallback overwrites every other field
//    with defaults (Status=Idle, pending={}, etc.), and the next stream.Update
//    persists that default-valued thread. Symptom: tests set Status=Executing,
//    the next AppendUserInput resets it to Idle, the SubmissionWatcher then
//    dispatches a round nobody asked for.
workspace.GetMeshNodeStream().Update(node =>
{
    var thread = node.Content as MeshThread ?? new MeshThread();   // ← silent overwrite
    return node with { Content = thread with { Status = ... } };
});
```

The handle's deserialisation wrap eliminates the `JsonElement` case at the boundary. Callers no longer need the `?? new TFoo()` fallback — if `Content` is genuinely absent or wrong-shaped, the pattern match fails cleanly and the lambda can return `node` unchanged.

**Where the wrap lives:** `MeshNodeStreamHandle.TypedContentObserver` (read path) + `MeshNodeStreamHandle.Update`'s `wrappedUpdate` (write path) in `src/MeshWeaver.Mesh.Contract/MeshNodeStreamExtensions.cs`. Helpers `EnsureTypedContent(node, options)` and `EnsureSerialisedContent(node, options)` are reusable by any other primitive that needs the same shape guarantee.

---

## Where scope walks live

`scope:children / scope:descendants / scope:subtree / scope:hierarchy / scope:ancestorsAndSelf` are **per-provider** responsibilities. The mesh level never walks content; it only coordinates fan-out across providers and merges the results.

| Layer | Class | Walks? |
|---|---|---|
| Mesh | `MeshQuery` (top-level), `RoutingMeshQueryProvider` | **No.** Fans out across providers and partitions, merges per-provider buckets with writable-first ordering, applies post-merge sort/skip/limit/select. |
| Mesh | `StaticNodeQueryProvider` | **No walks needed** — iterates the in-memory static catalog directly. |
| Per-provider (SQL) | `PostgreSqlMeshQuery` + `PostgreSqlSqlGenerator` | **Yes — pushed down to SQL.** `path LIKE '<prefix>/%'` on the indexed `path` column for `descendants` / `subtree`; `namespace = <basePath>` for `children`; in-memory ancestor split + `IN`-clause for `ancestors`. |
| Per-provider (SQL) | `CosmosMeshQuery` + `CosmosSqlGenerator` | **Yes — pushed down to Cosmos SQL** via `CosmosStorageAdapter.QueryNodesAsync`. |
| Per-provider (pedestrian) | `StorageAdapterMeshQueryProvider` (in-memory, file-system, embedded-resource) | **Yes — composed against `IStorageAdapter.ListChildPaths` in `IObservable` form.** One instance per `IStorageAdapter` (i.e. per partition in routed setups). |

Adding a new backend (e.g. blob storage) is local — implement `IMeshQueryProvider` once, with whatever native pushdown the backend supports. The mesh layer is unchanged. Likewise, when something feels like it belongs at the mesh layer ("discover all partitions", "find nodes matching X across the whole mesh"), it goes in `RoutingMeshQueryProvider` — never into a per-adapter walker.

**Autocomplete follows the same rule.** Per-adapter `AutocompleteAsync` consumes the QUERY stream (already-populated `MeshNode`s) and scores against the prefix — it never reads paths by hand. Discovering partitions when `basePath` is empty is `RoutingMeshQueryProvider.AutocompleteAsync`'s job.

### GUI-side single-node reads — always through `IMeshNodeStreamCache`

On the server side, `workspace.GetMeshNodeStream(path)` is the canonical single-node read primitive. On the **GUI** side (Blazor views), the equivalent is `IMeshNodeStreamCache.GetStream(path)` — a process-wide shared handle per path, opened once under `ImpersonateAsSystem`, replayed and live-connected. Every visible Blazor view that needs the same node joins the same upstream subscription; writes through `cache.Update(path, fn)` propagate to all subscribers in order.

Going around the cache (e.g. opening `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, ...)` directly inside a Blazor view) opens a **separate** upstream handle — writes through one are invisible to readers of the other, and the per-view subscription cost scales with the number of visible views. Always use the cache.

The list-rendering shape (one Blazor view per id, each binding to its own cache stream) is documented separately: **[Item-Template + MeshNode Stream Binding](xref:GUI/ItemTemplateMeshNodeStreamBinding)**. The canonical example is the thread chat view — N visible messages, N cache subscriptions, zero per-message layout-area round-trips.

---

## One-shot reads (`GetDataRequest` + `Observe`)

The canonical pattern for "give me this node's current MeshNode right now":

```csharp
var delivery = hub.Post(
    new GetDataRequest(new MeshNodeReference()),
    o => o.WithTarget(new Address(path)));

hub.Observe(delivery, (d, _) =>
{
    if (d is IMessageDelivery<GetDataResponse> response
        && response.Message.Data is MeshNode node)
    {
        // Use node.Content, node.Version, etc. — authoritative, no lag.
    }
    return Task.FromResult(d);
}, cancellationToken);
```

No `Query`, no `await`, no `FromAsync` bridge. The target hub activates on receipt of the message, responds with a `GetDataResponse` wrapping the current `MeshNode`, and your callback fires.

---

## Live updates (`GetRemoteStream`)

Use `GetRemoteStream` when you want to *react* to writes — render a view, wait for a job to finish, watch progress roll in.

```csharp
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(jobPath), new MeshNodeReference())
    .Where(change =>
        change.Value?.Content is JobStatus { State: "Done" or "Failed" })
    .Take(1)
    .Subscribe(final =>
        logger.LogInformation("Job finished: {State}",
            ((JobStatus)final.Value!.Content!).State));
```

The first emission is the current state; subsequent emissions arrive as the hub applies writes. `Where(...).Take(1)` waits until a condition is true and then completes — no polling loop, no `Task.Delay`.

---

## Writes (`PatchDataChangeRequest`)

Writes flow to the owning hub as data changes, not as node CRUD:

```csharp
hub.Post(
    new PatchDataChangeRequest(
        StreamId: targetAddress.ToString(),
        Version: expectedVersion,
        Change: new RawJson(patchJson),
        ChangeType: ChangeType.Patch,
        ChangedBy: userId),
    o => o.WithTarget(targetAddress));
```

Never go through `mesh.QueryAsync` + merge in memory + `mesh.UpdateNode`. The index read is stale; the merge loses concurrent writes; the full-node replace overwrites anything you didn't explicitly read. Let the owning hub apply the patch on its authoritative state.

For full-node updates use `DataChangeRequest.WithUpdates(fullNode)`.

---

## Upserts (`CreateOrUpdateNodeRequest`) — single verb, no delete-then-create

When the caller has the **full target shape** and wants the node to land regardless of whether it already exists (copy / move / import / agentic write-back), use the single-verb upsert:

```csharp
hub.Observe<CreateOrUpdateNodeResponse>(
        new CreateOrUpdateNodeRequest(targetNode))
    .FirstAsync()
    .Select(d => d.Message)
    .Subscribe(resp =>
    {
        if (!resp.Success) { /* resp.Log + resp.Error */ return; }
        // resp.WasCreated tells you create-vs-update; resp.Log carries audit.
    });
```

**Why a dedicated verb instead of chaining a create and an update yourself:**

- The caller doesn't need to check existence — the handler reads persistence and either dispatches `CreateNodeRequest` (when missing) or applies the update branch internally via `stream.Update` (when existing). One audit log. One response shape (`CreateOrUpdateNodeResponse` with `WasCreated`).
- **Never delete-then-create.** That pattern races the per-node hub's disposal — a `GetNode` issued shortly after the create returns null because the new request hits the still-tearing-down hub. The upsert handler applies the update via `stream.Update` instead, which routes the merge patch to the live owning hub and keeps `GetNode` consistent.
- **Permissions stay specific.** Missing target = `Permission.Create` checked by the inner `CreateNodeRequest`. Existing target = `Permission.Update`, re-validated on the patch path (`RlsDataValidator`) that `stream.Update` routes to the owning hub. The upsert request itself declares both via `[CreateOrUpdateNodePermission]` so the routing-layer gate still denies callers that have neither.
- **Patch mode is reserved** for incremental edits (log-line append, view-count bump, status flip): set `request.Patch` to a `Json.Patch.JsonPatch` payload. The handler will apply the patch to the existing node (or to `Node` as the seed when missing) and write the result. (Currently surface-only — patch mode lands when its caller does.)

Bulk upserts (e.g. node-tree copy) compose the per-node observable and merge with bounded concurrency so a wide subtree doesn't open every per-node hub simultaneously on the receiving side:

```csharp
allNodes
    .Select(node => hub.Observe<CreateOrUpdateNodeResponse>(
            new CreateOrUpdateNodeRequest(BuildTarget(node)))
        .FirstAsync()
        .Select(d => d.Message.Success ? 1 : 0))
    .ToObservable()
    .Merge(maxConcurrent: 16)
    .Sum();
```

`NodeCopyHelper.CopyNodeTree` is the canonical example — `force=false` routes through `CreateNodeRequest` (skip-on-exists), `force=true` routes through `CreateOrUpdateNodeRequest` (always upsert). The same shape applies to import, mirror, and any future "write a batch of MeshNodes from an external source" flow.

---

## Operations — named request types per intent

When you want to **do** something on a node (rather than read or write its content), define a named request type and handle it on the owning hub. The caller never sees the implementation detail.

**Example — run a script on a Code node.** The caller doesn't know (or need to know) that the Code hub dispatches to an internal kernel:

```csharp
// In MeshWeaver.Mesh.Contract — no MeshWeaver.Kernel reference!
public record ExecuteScriptRequest : IRequest<ExecuteScriptResponse>
{
    public string? SubmissionId { get; init; }
}

public record ExecuteScriptResponse
{
    public bool Success { get; init; }
    public string? SubmissionId { get; init; }
    public string? OutputAreaReference { get; init; }
    public string? Error { get; init; }
}
```

The Code node's hub registers a **synchronous** handler:

```csharp
// In CodeNodeType.HubConfiguration
config.WithHandler<ExecuteScriptRequest>(HandleExecuteScript)

private static IMessageDelivery HandleExecuteScript(
    IMessageHub hub, IMessageDelivery<ExecuteScriptRequest> request)
{
    // Synchronous workspace read — .Current is the latest committed state.
    var node = hub.GetWorkspace().GetStream(new MeshNodeReference())?.Current?.Value;
    if (node?.Content is not CodeConfiguration code || !code.IsExecutable)
    {
        hub.Post(new ExecuteScriptResponse { Success = false, Error = "..." },
            o => o.ResponseFor(request));
        return request.Processed();
    }

    var submissionId = request.Message.SubmissionId ?? Guid.NewGuid().ToString("N");
    var kernelAddress = /* private — derived from hub.Address */;

    // Fire-and-forget dispatch to the (private) kernel.
    hub.Post(new SubmitCodeRequest(code.Code ?? "") { Id = submissionId },
        o => o.WithTarget(kernelAddress));

    hub.Post(new ExecuteScriptResponse
        {
            Success = true,
            SubmissionId = submissionId,
            OutputAreaReference = submissionId
        },
        o => o.ResponseFor(request));
    return request.Processed();
}
```

The caller fires the request at the node and subscribes for progress:

```csharp
var delivery = hub.Post(
    new ExecuteScriptRequest(),
    o => o.WithTarget(new Address(codeNodePath)));

hub.Observe(delivery, (d, _) =>
{
    if (d is IMessageDelivery<ExecuteScriptResponse> resp && resp.Message.Success)
    {
        // Subscribe to the output area for progress — still no direct kernel reference.
        workspace.GetRemoteStream<UiControl, LayoutAreaReference>(
            new Address(codeNodePath),
            new LayoutAreaReference(resp.Message.OutputAreaReference!))
            .Subscribe(/* ... */);
    }
    return Task.FromResult(d);
});
```

**Rules for operation handlers:**

- Synchronous. No `.Subscribe` on a stream, no `await`, no `Observable.FromAsync`. Read `.Current?.Value` from the workspace stream (it's already populated at handler-invocation time).
- The target address is the **node** (`new Address(nodePath)`), never the implementation detail (kernel, persistence, etc.).
- The response is a *dispatch acknowledgement*, not a completion signal. For long-running work, expose an `OutputAreaReference` and let the caller subscribe via `GetRemoteStream`.

---

## Handlers: reactive chains, not `.Current`

Inside a `.WithHandler<TRequest>(...)` body the handler must not block. State is read **reactively** — compose with `.Select(...)` / `.Where(...)` / `.Take(1)` / `.Subscribe(...)`. The `Subscribe` callback fires once the stream emits; the handler returns `request.Processed()` immediately and the callback later posts the actual response via `hub.Post(response, o => o.ResponseFor(request))`.

**Never `.Current` / `.Current?.Value` on a stream.** `Current` is populated after the stream has emitted its first value — inside a handler that just triggered the hub's activation, the workspace hasn't loaded data yet and `Current` is null. You will ship a wrong answer. The reactive chain avoids this: `Subscribe` fires once the data is actually there.

```csharp
// ❌ NEVER
var node = hub.GetWorkspace().GetStream(new MeshNodeReference())?.Current?.Value;

// ✅ ALWAYS
hub.GetWorkspace().GetStream(new MeshNodeReference())
    ?.Select(change => change.Value)
    .Where(node => node is not null)
    .Take(1)
    .Subscribe(node =>
    {
        // handler logic here — post the response inside this callback
        hub.Post(new MyResponse { /* ... */ }, o => o.ResponseFor(request));
    });
return request.Processed();   // handler returns immediately
```

| Inside a handler | OK? |
|---|---|
| `hub.Post(...)` — fire a message | ✅ sync |
| `hub.Observe(delivery, callback)` — register; callback fires later | ✅ sync |
| `workspace.UpdateMeshNode(fn)` — apply an update | ✅ sync |
| `hub.GetWorkspace().GetStream(ref)?.Select(...).Where(...).Take(1).Subscribe(...)` — reactive read | ✅ |
| `hub.GetWorkspace().GetStream(ref)?.Current?.Value` — snapshot read | ❌ null on cold workspaces |
| `await anything` | ❌ never |
| `Observable.FromAsync(...)` | ❌ hides an await — same bug |

---

## Quick decision matrix

| Intent | Primitive |
|---|---|
| List nodes under X (paths / metadata only) | `mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(...))` — project to `Path` / `Name` / etc. **never read `.Content`** |
| Does node X exist? | `Query` + check `Items.Count` |
| Give me node X's MeshNode (live) | `workspace.GetMeshNodeStream(X)` — the **only** non-stale read path |
| Give me node X's MeshNode (once) | `hub.Post(GetDataRequest(new MeshNodeReference()), WithTarget(X))` + `Observe` |
| Keep me updated on node X's MeshNode | `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(X, new MeshNodeReference())` |
| Patch node X | `hub.Post(PatchDataChangeRequest(...), WithTarget(X))` |
| Replace node X wholesale | `hub.Post(DataChangeRequest{...}.WithUpdates(fullNode), WithTarget(X))` |
| Run the script on Code node X | `hub.Post(ExecuteScriptRequest(), WithTarget(X))` + `Observe<ExecuteScriptResponse>` |
| Wait until the run finishes | `workspace.GetRemoteStream` on X's output area until a terminal condition |
| Move/Copy node X (incl. subtree) | `hub.Post(MoveNodeRequest / CopyNodeRequest, WithTarget(X))` — owning hub reads its own state via `GetMeshNodeStream`, fans out per-child requests, never queries for content |
| Delete node X (incl. subtree) | `hub.Post(DeleteNodeRequest, WithTarget(X))` — recursive variant queries for **paths only** then fires one `DeleteNodeRequest` per descendant address |
| Stream content into node X during execution (AI streaming, long-running output) | Open `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(X, new MeshNodeReference())` once at start, push every delta via `.Update(node => node with { Content = ... })`, dispose at end. See [Thread Execution Streaming](xref:Architecture/ThreadExecutionStreaming) for the canonical writer + renderer pair. |

---

## Anti-patterns

```csharp
// ❌ Query to get content — stale read, lost-update risk.
var node = await mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();
return JsonSerializer.Serialize(node);

// ❌ Same in reactive clothing — still a query, still stale.
return mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
    .Take(1).Select(c => c.Items.FirstOrDefault());

// ❌ Reading Content while enumerating a query result — Content is stale.
await foreach (var n in mesh.QueryAsync<MeshNode>($"namespace:{parent} scope:subtree"))
{
    if (n.Content is JobStatus { State: "Done" }) { … }   // ← stale Content
}

// ❌ Wrapping QueryAsync in Observable.FromAsync does not fix consistency.
return Observable.FromAsync(ct =>
    mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync(ct).AsTask());

// ❌ "Recursive operation" by loading every subtree node from a query.
//    Stale Content + N+1 + memory blow-up + bypasses per-node hub validators.
await foreach (var n in mesh.QueryAsync<MeshNode>(
    $"namespace:{root} scope:subtree"))
{
    storage.DeleteAsync(n.Path);            // ← uses stale n; bypasses hub
}

// ❌ Caller addressing the implementation detail (kernel) directly.
hub.Post(new SubmitCodeRequest(...), o => o.WithTarget(kernelAddress));

// ❌ Async in a handler body.
.WithHandler<FooRequest>(async (hub, req) => { await something; return req.Processed(); })

// ✅ Project to metadata only — `.Path` / `.Name` / `.NodeType`, never `.Content`.
await foreach (var shell in mesh.QueryAsync<MeshNode>(
    $"namespace:{parent} scope:subtree"))
    paths.Add(shell.Path);                  // never read shell.Content

// ✅ Need content for a known path? Subscribe to the owning hub.
workspace.GetMeshNodeStream(path)
    .Take(1)
    .Subscribe(node => { /* node.Content is live, no lag */ });

// ✅ Recursive operation — fan out one request per descendant address;
//    each owning hub does the work with its own live state.
Observable.Merge(paths.Select(p =>
        hub.Observe(new DeleteNodeRequest(p), o => o.WithTarget(new Address(p)))))
    .Subscribe(_ => { }, err => logger.LogError(err, "delete fan-out failed"));

// ✅ One-shot content read — authoritative.
var delivery = hub.Post(new GetDataRequest(new MeshNodeReference()),
    o => o.WithTarget(new Address(path)));
hub.Observe(delivery, (d, _) => { /* ... */ return Task.FromResult(d); });

// ✅ Live updates.
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
    new Address(path), new MeshNodeReference());

// ✅ Named operation — caller never references the kernel.
hub.Post(new ExecuteScriptRequest(), o => o.WithTarget(new Address(codeNodePath)));
```

---

## Related reading

- [Asynchronous Calls](AsynchronousCalls) — the hub's single-threaded scheduler and why `await` deadlocks it.
- [Workspace references](WorkspaceReferences) — catalogue of `WorkspaceReference<T>` shapes and what each one emits.
- [Data access patterns](DataAccessPatterns) — which DI service to use for what.
