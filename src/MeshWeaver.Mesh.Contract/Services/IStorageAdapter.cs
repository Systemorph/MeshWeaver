using System.Collections.Immutable;
using System.Reactive;
using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Low-level storage adapter for persistence implementations.
/// Abstracts the actual storage mechanism (file system, Cosmos DB, etc.).
///
/// <para>
/// 🚨 API is <see cref="IObservable{T}"/> end-to-end per the "Nothing async ever"
/// rule (<c>Doc/Architecture/AsynchronousCalls.md</c>). No <c>Task&lt;T&gt;</c>,
/// no <c>IAsyncEnumerable&lt;T&gt;</c>, no <c>await</c>. Composable with
/// <c>SelectMany</c>/<c>Subscribe</c>; backends that wrap async leaves
/// (HTTP, filesystem) bridge them through a bounded <c>IIoPool</c> inside the
/// adapter — never above this line, and never via <c>Observable.FromAsync</c>,
/// which is forbidden outside <c>IoPool</c> and deadlocks under a blocking
/// subscriber (see <c>Doc/Architecture/ControlledIoPooling.md</c>).
/// </para>
/// </summary>
public interface IStorageAdapter
{
    /// <summary>
    /// In-process change feed — subscribers receive a notification for every
    /// <see cref="Write"/> and <see cref="Delete"/> this adapter commits.
    /// Used by per-node hubs to reconcile their cached workspace state when
    /// the mesh hub writes storage directly (bypassing per-node hub
    /// <c>stream.Update</c> routing). Default impl returns
    /// <c>Observable.Empty</c>; adapters that mutate state should override
    /// and publish from their Write/Delete.
    /// </summary>
    IObservable<DataChangeNotification> Changes
        => System.Reactive.Linq.Observable.Empty<DataChangeNotification>();

    /// <summary>Reads a node from storage. Emits the node (or null) and completes.</summary>
    IObservable<MeshNode?> Read(string path, JsonSerializerOptions options);

    /// <summary>
    /// Reads multiple nodes from storage in a SINGLE round-trip when the
    /// underlying backend supports it (Postgres batches via
    /// <c>WHERE (namespace, id) IN ((…), (…))</c>). Order is not guaranteed;
    /// missing paths are simply absent from the emitted sequence.
    ///
    /// <para>Default impl falls back to N parallel <see cref="Read"/> calls —
    /// fine for FileSystem / InMemory (they have no per-call latency to
    /// amortise). PostgreSqlStorageAdapter overrides this so multi-path
    /// probes (e.g. the URL resolver's <c>path:a|b|c</c> longest-prefix
    /// search) become ONE PG query instead of N.</para>
    /// </summary>
    IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => System.Reactive.Linq.Observable.Merge(
            paths.Select(p => System.Reactive.Linq.Observable.Select(
                System.Reactive.Linq.Observable.Where(Read(p, options), n => n is not null),
                n => n!)));

