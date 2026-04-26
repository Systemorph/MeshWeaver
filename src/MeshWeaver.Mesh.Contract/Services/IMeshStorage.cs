using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor")]
[assembly: InternalsVisibleTo("MeshWeaver.Blazor.Portal")]

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Persistence service for MeshNode instances in a hierarchical graph structure.
/// The graph root is at address "_graph" (path "/").
/// Each path segment manages its children (segment1 manages segment1/*, etc.)
/// This is the scoped wrapper that automatically injects JsonSerializerOptions from IMessageHub.
/// </summary>
internal interface IMeshStorage
{
    /// <summary>
    /// Gets a node by its path. Returns an observable that emits the node (or null
    /// if not found) and completes. The Task → IObservable bridge lives inside the
    /// implementation so callers compose with <c>SelectMany</c>/<c>Subscribe</c>
    /// instead of bridging at every call site.
    /// </summary>
    /// <param name="path">The node path (e.g., "org/acme/project/web")</param>
    /// <returns>Observable emitting the node or null</returns>
    IObservable<MeshNode?> GetNode(string path);

    /// <summary>
    /// Gets all child nodes at the specified parent path.
    /// </summary>
    /// <param name="parentPath">Parent path (empty or null for root level)</param>
    /// <returns>Async enumerable of child nodes</returns>
    IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath);

    /// <summary>
    /// Gets all descendant nodes under the specified path.
    /// </summary>
    /// <param name="parentPath">Parent path</param>
    /// <returns>Async enumerable of all descendant nodes</returns>
    IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath);

    /// <summary>
    /// Gets ALL descendant nodes including satellites (nodes where
    /// <c>MainNode != Path</c>). Default implementation delegates to
    /// <see cref="GetDescendantsAsync"/> which excludes satellites — impls that
    /// know how to include satellites (e.g. the full persistence service)
    /// override this.
    /// </summary>
    IAsyncEnumerable<MeshNode> GetAllDescendantsAsync(string? parentPath)
        => GetDescendantsAsync(parentPath);

    /// <summary>
    /// 🚨 INTERNAL — DO NOT USE FROM APPLICATION / LAYOUT / HUB-HANDLER CODE.
    /// Direct, instant, no-debounce write to the underlying storage. Bypasses
    /// the workspace's data layer (no in-memory state update, no DataChange fan-out,
    /// no MeshChangeFeed publish, no validators, no access control).
    /// <para>
    /// The ONLY legitimate callers are the framework's <c>HandleCreateNodeRequest</c>
    /// and <c>HandleDeleteNodeRequest</c> handlers in <c>MeshExtensions</c>, which
    /// orchestrate validators, change-feed publishing, and the activity log on top
    /// of this raw write.
    /// </para>
    /// <para>
    /// All other callers must go through <see cref="MeshNodeStreamExtensions.UpdateMeshNode"/>
    /// or post a <c>DataChangeRequest</c> / <c>UpdateNodeRequest</c> — the workspace
    /// debounces, validates, fans out, and (for Update) preserves CQRS read-your-writes
    /// semantics through the proper hub channels.
    /// </para>
    /// Don't try this at home — never use this pattern outside the two named handlers.
    /// </summary>
    internal IObservable<MeshNode> SaveNode(MeshNode node);

    /// <summary>
    /// 🚨 INTERNAL — DO NOT USE FROM APPLICATION / LAYOUT / HUB-HANDLER CODE.
    /// Direct, instant, no-debounce delete from the underlying storage. Bypasses
    /// the workspace's data layer entirely (no validators, no change feed, no fan-out).
    /// <para>
    /// The ONLY legitimate caller is the framework's <c>HandleDeleteNodeRequest</c>
    /// handler in <c>MeshExtensions</c>, which orchestrates validators + change-feed
    /// publishing on top of this raw delete.
    /// </para>
    /// <para>
    /// All other callers must post a <c>DeleteNodeRequest</c> to the mesh hub.
    /// </para>
    /// Don't try this at home — never use this pattern outside the named handler.
    /// </summary>
    internal IObservable<string> DeleteNode(string path, bool recursive = false);

    /// <summary>
    /// Moves a node and all its descendants to a new path.
    /// Comments associated with moved nodes are also migrated.
    /// </summary>
    /// <param name="sourcePath">The current node path</param>
    /// <param name="targetPath">The new node path</param>
    /// <returns>Observable emitting the moved node at the new path, or OnError</returns>
    /// <exception cref="InvalidOperationException">If source doesn't exist or target already exists</exception>
    IObservable<MeshNode> MoveNode(string sourcePath, string targetPath);

    /// <summary>
    /// Searches nodes by query text within their Name or Content.
    /// </summary>
    /// <param name="parentPath">Parent path to search under (null for all)</param>
    /// <param name="query">Search query</param>
    /// <returns>Async enumerable of matching nodes</returns>
    IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query);

    /// <summary>
    /// Checks if a node exists at the given path.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the node exists</returns>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Finds the node whose path is the longest prefix of the given full path.
    /// Uses a single query instead of iterating through ancestor paths.
    /// </summary>
    /// <param name="fullPath">The full path to find the best prefix match for</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The matching node and number of matched segments, or (null, 0) if not found</returns>
    Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsync(
        string fullPath, CancellationToken ct = default);

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
    /// <returns>Async enumerable of comments for the node</returns>
    IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath);

    /// <summary>
    /// Adds a comment to a node.
    /// </summary>
    /// <param name="comment">The comment to add</param>
    /// <returns>Observable emitting the saved comment, or OnError</returns>
    IObservable<Comment> AddComment(Comment comment);

    /// <summary>
    /// Deletes a comment by ID.
    /// </summary>
    /// <param name="commentId">The comment ID to delete</param>
    /// <returns>Observable emitting the deleted comment id on completion, or OnError</returns>
    IObservable<string> DeleteComment(string commentId);

    /// <summary>
    /// Gets a single comment by ID.
    /// </summary>
    /// <param name="commentId">The comment ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The comment or null if not found</returns>
    Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default);

    #endregion

    #region Partition Storage

    /// <summary>
    /// Gets all objects from a node's partition folder.
    /// Objects are stored as separate JSON files with $type discriminators.
    /// </summary>
    /// <param name="nodePath">The node path (e.g., "_types/story")</param>
    /// <param name="subPath">Optional sub-path within partition (e.g., "layoutAreas")</param>
    /// <returns>Async enumerable of deserialized objects</returns>
    IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath);

    /// <summary>
    /// Saves objects to a node's partition folder.
    /// Each object is stored as a separate JSON file with $type discriminator.
    /// </summary>
    /// <param name="nodePath">The node path</param>
    /// <param name="subPath">Optional sub-path within partition</param>
    /// <param name="objects">Objects to save</param>
    /// <returns>Observable that signals completion or OnError</returns>
    IObservable<IReadOnlyCollection<object>> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects);

    /// <summary>
    /// Deletes all objects from a node's partition folder (or sub-path).
    /// </summary>
    /// <param name="nodePath">The node path</param>
    /// <param name="subPath">Optional sub-path within partition</param>
    /// <returns>Observable that signals completion or OnError</returns>
    IObservable<string> DeletePartitionObjects(string nodePath, string? subPath = null);

    /// <summary>
    /// Gets the newest modification timestamp across all objects in a partition (or sub-path).
    /// Used for cache invalidation.
    /// </summary>
    /// <param name="nodePath">The node path</param>
    /// <param name="subPath">Optional sub-path within partition</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The newest modification timestamp, or null if no objects exist</returns>
    Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(string nodePath, string? subPath = null, CancellationToken ct = default);

    #endregion

    #region Secure Operations

    /// <summary>
    /// Gets a node by path, applying security filtering.
    /// Returns null if the user doesn't have read permission.
    /// Default implementation delegates to GetNodeAsync (no security filtering).
    /// Use SecurePersistenceServiceDecorator to add security.
    /// </summary>
    /// <param name="path">The node path</param>
    /// <param name="userId">The user's ObjectId (null for anonymous)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The node or null if not found or not authorized</returns>
    Task<MeshNode?> GetNodeSecureAsync(string path, string? userId, CancellationToken ct = default)
        => GetNode(path).FirstAsync().ToTask(ct);

    /// <summary>
    /// Gets child nodes, filtering out those the user cannot read.
    /// Default implementation delegates to GetChildrenAsync (no security filtering).
    /// Use SecurePersistenceServiceDecorator to add security.
    /// </summary>
    /// <param name="parentPath">Parent path (empty or null for root level)</param>
    /// <param name="userId">The user's ObjectId (null for anonymous)</param>
    /// <returns>Async enumerable of accessible child nodes</returns>
    IAsyncEnumerable<MeshNode> GetChildrenSecureAsync(string? parentPath, string? userId)
        => GetChildrenAsync(parentPath);

    /// <summary>
    /// Gets descendant nodes, filtering out those the user cannot read.
    /// Default implementation delegates to GetDescendantsAsync (no security filtering).
    /// Use SecurePersistenceServiceDecorator to add security.
    /// </summary>
    /// <param name="parentPath">Parent path</param>
    /// <param name="userId">The user's ObjectId (null for anonymous)</param>
    /// <returns>Async enumerable of accessible descendant nodes</returns>
    IAsyncEnumerable<MeshNode> GetDescendantsSecureAsync(string? parentPath, string? userId)
        => GetDescendantsAsync(parentPath);

    #endregion
}
