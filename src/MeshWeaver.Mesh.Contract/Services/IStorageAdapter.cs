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
/// (HTTP, filesystem) do the <c>Observable.FromAsync</c> bridge inside the
/// adapter — never above this line.
/// </para>
/// </summary>
public interface IStorageAdapter
{
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
    /// </summary>
    IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options);

    /// <summary>Deletes a node from storage and emits the deleted path.</summary>
    IObservable<string> Delete(string path);

    /// <summary>
    /// Lists child paths under a parent path.
    /// Returns both node paths (records present at that level) and directory paths
    /// (intermediate folders that have nodes under them but no node at the folder level).
    /// </summary>
    /// <param name="parentPath">Parent path (empty/null for root level).</param>
    IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath);

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
