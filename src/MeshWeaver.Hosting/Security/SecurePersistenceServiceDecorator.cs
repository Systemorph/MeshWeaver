using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

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

    public SecurePersistenceServiceDecorator(
        IStorageService inner,
        Lazy<ISecurityService> securityService,
        ILogger<SecurePersistenceServiceDecorator> logger)
    {
        _inner = inner;
        _securityService = securityService;
        _logger = logger;
    }

    private ISecurityService SecurityService => _securityService.Value;

    #region Secure Operations

    /// <summary>
    /// NodeType definitions are always publicly readable.
    /// </summary>
    private static bool IsPubliclyReadable(MeshNode node)
        => node.NodeType == MeshNode.NodeTypePath;

    public async Task<MeshNode?> GetNodeSecureAsync(string path, string? userId, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var node = await _inner.GetNodeAsync(path, options, ct);
        if (node == null)
            return null;

        if (IsPubliclyReadable(node))
            return node;

        var hasPermission = string.IsNullOrEmpty(userId)
            ? await SecurityService.HasPermissionAsync(path, Permission.Read, ct)
            : await SecurityService.HasPermissionAsync(path, userId, Permission.Read, ct);

        if (!hasPermission)
        {
            _logger.LogDebug("SecurePersistence: User {UserId} denied read access to {Path}", userId ?? "(anonymous)", path);
            return null;
        }

        return node;
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenSecureAsync(string? parentPath, string? userId, JsonSerializerOptions options)
    {
        await foreach (var node in _inner.GetChildrenAsync(parentPath, options))
        {
            if (IsPubliclyReadable(node))
            {
                yield return node;
                continue;
            }

            var hasPermission = string.IsNullOrEmpty(userId)
                ? await SecurityService.HasPermissionAsync(node.Path, Permission.Read)
                : await SecurityService.HasPermissionAsync(node.Path, userId, Permission.Read);

            if (hasPermission)
            {
                yield return node;
            }
            else
            {
                _logger.LogTrace("SecurePersistence: Filtering out {Path} for user {UserId}", node.Path, userId ?? "(anonymous)");
            }
        }
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsSecureAsync(string? parentPath, string? userId, JsonSerializerOptions options)
    {
        await foreach (var node in _inner.GetDescendantsAsync(parentPath, options))
        {
            if (IsPubliclyReadable(node))
            {
                yield return node;
                continue;
            }

            var hasPermission = string.IsNullOrEmpty(userId)
                ? await SecurityService.HasPermissionAsync(node.Path, Permission.Read)
                : await SecurityService.HasPermissionAsync(node.Path, userId, Permission.Read);

            if (hasPermission)
            {
                yield return node;
            }
            else
            {
                _logger.LogTrace("SecurePersistence: Filtering out {Path} for user {UserId}", node.Path, userId ?? "(anonymous)");
            }
        }
    }

    #endregion

    #region Delegated Methods (pass-through to inner service)

    public Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.GetNodeAsync(path, options, ct);

    public IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath, JsonSerializerOptions options)
        => _inner.GetChildrenAsync(parentPath, options);

    public IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath, JsonSerializerOptions options)
        => _inner.GetDescendantsAsync(parentPath, options);

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
