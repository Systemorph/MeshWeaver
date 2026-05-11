using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Reactive;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Core persistence service for MeshNode instances in a hierarchical graph structure.
/// The graph root is at address "_graph" (path "/").
/// Each path segment manages its children (segment1 manages segment1/*, etc.)
/// This is the internal interface that accepts JsonSerializerOptions per method.
/// Use IMeshStorage for the scoped wrapper that injects options automatically.
/// </summary>
internal interface IStorageService
{
    /// <summary>
    /// Gets a node by its path. Returns an observable that emits the node (or null
    /// if not found) and completes. The Task → IObservable bridge lives inside the
    /// implementation so callers compose with <c>SelectMany</c>/<c>Subscribe</c>
    /// instead of bridging at every call site.
    /// </summary>
    /// <param name="path">The node path (e.g., "org/acme/project/web")</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Observable emitting the node or null</returns>
    IObservable<MeshNode?> GetNode(string path, JsonSerializerOptions options);

    // GetChildren / GetChildrenAsync / GetAllChildrenAsync / GetDescendantsAsync
    // / GetAllDescendantsAsync / GetChildrenSecure / GetDescendantsSecure / SearchAsync
    // (the MeshNode-returning forms) all deleted in the persistence-layer cull
    // (2026-05-11). The routing layer holds no enumeration concept at all.
    // Application code uses `workspace.GetQuery(id, queries…)` per
    // `Doc/Architecture/SyncedMeshNodeQueries.md`; recursive hub operations
    // (Copy/Move/Delete) fan out per-node requests; backend-specific descendant
    // walks live in `SimpleMeshNodeStorage` (for pedestrian adapters: in-memory,
    // file-system, embedded resources) — exposed through a separate
    // `IMeshQueryProvider` implementation, never through this interface.
    // Postgres deployments route through PostgreSqlMeshQuery's SQL pushdown.

    /// <summary>
    /// Creates or updates a node. Cold IObservable — Subscribe triggers the write.
    /// Composes via SelectMany; never awaits a Task internally between layers.
    /// The Task→IObservable bridge sits at the leaf <see cref="IStorageAdapter"/>
    /// boundary, scheduled on TaskPool, so the write never runs on a hub scheduler.
    /// </summary>
    IObservable<MeshNode> SaveNode(MeshNode node, JsonSerializerOptions options);

    /// <summary>
    /// Deletes a node and optionally its descendants. Cold IObservable —
    /// Subscribe triggers the delete.
    /// </summary>
    IObservable<string> DeleteNode(string path, bool recursive = false);

    /// <summary>
    /// Moves a node and all its descendants to a new path. Cold IObservable —
    /// Subscribe triggers the move. Comments associated with moved nodes are
    /// also migrated. Errors flow via OnError (e.g. source doesn't exist or
    /// target already exists).
    /// </summary>
    IObservable<MeshNode> MoveNode(string sourcePath, string targetPath, JsonSerializerOptions options);

    // SearchAsync deleted with the rest of the "load all" surface.
    // Use `workspace.GetQuery(id, queryString)` (synced) for any user-facing search.

    /// <summary>
    /// Checks if a node exists at the given path. Cold IObservable — Subscribe
    /// triggers the read.
    /// </summary>
    IObservable<bool> Exists(string path);

    /// <summary>
    /// Finds the node whose path is the longest prefix of the given full path.
    /// Uses a single query instead of iterating through ancestor paths. Cold
    /// IObservable — Subscribe triggers the read. Default impl emits (null, 0).
    /// </summary>
    IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => System.Reactive.Linq.Observable.Return<(MeshNode?, int)>((null, 0));

    /// <summary>
    /// Initializes the persistence service.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    Task InitializeAsync(CancellationToken ct = default);

    #region Comments

    /// <summary>
    /// Gets all comments for a node.
    /// </summary>
    /// <param name="nodePath">Path of the node</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Async enumerable of comments for the node</returns>
    IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath, JsonSerializerOptions options);

    /// <summary>
    /// Adds a comment to a node. Cold IObservable — Subscribe triggers the write.
    /// </summary>
    IObservable<Comment> AddComment(Comment comment, JsonSerializerOptions options);

    /// <summary>
    /// Deletes a comment by ID. Cold IObservable — Subscribe triggers the delete.
    /// </summary>
    IObservable<string> DeleteComment(string commentId);

    /// <summary>
    /// Gets a single comment by ID. Cold IObservable — Subscribe triggers the
    /// read. Emits null if not found.
    /// </summary>
    IObservable<Comment?> GetComment(string commentId);

    #endregion

    #region Partition Storage

    /// <summary>
    /// Gets all objects from a node's partition folder.
    /// Objects are stored as separate JSON files with $type discriminators.
    /// </summary>
    /// <param name="nodePath">The node path (e.g., "_types/story")</param>
    /// <param name="subPath">Optional sub-path within partition (e.g., "layoutAreas")</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Async enumerable of deserialized objects</returns>
    IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath, JsonSerializerOptions options);

    /// <summary>
    /// Saves objects to a node's partition folder. Cold IObservable — Subscribe
    /// triggers the write. Each object is stored as a separate record with $type
    /// discriminator.
    /// </summary>
    IObservable<IReadOnlyCollection<object>> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options);

    /// <summary>
    /// Deletes all objects from a node's partition folder (or sub-path). Cold
    /// IObservable — Subscribe triggers the delete.
    /// </summary>
    IObservable<string> DeletePartitionObjects(string nodePath, string? subPath = null);

    /// <summary>
    /// Gets the newest modification timestamp across all objects in a partition
    /// (or sub-path). Used for cache invalidation. Cold IObservable — Subscribe
    /// triggers the read.
    /// </summary>
    IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null);

    #endregion

    #region Secure Operations

    /// <summary>
    /// Gets a node by path, applying security filtering.
    /// Emits null if the user doesn't have read permission.
    /// Default implementation delegates to <see cref="GetNode"/> (no security filtering).
    /// Use SecurePersistenceServiceDecorator to add security.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="userId">The user's ObjectId (null for anonymous)</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>Observable emitting the node, or null if not found / not authorized</returns>
    IObservable<MeshNode?> GetNodeSecure(string path, string? userId, JsonSerializerOptions options)
        => GetNode(path, options);

    // GetChildrenSecure / GetDescendantsSecure deleted with the rest of the
    // "load all" surface. Permission-filtered listing is done via
    // `workspace.GetQuery(id, query)` — the synced-query engine pushes RLS
    // into the underlying provider.

    #endregion
}
