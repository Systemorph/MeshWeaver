using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Scoped wrapper around IPersistenceServiceCore that automatically injects
/// JsonSerializerOptions from the current IMessageHub.
/// </summary>
internal class PersistenceService(IPersistenceServiceCore core, IMessageHub hub) : IPersistenceService
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

    public Task<MeshNode?> GetNodeSecureAsync(string path, string? userId, CancellationToken ct = default)
        => core.GetNodeSecureAsync(path, userId, Options, ct);

    public IAsyncEnumerable<MeshNode> GetChildrenSecureAsync(string? parentPath, string? userId)
        => core.GetChildrenSecureAsync(parentPath, userId, Options);

    public IAsyncEnumerable<MeshNode> GetDescendantsSecureAsync(string? parentPath, string? userId)
        => core.GetDescendantsSecureAsync(parentPath, userId, Options);

    #endregion
}
