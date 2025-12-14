using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// In-memory implementation of IPersistenceService.
/// Suitable for development and testing.
/// Optionally backs to an IStorageAdapter for file system persistence.
/// </summary>
public class InMemoryPersistenceService : IPersistenceService
{
    private readonly ConcurrentDictionary<string, MeshNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Comment> _comments = new(StringComparer.OrdinalIgnoreCase);
    private readonly IStorageAdapter? _storageAdapter;
    private bool _initialized;

    public InMemoryPersistenceService(IStorageAdapter? storageAdapter = null)
    {
        _storageAdapter = storageAdapter;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        if (_storageAdapter != null)
        {
            await LoadFromStorageAsync("", ct);
        }

        _initialized = true;
    }

    private async Task LoadFromStorageAsync(string parentPath, CancellationToken ct)
    {
        var childPaths = await _storageAdapter!.ListChildPathsAsync(parentPath, ct);
        foreach (var path in childPaths)
        {
            var node = await _storageAdapter.ReadAsync(path, ct);
            if (node != null)
            {
                var normalizedPath = NormalizePath(path);
                _nodes[normalizedPath] = node;
                // Recursively load children
                await LoadFromStorageAsync(path, ct);
            }
        }
    }

    public Task<MeshNode?> GetNodeAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        _nodes.TryGetValue(normalizedPath, out var node);
        return Task.FromResult(node);
    }

    public Task<IEnumerable<MeshNode>> GetChildrenAsync(string? parentPath, CancellationToken ct = default)
    {
        var normalizedParent = NormalizePath(parentPath);
        var parentSegments = string.IsNullOrEmpty(normalizedParent)
            ? Array.Empty<string>()
            : normalizedParent.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var expectedDepth = parentSegments.Length + 1;

        var children = _nodes.Values
            .Where(n =>
            {
                var nodeSegments = n.Segments;
                if (nodeSegments.Length != expectedDepth)
                    return false;

                // Check if parent matches
                if (parentSegments.Length == 0)
                    return true;

                for (int i = 0; i < parentSegments.Length; i++)
                {
                    if (!nodeSegments[i].Equals(parentSegments[i], StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            })
            .OrderBy(n => n.DisplayOrder)
            .ThenBy(n => n.Name)
            .ToList();

        return Task.FromResult<IEnumerable<MeshNode>>(children);
    }

    public Task<IEnumerable<MeshNode>> GetDescendantsAsync(string? parentPath, CancellationToken ct = default)
    {
        var normalizedParent = NormalizePath(parentPath);

        IEnumerable<MeshNode> descendants;
        if (string.IsNullOrEmpty(normalizedParent))
        {
            descendants = _nodes.Values.ToList();
        }
        else
        {
            descendants = _nodes.Values
                .Where(n =>
                {
                    var nodePath = NormalizePath(n.Prefix);
                    return nodePath.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }

        return Task.FromResult<IEnumerable<MeshNode>>(descendants.OrderBy(n => n.Prefix));
    }

    public async Task<MeshNode> SaveNodeAsync(MeshNode node, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(node.Prefix);
        var savedNode = node with { Key = normalizedPath };
        _nodes[normalizedPath] = savedNode;

        if (_storageAdapter != null)
        {
            await _storageAdapter.WriteAsync(savedNode, ct);
        }

        return savedNode;
    }

    public async Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);

        if (recursive)
        {
            var toDelete = _nodes.Keys
                .Where(k => k.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                            k.StartsWith(normalizedPath + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in toDelete)
            {
                _nodes.TryRemove(key, out _);
                if (_storageAdapter != null)
                {
                    await _storageAdapter.DeleteAsync(key, ct);
                }
            }
        }
        else
        {
            _nodes.TryRemove(normalizedPath, out _);
            if (_storageAdapter != null)
            {
                await _storageAdapter.DeleteAsync(normalizedPath, ct);
            }
        }
    }

    public Task<IEnumerable<MeshNode>> SearchAsync(string? parentPath, string query, CancellationToken ct = default)
    {
        var normalizedParent = NormalizePath(parentPath);
        var queryLower = query.ToLowerInvariant();

        var results = _nodes.Values
            .Where(n =>
            {
                // Filter by parent path if specified
                if (!string.IsNullOrEmpty(normalizedParent))
                {
                    var nodePath = NormalizePath(n.Prefix);
                    if (!nodePath.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase) &&
                        !nodePath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                // Search in Name, Description, and Content
                if (n.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
                if (n.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
                if (n.Content?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return true;

                return false;
            })
            .OrderBy(n => n.DisplayOrder)
            .ThenBy(n => n.Name)
            .ToList();

        return Task.FromResult<IEnumerable<MeshNode>>(results);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        return Task.FromResult(_nodes.ContainsKey(normalizedPath));
    }

    private static string NormalizePath(string? path) =>
        path?.Trim('/').ToLowerInvariant() ?? "";

    #region Comments

    public Task<IEnumerable<Comment>> GetCommentsAsync(string nodePath, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(nodePath);
        var comments = _comments.Values
            .Where(c => NormalizePath(c.NodePath) == normalizedPath)
            .OrderBy(c => c.CreatedAt)
            .ToList();

        return Task.FromResult<IEnumerable<Comment>>(comments);
    }

    public Task<Comment> AddCommentAsync(Comment comment, CancellationToken ct = default)
    {
        var savedComment = comment with
        {
            Id = string.IsNullOrEmpty(comment.Id) ? Guid.NewGuid().ToString() : comment.Id,
            CreatedAt = comment.CreatedAt == default ? DateTimeOffset.UtcNow : comment.CreatedAt
        };

        _comments[savedComment.Id] = savedComment;
        return Task.FromResult(savedComment);
    }

    public Task DeleteCommentAsync(string commentId, CancellationToken ct = default)
    {
        _comments.TryRemove(commentId, out _);
        return Task.CompletedTask;
    }

    public Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default)
    {
        _comments.TryGetValue(commentId, out var comment);
        return Task.FromResult(comment);
    }

    #endregion
}
