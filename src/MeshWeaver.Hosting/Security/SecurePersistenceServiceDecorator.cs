using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Security;

/// <summary>
/// Decorator that adds security filtering to IMeshStorage operations.
/// Implements the secure query methods by delegating to the inner service
/// and filtering results based on user permissions.
/// </summary>
internal class SecurePersistenceServiceDecorator : IStorageService
{
    private readonly IStorageService _inner;
    private readonly Lazy<ISecurityService> _securityService;
    private readonly ILogger<SecurePersistenceServiceDecorator> _logger;
    private readonly Dictionary<string, INodeTypeAccessRule> _nodeTypeAccessRules;

    public SecurePersistenceServiceDecorator(
        IStorageService inner,
        Lazy<ISecurityService> securityService,
        ILogger<SecurePersistenceServiceDecorator> logger,
        IEnumerable<INodeTypeAccessRule>? nodeTypeAccessRules = null)
    {
        _inner = inner;
        _securityService = securityService;
        _logger = logger;
        _nodeTypeAccessRules = (nodeTypeAccessRules ?? [])
            .Where(r => r.SupportedOperations.Contains(NodeOperation.Read))
            .ToDictionary(r => r.NodeType, StringComparer.OrdinalIgnoreCase);
    }

    private ISecurityService SecurityService => _securityService.Value;

    #region Secure Operations

    /// <summary>
    /// NodeType definitions are always publicly readable.
    /// </summary>
    private static bool IsPubliclyReadable(MeshNode node)
        => node.NodeType == MeshNode.NodeTypePath;

    /// <summary>
    /// Checks if a user has read access to a node.
    /// Checks in order: publicly readable → INodeTypeAccessRule → ISecurityService permissions.
    /// Returns an IObservable&lt;bool&gt; — composes reactively with the secure-read pipeline.
    /// </summary>
    private IObservable<bool> HasReadAccess(MeshNode node, string? userId)
    {
        if (IsPubliclyReadable(node))
            return Observable.Return(true);

        IObservable<bool> permissionCheck = string.IsNullOrEmpty(userId)
            ? SecurityService.HasPermission(node.Path, Permission.Read)
            : SecurityService.HasPermission(node.Path, userId, Permission.Read);

        // Check INodeTypeAccessRule (e.g., User, VUser, Organization nodes with WithPublicRead)
        // first; fall back to the security service permission check when the rule denies.
        if (!string.IsNullOrEmpty(node.NodeType)
            && _nodeTypeAccessRules.TryGetValue(node.NodeType, out var rule))
        {
            var context = new NodeValidationContext
            {
                Operation = NodeOperation.Read,
                Node = node
            };
            return rule.HasAccess(context, userId)
                .SelectMany(ok => ok ? Observable.Return(true) : permissionCheck);
        }

        return permissionCheck;
    }

    public IObservable<MeshNode?> GetNodeSecure(string path, string? userId, JsonSerializerOptions options)
        => _inner.GetNode(path, options)
            .SelectMany(node =>
            {
                if (node == null)
                    return Observable.Return<MeshNode?>(null);
                // Take(1): HasReadAccess rides the live AccessAssignment synced
                // query and is hot — without bounding it the surrounding
                // SelectMany never completes for the single-node case.
                return HasReadAccess(node, userId)
                    .Take(1)
                    .Select(ok =>
                    {
                        if (ok)
                            return (MeshNode?)node;
                        _logger.LogWarning("SecurePersistence: User {UserId} denied read access to {Path}", userId ?? "(anonymous)", path);
                        return null;
                    });
            });

    // GetChildrenSecure / GetDescendantsSecure / Get*Async / SearchAsync deleted in the
    // persistence-layer cull (2026-05-11). Permission-filtered listing now flows through
    // `workspace.GetQuery(id, query)` — the synced-query engine pushes RLS down into the
    // underlying provider, so this decorator no longer has to filter row-by-row in memory.

    #endregion

    #region Delegated Methods (pass-through to inner service)

    public IObservable<MeshNode?> GetNode(string path, JsonSerializerOptions options)
        => _inner.GetNode(path, options);

    /// <summary>
    /// Test/back-compat shim. Production callers go through <see cref="GetNode"/>.
    /// </summary>
    public Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.GetNode(path, options).FirstAsync().ToTask(ct);

    public IObservable<MeshNode> SaveNode(MeshNode node, JsonSerializerOptions options)
        => _inner.SaveNode(node, options);

    public IObservable<string> DeleteNode(string path, bool recursive = false)
        => _inner.DeleteNode(path, recursive);

    public IObservable<MeshNode> MoveNode(string sourcePath, string targetPath, JsonSerializerOptions options)
        => _inner.MoveNode(sourcePath, targetPath, options);

    public IObservable<bool> Exists(string path)
        => _inner.Exists(path);

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => _inner.FindBestPrefixMatch(fullPath, options);

    public Task InitializeAsync(CancellationToken ct = default)
        => _inner.InitializeAsync(ct);

    public IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath, JsonSerializerOptions options)
        => _inner.GetCommentsAsync(nodePath, options);

    public IObservable<Comment> AddComment(Comment comment, JsonSerializerOptions options)
        => _inner.AddComment(comment, options);

    public IObservable<string> DeleteComment(string commentId)
        => _inner.DeleteComment(commentId);

    public IObservable<Comment?> GetComment(string commentId) => _inner.GetComment(commentId);

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath, JsonSerializerOptions options)
        => _inner.GetPartitionObjectsAsync(nodePath, subPath, options);

    public IObservable<IReadOnlyCollection<object>> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => _inner.SavePartitionObjects(nodePath, subPath, objects, options);

    public IObservable<string> DeletePartitionObjects(string nodePath, string? subPath = null)
        => _inner.DeletePartitionObjects(nodePath, subPath);

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => _inner.GetPartitionMaxTimestamp(nodePath, subPath);

    #endregion
}
