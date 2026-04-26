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
                return HasReadAccess(node, userId)
                    .Select(ok =>
                    {
                        if (ok)
                            return (MeshNode?)node;
                        _logger.LogWarning("SecurePersistence: User {UserId} denied read access to {Path}", userId ?? "(anonymous)", path);
                        return null;
                    });
            });

    public IObservable<MeshNode> GetChildrenSecure(string? parentPath, string? userId, JsonSerializerOptions options)
        => ObservableTopNExtensions.ToObservableSequence(_inner.GetChildrenAsync(parentPath, options))
            .SelectMany(node => HasReadAccess(node, userId)
                .Do(ok =>
                {
                    if (!ok)
                        _logger.LogTrace("SecurePersistence: Filtering out {Path} for user {UserId}", node.Path, userId ?? "(anonymous)");
                })
                .Where(ok => ok)
                .Select(_ => node));

    public IObservable<MeshNode> GetDescendantsSecure(string? parentPath, string? userId, JsonSerializerOptions options)
        => ObservableTopNExtensions.ToObservableSequence(_inner.GetDescendantsAsync(parentPath, options))
            .SelectMany(node => HasReadAccess(node, userId)
                .Do(ok =>
                {
                    if (!ok)
                        _logger.LogTrace("SecurePersistence: Filtering out {Path} for user {UserId}", node.Path, userId ?? "(anonymous)");
                })
                .Where(ok => ok)
                .Select(_ => node));

    #endregion

    #region Delegated Methods (pass-through to inner service)

    public IObservable<MeshNode?> GetNode(string path, JsonSerializerOptions options)
        => _inner.GetNode(path, options);

    /// <summary>
    /// Test/back-compat shim. Production callers go through <see cref="GetNode"/>.
    /// </summary>
    [Obsolete("Use GetNode(path, options) which returns IObservable<MeshNode?>.")]
    public Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.GetNode(path, options).FirstAsync().ToTask(ct);

    public IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath, JsonSerializerOptions options)
        => _inner.GetChildrenAsync(parentPath, options);

    public IAsyncEnumerable<MeshNode> GetAllChildrenAsync(string? parentPath, JsonSerializerOptions options)
        => _inner.GetAllChildrenAsync(parentPath, options);

    public IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath, JsonSerializerOptions options)
        => _inner.GetDescendantsAsync(parentPath, options);

    public IAsyncEnumerable<MeshNode> GetAllDescendantsAsync(string? parentPath, JsonSerializerOptions options)
        => _inner.GetAllDescendantsAsync(parentPath, options);

    public Task<MeshNode> SaveNodeAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.SaveNodeAsync(node, options, ct);

    public Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
        => _inner.DeleteNodeAsync(path, recursive, ct);

    public Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.MoveNodeAsync(sourcePath, targetPath, options, ct);

    public IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query, JsonSerializerOptions options)
        => _inner.SearchAsync(parentPath, query, options);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => _inner.ExistsAsync(path, ct);

    public Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsync(
        string fullPath, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.FindBestPrefixMatchAsync(fullPath, options, ct);

    public Task InitializeAsync(CancellationToken ct = default)
        => _inner.InitializeAsync(ct);

    public IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath, JsonSerializerOptions options)
        => _inner.GetCommentsAsync(nodePath, options);

    public Task<Comment> AddCommentAsync(Comment comment, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.AddCommentAsync(comment, options, ct);

    public Task DeleteCommentAsync(string commentId, CancellationToken ct = default)
        => _inner.DeleteCommentAsync(commentId, ct);

    public Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default)
        => _inner.GetCommentAsync(commentId, ct);

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath, JsonSerializerOptions options)
        => _inner.GetPartitionObjectsAsync(nodePath, subPath, options);

    public Task SavePartitionObjectsAsync(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.SavePartitionObjectsAsync(nodePath, subPath, objects, options, ct);

    public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => _inner.DeletePartitionObjectsAsync(nodePath, subPath, ct);

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => _inner.GetPartitionMaxTimestampAsync(nodePath, subPath, ct);

    #endregion
}
