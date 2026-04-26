using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
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
    ILogger<PersistenceService>? logger = null,
    IEnumerable<INodeTypeAccessRule>? nodeTypeAccessRules = null) : IMeshStorage
{
    private readonly Dictionary<string, INodeTypeAccessRule> _nodeTypeAccessRules =
        (nodeTypeAccessRules ?? [])
            .Where(r => r.SupportedOperations.Contains(NodeOperation.Read))
            .GroupBy(r => r.NodeType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    private JsonSerializerOptions Options => hub.JsonSerializerOptions;

    public IObservable<MeshNode?> GetNode(string path)
        => core.GetNode(path, Options);

    public IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath)
        => core.GetChildrenAsync(parentPath, Options);

    public IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath)
        => core.GetDescendantsAsync(parentPath, Options);

    public IAsyncEnumerable<MeshNode> GetAllDescendantsAsync(string? parentPath)
        => core.GetAllDescendantsAsync(parentPath, Options);

    public IObservable<MeshNode> SaveNode(MeshNode node)
        => Observable.FromAsync(ct => core.SaveNodeAsync(node, Options, ct));

    public IObservable<string> DeleteNode(string path, bool recursive = false)
        => Observable.FromAsync(ct => core.DeleteNodeAsync(path, recursive, ct))
            .Select(_ => path);

    public IObservable<MeshNode> MoveNode(string sourcePath, string targetPath)
        => Observable.FromAsync(ct => core.MoveNodeAsync(sourcePath, targetPath, Options, ct));

    public IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query)
        => core.SearchAsync(parentPath, query, Options);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => core.ExistsAsync(path, ct);

    public Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsync(
        string fullPath, CancellationToken ct = default)
        => core.FindBestPrefixMatchAsync(fullPath, Options, ct);

    public Task InitializeAsync(CancellationToken ct = default)
        => core.InitializeAsync(ct);

    #region Comments

    public IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath)
        => core.GetCommentsAsync(nodePath, Options);

    public IObservable<Comment> AddComment(Comment comment)
        => Observable.FromAsync(ct => core.AddCommentAsync(comment, Options, ct));

    public IObservable<string> DeleteComment(string commentId)
        => Observable.FromAsync(ct => core.DeleteCommentAsync(commentId, ct))
            .Select(_ => commentId);

    public Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default)
        => core.GetCommentAsync(commentId, ct);

    #endregion

    #region Partition Storage

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath)
        => core.GetPartitionObjectsAsync(nodePath, subPath, Options);

    public IObservable<IReadOnlyCollection<object>> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects)
        => Observable.FromAsync(ct => core.SavePartitionObjectsAsync(nodePath, subPath, objects, Options, ct))
            .Select(_ => objects);

    public IObservable<string> DeletePartitionObjects(string nodePath, string? subPath = null)
        => Observable.FromAsync(ct => core.DeletePartitionObjectsAsync(nodePath, subPath, ct))
            .Select(_ => subPath ?? nodePath);

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => core.GetPartitionMaxTimestampAsync(nodePath, subPath, ct);

    #endregion

    #region Secure Operations

    /// <summary>
    /// NodeType definitions are always publicly readable.
    /// </summary>
    private static bool IsPubliclyReadable(MeshNode node)
        => node.NodeType == MeshNode.NodeTypePath;

    /// <summary>
    /// Users can always read their own User/VUser profile nodes,
    /// and hubs can always access their own nodes (MainNode == userId).
    /// </summary>
    private static bool IsSelfAccess(MeshNode node, string? userId)
        => !string.IsNullOrEmpty(userId)
           && (((node.Namespace == "User" || node.Namespace == "VUser")
                && string.Equals(node.Id, userId, StringComparison.OrdinalIgnoreCase))
               || string.Equals(node.MainNode, userId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Checks if a user has read access to a node.
    /// Checks: publicly readable → self-access → INodeTypeAccessRule → ISecurityService.
    /// Returns an IObservable&lt;bool&gt; — composes reactively with the secure-read pipeline.
    /// </summary>
    private IObservable<bool> HasReadAccess(MeshNode node, string? userId)
    {
        if (securityService == null || IsPubliclyReadable(node) || IsSelfAccess(node, userId))
            return Observable.Return(true);

        IObservable<bool> permissionCheck = string.IsNullOrEmpty(userId)
            ? securityService.HasPermission(node.Path, Permission.Read)
            : securityService.HasPermission(node.Path, userId, Permission.Read);

        // Check INodeTypeAccessRule (e.g., User, VUser, Organization with WithPublicRead) first;
        // fall back to the security service permission check when the rule denies.
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

    public IObservable<MeshNode?> GetNodeSecure(string path, string? userId)
        => core.GetNode(path, Options)
            .SelectMany(node =>
            {
                if (node == null || securityService == null)
                    return Observable.Return(node);
                return HasReadAccess(node, userId)
                    .Select(ok =>
                    {
                        if (ok)
                            return node;
                        logger?.LogWarning("SecurePersistence: User {UserId} denied read access to {Path}", userId ?? "(anonymous)", path);
                        return null;
                    });
            });

    public IObservable<MeshNode> GetChildrenSecure(string? parentPath, string? userId)
        => ObservableTopNExtensions.ToObservableSequence(core.GetChildrenAsync(parentPath, Options))
            .SelectMany(node =>
                securityService == null
                    ? Observable.Return(node)
                    : HasReadAccess(node, userId).Where(ok => ok).Select(_ => node));

    public IObservable<MeshNode> GetDescendantsSecure(string? parentPath, string? userId)
        => ObservableTopNExtensions.ToObservableSequence(core.GetDescendantsAsync(parentPath, Options))
            .SelectMany(node =>
                securityService == null
                    ? Observable.Return(node)
                    : HasReadAccess(node, userId).Where(ok => ok).Select(_ => node));

    #endregion
}
