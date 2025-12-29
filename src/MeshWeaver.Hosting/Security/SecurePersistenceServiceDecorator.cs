using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace MeshWeaver.Hosting.Security;

/// <summary>
/// Decorator that adds security filtering to IPersistenceService operations.
/// Implements the secure query methods by delegating to the inner service
/// and filtering results based on user permissions.
/// </summary>
public class SecurePersistenceServiceDecorator : IPersistenceService
{
    private readonly IPersistenceService _inner;
    private readonly ISecurityService _securityService;
    private readonly ILogger<SecurePersistenceServiceDecorator> _logger;

    public SecurePersistenceServiceDecorator(
        IPersistenceService inner,
        ISecurityService securityService,
        ILogger<SecurePersistenceServiceDecorator> logger)
    {
        _inner = inner;
        _securityService = securityService;
        _logger = logger;
    }

    #region Secure Operations

    public async Task<MeshNode?> GetNodeSecureAsync(string path, string? userId, CancellationToken ct = default)
    {
        var node = await _inner.GetNodeAsync(path, ct);
        if (node == null)
            return null;

        var hasPermission = string.IsNullOrEmpty(userId)
            ? await _securityService.HasPermissionAsync(path, Permission.Read, ct)
            : await _securityService.HasPermissionAsync(path, userId, Permission.Read, ct);

        if (!hasPermission)
        {
            _logger.LogDebug("SecurePersistence: User {UserId} denied read access to {Path}", userId ?? "(anonymous)", path);
            return null;
        }

        return node;
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenSecureAsync(string? parentPath, string? userId)
    {
        await foreach (var node in _inner.GetChildrenAsync(parentPath))
        {
            var hasPermission = string.IsNullOrEmpty(userId)
                ? await _securityService.HasPermissionAsync(node.Path, Permission.Read)
                : await _securityService.HasPermissionAsync(node.Path, userId, Permission.Read);

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

    public async IAsyncEnumerable<MeshNode> GetDescendantsSecureAsync(string? parentPath, string? userId)
    {
        await foreach (var node in _inner.GetDescendantsAsync(parentPath))
        {
            var hasPermission = string.IsNullOrEmpty(userId)
                ? await _securityService.HasPermissionAsync(node.Path, Permission.Read)
                : await _securityService.HasPermissionAsync(node.Path, userId, Permission.Read);

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

    public async IAsyncEnumerable<object> QuerySecureAsync(string query, string path, string? userId)
    {
        await foreach (var obj in _inner.QueryAsync(query, path))
        {
            if (obj is MeshNode node)
            {
                var hasPermission = string.IsNullOrEmpty(userId)
                    ? await _securityService.HasPermissionAsync(node.Path, Permission.Read)
                    : await _securityService.HasPermissionAsync(node.Path, userId, Permission.Read);

                if (hasPermission)
                {
                    yield return obj;
                }
                else
                {
                    _logger.LogTrace("SecurePersistence: Filtering out {Path} from query for user {UserId}", node.Path, userId ?? "(anonymous)");
                }
            }
            else
            {
                // For non-MeshNode objects (partition objects), we check permission based on the path context
                // These objects are typically associated with the query path
                var hasPermission = string.IsNullOrEmpty(userId)
                    ? await _securityService.HasPermissionAsync(path, Permission.Read)
                    : await _securityService.HasPermissionAsync(path, userId, Permission.Read);

                if (hasPermission)
                {
                    yield return obj;
                }
            }
        }
    }

    #endregion

    #region Delegated Methods (pass-through to inner service)

    public Task<MeshNode?> GetNodeAsync(string path, CancellationToken ct = default)
        => _inner.GetNodeAsync(path, ct);

    public IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath)
        => _inner.GetChildrenAsync(parentPath);

    public IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath)
        => _inner.GetDescendantsAsync(parentPath);

    public Task<MeshNode> SaveNodeAsync(MeshNode node, CancellationToken ct = default)
        => _inner.SaveNodeAsync(node, ct);

    public Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
        => _inner.DeleteNodeAsync(path, recursive, ct);

    public Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, CancellationToken ct = default)
        => _inner.MoveNodeAsync(sourcePath, targetPath, ct);

    public IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query)
        => _inner.SearchAsync(parentPath, query);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => _inner.ExistsAsync(path, ct);

    public Task InitializeAsync(CancellationToken ct = default)
        => _inner.InitializeAsync(ct);

    public IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath)
        => _inner.GetCommentsAsync(nodePath);

    public Task<Comment> AddCommentAsync(Comment comment, CancellationToken ct = default)
        => _inner.AddCommentAsync(comment, ct);

    public Task DeleteCommentAsync(string commentId, CancellationToken ct = default)
        => _inner.DeleteCommentAsync(commentId, ct);

    public Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default)
        => _inner.GetCommentAsync(commentId, ct);

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath = null)
        => _inner.GetPartitionObjectsAsync(nodePath, subPath);

    public Task SavePartitionObjectsAsync(string nodePath, string? subPath, IReadOnlyCollection<object> objects, CancellationToken ct = default)
        => _inner.SavePartitionObjectsAsync(nodePath, subPath, objects, ct);

    public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => _inner.DeletePartitionObjectsAsync(nodePath, subPath, ct);

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => _inner.GetPartitionMaxTimestampAsync(nodePath, subPath, ct);

    public IAsyncEnumerable<object> QueryAsync(string query, string path)
        => _inner.QueryAsync(query, path);

    #endregion
}
