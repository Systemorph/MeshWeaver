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

    // Get*Async / SearchAsync deleted in persistence-cull (2026-05-11). Listings
    // go through `workspace.GetQuery(id, queries…)`; single-node reads through
    // `workspace.GetMeshNodeStream(path)`. See `Doc/Architecture/CqrsAndContentAccess.md`.

    public IObservable<MeshNode> SaveNode(MeshNode node)
        => core.SaveNode(node, Options);

    public IObservable<string> DeleteNode(string path, bool recursive = false)
        => core.DeleteNode(path, recursive);

    public IObservable<MeshNode> MoveNode(string sourcePath, string targetPath)
        => core.MoveNode(sourcePath, targetPath, Options);

    public IObservable<bool> Exists(string path)
        => core.Exists(path);

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(string fullPath)
        => core.FindBestPrefixMatch(fullPath, Options);

    public Task InitializeAsync(CancellationToken ct = default)
        => core.InitializeAsync(ct);

    #region Comments

    public IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath)
        => core.GetCommentsAsync(nodePath, Options);

    public IObservable<Comment> AddComment(Comment comment)
        => core.AddComment(comment, Options);

    public IObservable<string> DeleteComment(string commentId)
        => core.DeleteComment(commentId);

    public IObservable<Comment?> GetComment(string commentId)
        => core.GetComment(commentId);

    #endregion

    #region Partition Storage

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath)
        => core.GetPartitionObjectsAsync(nodePath, subPath, Options);

    public IObservable<IReadOnlyCollection<object>> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects)
        => core.SavePartitionObjects(nodePath, subPath, objects, Options);

    public IObservable<string> DeletePartitionObjects(string nodePath, string? subPath = null)
        => core.DeletePartitionObjects(nodePath, subPath);

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => core.GetPartitionMaxTimestamp(nodePath, subPath);

    #endregion

    #region Secure Operations

    /// <summary>
    /// NodeType definitions are always publicly readable.
    /// </summary>
    private static bool IsPubliclyReadable(MeshNode node)
        => node.NodeType == MeshNode.NodeTypePath;

    /// <summary>
    /// Users can always read their own profile nodes (identified by NodeType
    /// "User" or "VUser" with Id == userId), and hubs can always access their
    /// own nodes (MainNode == userId). NodeType is the type discriminator —
    /// don't gate on Namespace, since post-v10 the user partition is at
    /// root and the legacy "User"/"VUser" namespace is no longer meaningful.
    /// </summary>
    private static bool IsSelfAccess(MeshNode node, string? userId)
    {
        if (string.IsNullOrEmpty(userId))
            return false;
        if (string.Equals(node.MainNode, userId, StringComparison.OrdinalIgnoreCase))
            return true;
        return (node.NodeType == "User" || node.NodeType == "VUser")
               && string.Equals(node.Id, userId, StringComparison.OrdinalIgnoreCase);
    }

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
                // Take(1): HasReadAccess rides the live AccessAssignment synced
                // query and is hot — without bounding it the surrounding
                // SelectMany never completes for the single-node case.
                return HasReadAccess(node, userId)
                    .Take(1)
                    .Select(ok =>
                    {
                        if (ok)
                            return node;
                        logger?.LogWarning("SecurePersistence: User {UserId} denied read access to {Path}", userId ?? "(anonymous)", path);
                        return null;
                    });
            });

    // Get*Secure deleted with the rest of the "load all" surface. Permission-filtered
    // listing is now done via `workspace.GetQuery(id, query)` — the synced-query
    // engine pushes RLS into the underlying provider.

    #endregion
}
