using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Scoped wrapper around IStorageService that automatically injects
/// JsonSerializerOptions from the current IMessageHub.
/// When ISecurityService is available, secure methods filter results by user permissions.
/// </summary>
internal class PersistenceService(
    IStorageService core,
    IMessageHub hub,
    ISecurityService? securityService = null,
    ILogger<PersistenceService>? logger = null) : IMeshStorage
{
    private JsonSerializerOptions Options => hub.JsonSerializerOptions;

    public Task<MeshNode?> GetNodeAsync(string path, CancellationToken ct = default)
        => core.GetNodeAsync(path, Options, ct);

    public IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath)
        => core.GetChildrenAsync(parentPath, Options);

    public IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath)
        => core.GetDescendantsAsync(parentPath, Options);

    public Task<MeshNode> SaveNodeAsync(MeshNode node, CancellationToken ct = default)
        => core.SaveNodeAsync(node, Options, ct);

    public Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
        => core.DeleteNodeAsync(path, recursive, ct);

    public Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, CancellationToken ct = default)
        => core.MoveNodeAsync(sourcePath, targetPath, Options, ct);

    public IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query)
        => core.SearchAsync(parentPath, query, Options);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => core.ExistsAsync(path, ct);

    public Task InitializeAsync(CancellationToken ct = default)
        => core.InitializeAsync(ct);

    #region Comments

    public IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath)
        => core.GetCommentsAsync(nodePath, Options);

    public Task<Comment> AddCommentAsync(Comment comment, CancellationToken ct = default)
        => core.AddCommentAsync(comment, Options, ct);

    public Task DeleteCommentAsync(string commentId, CancellationToken ct = default)
        => core.DeleteCommentAsync(commentId, ct);

    public Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default)
        => core.GetCommentAsync(commentId, ct);

    #endregion

    #region Partition Storage

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath)
        => core.GetPartitionObjectsAsync(nodePath, subPath, Options);

    public Task SavePartitionObjectsAsync(string nodePath, string? subPath, IReadOnlyCollection<object> objects, CancellationToken ct = default)
        => core.SavePartitionObjectsAsync(nodePath, subPath, objects, Options, ct);

    public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => core.DeletePartitionObjectsAsync(nodePath, subPath, ct);

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => core.GetPartitionMaxTimestampAsync(nodePath, subPath, ct);

    #endregion

    #region Secure Operations

    /// <summary>
    /// NodeType definitions are always publicly readable.
    /// </summary>
    private static bool IsPubliclyReadable(MeshNode node)
        => node.NodeType == MeshNode.NodeTypePath;

    public async Task<MeshNode?> GetNodeSecureAsync(string path, string? userId, CancellationToken ct = default)
    {
        var node = await core.GetNodeAsync(path, Options, ct);
        if (node == null || securityService == null)
            return node;

        if (IsPubliclyReadable(node))
            return node;

        var hasPermission = string.IsNullOrEmpty(userId)
            ? await securityService.HasPermissionAsync(path, Permission.Read, ct)
            : await securityService.HasPermissionAsync(path, userId, Permission.Read, ct);

        if (!hasPermission)
        {
            logger?.LogDebug("SecurePersistence: User {UserId} denied read access to {Path}", userId ?? "(anonymous)", path);
            return null;
        }

        return node;
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenSecureAsync(string? parentPath, string? userId)
    {
        await foreach (var node in core.GetChildrenAsync(parentPath, Options))
        {
            if (securityService == null || IsPubliclyReadable(node))
            {
                yield return node;
                continue;
            }

            var hasPermission = string.IsNullOrEmpty(userId)
                ? await securityService.HasPermissionAsync(node.Path, Permission.Read)
                : await securityService.HasPermissionAsync(node.Path, userId, Permission.Read);

            if (hasPermission)
                yield return node;
        }
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsSecureAsync(string? parentPath, string? userId)
    {
        await foreach (var node in core.GetDescendantsAsync(parentPath, Options))
        {
            if (securityService == null || IsPubliclyReadable(node))
            {
                yield return node;
                continue;
            }

            var hasPermission = string.IsNullOrEmpty(userId)
                ? await securityService.HasPermissionAsync(node.Path, Permission.Read)
                : await securityService.HasPermissionAsync(node.Path, userId, Permission.Read);

            if (hasPermission)
                yield return node;
        }
    }

    #endregion
}