    /// <summary>
    /// Writes a node to storage. Emits the written node when this adapter
    /// accepted the path; emits <c>null</c> when the path isn't owned here
    /// so the try-then-claim chain in <c>PersistenceService.Write</c> moves
    /// on to the next writable provider.
    ///
    /// <para>🚨 <b>The write is CONDITIONAL on <see cref="MeshNode.Version"/> wherever the backend can
    /// express the condition (#971).</b> <c>MeshNode.Version</c> is the node's forward-only revision
    /// counter — every mint goes through <see cref="MeshNode.NextVersion"/> (<c>current + 1</c>) and an
    /// unchanged node is re-persisted at the version it already carries — so a correctly-produced write
    /// is ALWAYS at or above the version already stored. A backend that can compare therefore MUST
    /// leave the row untouched when <c>node.Version</c> is strictly BELOW the durable row's, and MUST
    /// report that by emitting the STORED (winning) node instead of the one it was handed. Equal
    /// versions still apply: re-persisting an unchanged node is a legitimate, common shape.</para>
    ///
    /// <para>Why this belongs in the store and not only in a decorator: the in-process high-water
    /// filter in <c>MonotonicWriteGuardStorageAdapter</c> is empty on a freshly started replica, so
    /// that replica's FIRST write to any path has nothing to compare against. Monotonicity is a
    /// property of the ROW, and only the store can enforce it across replicas — a rollout briefly
    /// running two pods, a KEDA scale-out, a restarted replica. The emitted stored node is what lets
    /// the decorator turn the refusal into a MERGE (see <c>MeshNodeConflictMerge</c>) rather than
    /// bouncing a conflict back at a caller that has no retry loop.</para>
    ///
    /// <para>A backend that CANNOT express the condition (single-process file system, a store with no
    /// conditional upsert) simply emits the written node as before; the in-process guard remains its
    /// only protection, which is sound because such backends are not multi-replica.</para>
    ///
    /// <para>A refused write must NOT publish <see cref="Changes"/> — nothing changed, and a
    /// notification carrying the losing node would hand every subscriber the stale state the store
    /// just rejected.</para>
    /// </summary>
    IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options);

    /// <summary>
    /// Writes multiple nodes in as few round-trips as the backend allows, emitting
    /// the nodes this adapter accepted. Mirrors <see cref="ReadMany"/> on the write
    /// side: the default is correct everywhere, and backends that can do better
    /// override it (Postgres windows by target table and sends one
    /// <c>NpgsqlBatch</c> per window).
    ///
    /// <para>🚨 <b>Order is part of the contract.</b> The default writes STRICTLY IN
    /// SEQUENCE — not <c>Merge</c> like <see cref="ReadMany"/> — and an override MUST
    /// preserve the caller's order when publishing <see cref="Changes"/>. Callers
    /// order parents before children on purpose (the installer's <c>CopyOrder</c>):
    /// activating a child's per-node hub while its parent's is still cold is the
    /// race that used to wedge installs. Batching the STORAGE write is safe — a
    /// transaction has no ordering to lose — but the change feed is what wakes the
    /// hubs, so it must still arrive parents-first.</para>
    ///
    /// <para>Nodes this adapter does not own are simply absent from the result, the
    /// same way <see cref="Write"/> emits <c>null</c>, so the try-then-claim chain in
    /// <c>PersistenceService</c> keeps working unchanged.</para>
    /// </summary>
    IObservable<IReadOnlyList<MeshNode>> WriteMany(
        IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
        => System.Reactive.Linq.Observable.Select(
            System.Reactive.Linq.Observable.ToList(
                System.Reactive.Linq.Observable.Select(
                    System.Reactive.Linq.Observable.Where(
                        System.Reactive.Linq.Observable.Concat(
                            nodes.Select(n => Write(n, options))),
                        n => n is not null),
                    n => n!)),
            written => (IReadOnlyList<MeshNode>)written);

    /// <summary>
    /// COMPARE-AND-SET — the atomic "first writer wins" primitive, and the WRITE-side twin of
    /// <see cref="DeleteIfExists"/>. Applies <paramref name="node"/> only while the durable row
    /// still carries <paramref name="expectedVersion"/> (or no row exists at all, when it is
    /// <c>0</c>), and reports which of the two happened:
    ///
    /// <list type="bullet">
    /// <item><c>true</c> — APPLIED. The durable row now holds <paramref name="node"/>.</item>
    /// <item><c>false</c> — REFUSED. Somebody else moved the row (or it is absent when a version
    /// was expected). The caller's intent did NOT land and it must not act as though it did.</item>
    /// <item><c>null</c> — this adapter does not own the path, so the try-then-claim chain in
    /// <c>PersistenceService</c> moves on to the next writable provider. Same "not mine" signal
    /// <see cref="Write"/> gives by emitting <c>null</c>.</item>
    /// </list>
    ///
    /// <para>🚨 <b>Why the ordinary <see cref="Write"/> cannot serve here (#1424).</b> The regular
    /// upsert is version-conditional but NOT exclusive: it applies at EQUAL versions, because
    /// re-persisting an unchanged node is a legitimate, common shape. Two writers that each read the
    /// row at version <c>v</c> and each mint <c>v+1</c> therefore BOTH commit, last-write-wins, and
    /// neither is told it lost — the store returns the node it was handed, so the
    /// <c>saved.Version &gt; written.Version</c> refusal signal never fires. That is exactly how two
    /// Orleans clusters sharing one Postgres database each granted themselves the build claim and each
    /// ran the full bake. Exclusivity needs an equality condition on a version the caller READ, which
    /// is what this method is: at most one of N concurrent callers holding the same
    /// <paramref name="expectedVersion"/> can be told <c>true</c>.</para>
    ///
    /// <para>Backends that can express the condition MUST override — Postgres via
    /// <c>ON CONFLICT … DO UPDATE … WHERE target.version = @expected</c> (and
    /// <c>DO NOTHING</c> for <paramref name="expectedVersion"/> <c>0</c>) plus the row count,
    /// in-memory via <c>TryAdd</c>/<c>TryUpdate</c>. The default below is a NON-ATOMIC
    /// read-compare-write, correct only for single-writer backends (a FileSystem dev host) —
    /// the same contract, and the same caveat, as <see cref="DeleteIfExists"/>.</para>
    ///
    /// <para>🚨 Decorators MUST forward to their inner adapter, or the atomicity is silently lost at
    /// the outermost decorator that falls back to the default — the same forwarding rule as
    /// <see cref="Changes"/>, <see cref="DeleteIfExists"/>, <see cref="ResolvePath"/> and
    /// <see cref="ListDescendantPaths"/>.</para>
    /// </summary>
    IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
        => System.Reactive.Linq.Observable.SelectMany(
            System.Reactive.Linq.Observable.Take(Read(node.Path, options), 1),
            stored => (stored?.Version ?? 0) != expectedVersion
                ? System.Reactive.Linq.Observable.Return<bool?>(false)
                : System.Reactive.Linq.Observable.Select(
                    Write(node, options), written => written is null ? (bool?)null : true));

    /// <summary>Deletes a node from storage and emits the deleted path.</summary>
    IObservable<string> Delete(string path);

    /// <summary>
    /// Deletes a node if present, emitting whether THIS call removed a stored
    /// node — the atomic "first delete wins" primitive that multi-replica
    /// single-use consumers (OAuth authorization codes) gate on. Backends that
    /// can observe the outcome atomically MUST override: Postgres via the
    /// DELETE row count, in-memory via <c>TryRemove</c>. The default emits
    /// <c>true</c> unconditionally (non-strict) — acceptable only for
    /// single-instance backends (FileSystem dev hosts). Decorators MUST
    /// forward to their inner adapter so strict semantics survive the chain.
    /// </summary>
    IObservable<bool> DeleteIfExists(string path)
        => System.Reactive.Linq.Observable.Select(Delete(path), _ => true);

    /// <summary>
    /// 🚨 PRE-FLIGHT for <see cref="Delete"/> — names the READ-ONLY storage provider that makes
    /// <paramref name="path"/> structurally undeletable, or <c>null</c> when a delete is
    /// unobstructed. This is the question <see cref="Delete"/> itself answers at COMMIT time,
    /// exposed so a caller can ask it BEFORE it starts removing anything (#1433).
    ///
    /// <para><b>Why it exists.</b> A composite adapter reads across every provider (read-only ones
    /// included) but can only delete through the WRITABLE ones, so a path served solely by a
    /// read-only provider passes an existence check and then cannot be committed. In a RECURSIVE
    /// delete that is not a cosmetic mismatch: <c>HierarchicalPathDeletion</c> walks bottom-up, so
    /// by the time the undeletable root is reached its writable descendants are already gone —
    /// the subtree is destroyed and the operation still fails. Asking first is what keeps the
    /// gate and the commit looking at the same thing.</para>
    ///
    /// <para>Non-null means REFUSE — it never means "delete it some other way". Nothing here
    /// widens what a delete removes; a read-only provider is never asked to delete.</para>
    ///
    /// <para>The default is <c>null</c>: a single-store adapter has no read-only provider behind
    /// it, so nothing can block a delete that its own <see cref="Delete"/> would not already
    /// refuse. That default cannot open a hole — <see cref="Delete"/> remains the authority and
    /// still refuses at commit; this only moves an inevitable refusal earlier. Decorators MUST
    /// forward to their inner adapter (same rule as <see cref="ListDescendantPaths"/>).</para>
    /// </summary>
    /// <param name="path">The path a delete is being considered for.</param>
    /// <returns>The blocking read-only provider's name, or <c>null</c> when nothing blocks.</returns>
    IObservable<string?> FindDeleteBlockingProvider(string path)
        => System.Reactive.Linq.Observable.Return<string?>(null);

    /// <summary>
    /// Lists child paths under a parent path.
    /// Returns both node paths (records present at that level) and directory paths
    /// (intermediate folders that have nodes under them but no node at the folder level).
    /// </summary>
    /// <param name="parentPath">Parent path (empty/null for root level).</param>
    IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath);

    /// <summary>
    /// Enumerates every STRICT descendant node path under <paramref name="rootPath"/>
    /// (the root itself is excluded), straight from storage — the AUTHORITATIVE
    /// subtree enumeration the recursive-delete planner and its post-delete
    /// verification are built on (issue #839: planning off the eventually-consistent
    /// query catalog let stale/late rows survive a "successful" recursive delete).
    /// Emits one complete snapshot and completes.
    ///
    /// <para>The default walks <see cref="ListChildPaths"/> level by level, recursing
    /// into node paths AND directory paths — correct for backends whose child listing
    /// surfaces intermediate directory levels (FileSystem, InMemory, Caching).
    /// Backends whose listing is a flat single-level row scan (Postgres and friends)
    /// MUST override with a native prefix enumeration across every table of the
    /// partition, and decorators MUST forward to their inner adapter so the native
    /// override survives the chain (same rule as <see cref="ResolvePath"/>).</para>
    /// </summary>
    IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => System.Reactive.Linq.Observable.Select(
            WalkDescendantPaths(this, rootPath),
            set => (IReadOnlyCollection<string>)set.ToImmutableList());

    /// <summary>
    /// Level-by-level descendant walk over <see cref="ListChildPaths"/>: node paths at
    /// each level are collected, and BOTH node paths and directory paths are recursed
    /// into (a node can have children of its own; a directory is a node-less
    /// intermediate level that still anchors real descendants).
    /// </summary>
    private static IObservable<System.Collections.Immutable.ImmutableHashSet<string>> WalkDescendantPaths(
        IStorageAdapter adapter, string parentPath)
        => System.Reactive.Linq.Observable.SelectMany(
            System.Reactive.Linq.Observable.Take(adapter.ListChildPaths(parentPath), 1),
            level =>
            {
                var nodes = (level.NodePaths ?? Enumerable.Empty<string>())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                var recurseInto = nodes.Union(
                    (level.DirectoryPaths ?? Enumerable.Empty<string>())
                        .Where(p => !string.IsNullOrEmpty(p)));
                if (recurseInto.Count == 0)
                    return System.Reactive.Linq.Observable.Return(nodes);
                return System.Reactive.Linq.Observable.Aggregate(
                    System.Reactive.Linq.Observable.Merge(
                        recurseInto.Select(child => WalkDescendantPaths(adapter, child))),
                    nodes,
                    (acc, sub) => acc.Union(sub));
            });

    /// <summary>Existence check for a single node path.</summary>
    IObservable<bool> Exists(string path);

    /// <summary>
    /// Finds the node whose path is the longest prefix of the given full path.
    /// For example, given "Organization/acme/Settings", finds "Organization/acme" if it exists.
    /// Default impl emits (null, 0) — caller falls back to iterative lookup.
    /// </summary>
    IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => System.Reactive.Linq.Observable.Return<(MeshNode?, int)>((null, 0));

    /// <summary>
    /// Resolves the closest-matching MeshNode for <paramref name="fullPath"/>
    /// across EVERY table in the partition's schema (primary <c>mesh_nodes</c>
    /// plus each satellite table named in
    /// <see cref="PartitionDefinition.TableMappings"/>) in a SINGLE round-trip
    /// to the underlying store. Returns the deepest path-prefix match across
    /// all tables; if no row matches, returns <c>(null, 0)</c>.
    ///
    /// <para>The caller (path-resolution layer) is responsible for the
    /// out-of-band fallbacks: configuration nodes, partition-root virtual
    /// node, static-provider nodes. Those are pure in-memory and don't
    /// belong in storage.</para>
    ///
    /// <para>Default implementation delegates to
    /// <see cref="FindBestPrefixMatch"/> — sufficient for backends with a
    /// single physical table per partition (FileSystem, InMemory). Postgres
    /// overrides this with a UNION across primary + satellites so the same
    /// one-query contract holds when satellites carry the deepest match.</para>
    /// </summary>
    IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => FindBestPrefixMatch(fullPath, options);

    /// <summary>
    /// Lists partition sub-paths for a node (subdirectories that contain partition data,
    /// not child nodes). E.g. "Source", "layoutAreas".
    /// </summary>
    IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => System.Reactive.Linq.Observable.Return(Enumerable.Empty<string>());

    #region Partition Storage

    /// <summary>Enumerates partition objects under a node's partition folder. Hot per emission; completes when exhausted.</summary>
    IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options);

    /// <summary>Saves objects to a node's partition folder. Emits once and completes.</summary>
    IObservable<Unit> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options);

    /// <summary>Deletes objects under a node's partition folder (or sub-path). Emits once and completes.</summary>
    IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null);

    /// <summary>Newest timestamp across objects in a partition (or sub-path); null if empty.</summary>
    IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null);

    #endregion
}

/// <summary>
/// Marker capability: the adapter's matching <c>IMeshQueryProvider</c>
/// (PostgreSqlMeshQuery, CosmosMeshQuery, …) answers Children / Descendants /
/// Subtree / Hierarchy / source:activity queries with a single round-trip via
/// a scope-clause or satellite JOIN. When the adapter implements this marker,
/// the pedestrian <c>StorageAdapterMeshQueryProvider</c> skips its
/// ListChildPaths-walk + per-path Read fallback for those scopes — that walk
/// is N+1 duplicate work running in parallel with the optimized provider.
/// <para>FileSystem / InMemory adapters do NOT implement this and continue to
/// rely on the pedestrian walk.</para>
/// </summary>
public interface IScopedQueryStorageAdapter : IStorageAdapter
{
}
