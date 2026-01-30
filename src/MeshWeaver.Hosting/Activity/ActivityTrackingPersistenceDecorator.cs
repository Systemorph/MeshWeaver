using System.Collections.Concurrent;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Activity;

/// <summary>
/// Decorator that tracks user activity on persistence operations.
/// Records reads and writes to _activity/{userId} partition storage.
/// Flushes activity records synchronously on each save operation.
/// </summary>
public class ActivityTrackingPersistenceDecorator : IPersistenceServiceCore
{
    private readonly IPersistenceServiceCore _inner;
    private readonly AccessService _accessService;
    private readonly ILogger<ActivityTrackingPersistenceDecorator> _logger;

    // In-memory buffer for batching writes
    private readonly ConcurrentDictionary<string, UserActivityRecord> _pendingUpdates = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    public ActivityTrackingPersistenceDecorator(
        IPersistenceServiceCore inner,
        AccessService accessService,
        ILogger<ActivityTrackingPersistenceDecorator> logger)
    {
        _inner = inner;
        _accessService = accessService;
        _logger = logger;
    }

    #region Tracked Operations

    public async Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var result = await _inner.GetNodeAsync(path, options, ct);
        if (result != null)
            TrackActivity(path, result, ActivityType.Read);
        return result;
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath, JsonSerializerOptions options)
    {
        await foreach (var node in _inner.GetChildrenAsync(parentPath, options))
        {
            TrackActivity(node.Path, node, ActivityType.Read);
            yield return node;
        }
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath, JsonSerializerOptions options)
    {
        await foreach (var node in _inner.GetDescendantsAsync(parentPath, options))
        {
            TrackActivity(node.Path, node, ActivityType.Read);
            yield return node;
        }
    }

    public async Task<MeshNode> SaveNodeAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var result = await _inner.SaveNodeAsync(node, options, ct);
        TrackActivity(result.Path, result, ActivityType.Write);

        // Flush pending activities on save
        await FlushPendingActivitiesAsync(options);

        return result;
    }

    public async Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        TrackActivity(path, null, ActivityType.Delete);
        await _inner.DeleteNodeAsync(path, recursive, ct);
    }

    #endregion

    #region Activity Tracking

    private void TrackActivity(string path, MeshNode? node, ActivityType type)
    {
        // Skip tracking for _activity paths to avoid infinite loop
        if (string.IsNullOrEmpty(path) || path.StartsWith("_activity/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("_activity", StringComparison.OrdinalIgnoreCase))
            return;

        // Skip system paths
        if (path.StartsWith("_", StringComparison.OrdinalIgnoreCase))
            return;

        var userId = _accessService.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId))
            return; // No user context, skip tracking

        var key = $"{userId}:{path}";
        var now = DateTimeOffset.UtcNow;

        _pendingUpdates.AddOrUpdate(
            key,
            _ => new UserActivityRecord
            {
                Id = path.Replace("/", "_"),
                NodePath = path,
                UserId = userId,
                ActivityType = type,
                FirstAccessedAt = now,
                LastAccessedAt = now,
                AccessCount = 1,
                NodeName = node?.Name,
                NodeType = node?.NodeType,
                Namespace = node?.Namespace
            },
            (_, existing) => existing with
            {
                LastAccessedAt = now,
                AccessCount = existing.AccessCount + 1,
                ActivityType = type,
                NodeName = node?.Name ?? existing.NodeName,
                NodeType = node?.NodeType ?? existing.NodeType,
                Namespace = node?.Namespace ?? existing.Namespace
            });
    }

    /// <summary>
    /// Flushes any pending activity records to persistence.
    /// Call this explicitly when activities need to be persisted immediately.
    /// </summary>
    public async Task FlushPendingActivitiesAsync(JsonSerializerOptions options)
    {
        if (_pendingUpdates.IsEmpty)
            return;

        if (!await _flushLock.WaitAsync(0))
            return; // Already flushing

        try
        {
            // Group by user
            var userGroups = _pendingUpdates.Values
                .GroupBy(a => a.UserId)
                .ToList();

            foreach (var group in userGroups)
            {
                try
                {
                    var userId = group.Key;
                    var activityPath = $"_activity/{userId}";

                    // Load existing activities
                    var existing = new Dictionary<string, UserActivityRecord>(StringComparer.OrdinalIgnoreCase);
                    await foreach (var obj in _inner.GetPartitionObjectsAsync(activityPath, null, options))
                    {
                        if (obj is UserActivityRecord record)
                            existing[record.NodePath] = record;
                    }

                    // Merge with pending
                    foreach (var pending in group)
                    {
                        if (existing.TryGetValue(pending.NodePath, out var ex))
                        {
                            existing[pending.NodePath] = ex with
                            {
                                LastAccessedAt = pending.LastAccessedAt,
                                AccessCount = ex.AccessCount + pending.AccessCount,
                                ActivityType = pending.ActivityType,
                                NodeName = pending.NodeName ?? ex.NodeName,
                                NodeType = pending.NodeType ?? ex.NodeType,
                                Namespace = pending.Namespace ?? ex.Namespace
                            };
                        }
                        else
                        {
                            existing[pending.NodePath] = pending;
                        }
                        _pendingUpdates.TryRemove($"{userId}:{pending.NodePath}", out _);
                    }

                    // Save merged activities
                    await _inner.SavePartitionObjectsAsync(
                        activityPath, null,
                        existing.Values.Cast<object>().ToList(),
                        options);

                    _logger.LogDebug("Flushed {Count} activity records for user {UserId}", group.Count(), userId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to flush activity records for user {UserId}", group.Key);
                }
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    #endregion

    #region Delegated Methods (pass-through to inner service)

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

    // Secure operations delegate to inner
    public Task<MeshNode?> GetNodeSecureAsync(string path, string? userId, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.GetNodeSecureAsync(path, userId, options, ct);

    public IAsyncEnumerable<MeshNode> GetChildrenSecureAsync(string? parentPath, string? userId, JsonSerializerOptions options)
        => _inner.GetChildrenSecureAsync(parentPath, userId, options);

    public IAsyncEnumerable<MeshNode> GetDescendantsSecureAsync(string? parentPath, string? userId, JsonSerializerOptions options)
        => _inner.GetDescendantsSecureAsync(parentPath, userId, options);

    #endregion
}
