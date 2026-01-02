using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Query;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Activity;

/// <summary>
/// Decorator that tracks user activity on persistence operations.
/// Records reads and writes to _activity/{userId} partition storage.
/// Uses in-memory buffering with periodic flush for performance.
/// </summary>
public class ActivityTrackingPersistenceDecorator : IPersistenceService, IDisposable
{
    private readonly IPersistenceService _inner;
    private readonly AccessService _accessService;
    private readonly ILogger<ActivityTrackingPersistenceDecorator> _logger;

    // In-memory buffer for batching writes
    private readonly ConcurrentDictionary<string, UserActivityRecord> _pendingUpdates = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly Timer _flushTimer;
    private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(5);
    private bool _disposed;

    public ActivityTrackingPersistenceDecorator(
        IPersistenceService inner,
        AccessService accessService,
        ILogger<ActivityTrackingPersistenceDecorator> logger)
    {
        _inner = inner;
        _accessService = accessService;
        _logger = logger;

        // Start periodic flush timer
        _flushTimer = new Timer(FlushTimerCallback, null, _flushInterval, _flushInterval);
    }

    private void FlushTimerCallback(object? state)
    {
        _ = FlushPendingActivitiesAsync();
    }

    #region Tracked Operations

    public async Task<MeshNode?> GetNodeAsync(string path, CancellationToken ct = default)
    {
        var result = await _inner.GetNodeAsync(path, ct);
        if (result != null)
            TrackActivity(path, result, ActivityType.Read);
        return result;
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath)
    {
        await foreach (var node in _inner.GetChildrenAsync(parentPath))
        {
            TrackActivity(node.Path, node, ActivityType.Read);
            yield return node;
        }
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath)
    {
        await foreach (var node in _inner.GetDescendantsAsync(parentPath))
        {
            TrackActivity(node.Path, node, ActivityType.Read);
            yield return node;
        }
    }

    public async Task<MeshNode> SaveNodeAsync(MeshNode node, CancellationToken ct = default)
    {
        var result = await _inner.SaveNodeAsync(node, ct);
        TrackActivity(result.Path, result, ActivityType.Write);
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
        if (path.StartsWith("_activity/", StringComparison.OrdinalIgnoreCase) ||
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

    private async Task FlushPendingActivitiesAsync()
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
                    await foreach (var obj in _inner.GetPartitionObjectsAsync(activityPath))
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
                        existing.Values.Cast<object>().ToList());

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

    public async IAsyncEnumerable<object> QueryAsync(string query, string path)
    {
        var parser = new QueryParser();
        var parsedQuery = parser.Parse(query);
        var evaluator = new QueryEvaluator();

        IEnumerable<object> results;

        // Handle $source=activity queries - query from user's activity partition
        if (parsedQuery.Source == QuerySource.Activity)
        {
            var userId = _accessService.Context?.ObjectId;
            if (string.IsNullOrEmpty(userId))
            {
                yield break; // No user context for activity query
            }

            var activityPath = $"_activity/{userId}";
            var activityList = new List<object>();

            await foreach (var obj in _inner.GetPartitionObjectsAsync(activityPath))
            {
                if (evaluator.Matches(obj, parsedQuery))
                    activityList.Add(obj);
            }

            results = activityList;
        }
        else
        {
            // Delegate to inner service for regular queries
            var innerResults = new List<object>();
            await foreach (var item in _inner.QueryAsync(query, path))
            {
                innerResults.Add(item);
            }
            results = innerResults;
        }

        // Apply ordering
        if (parsedQuery.OrderBy != null)
        {
            results = evaluator.OrderResults(results, parsedQuery.OrderBy);
        }

        // Apply limit
        if (parsedQuery.Limit.HasValue)
        {
            results = evaluator.LimitResults(results, parsedQuery.Limit);
        }

        foreach (var item in results)
        {
            yield return item;
        }
    }

    // Secure operations delegate to inner
    public Task<MeshNode?> GetNodeSecureAsync(string path, string? userId, CancellationToken ct = default)
        => _inner.GetNodeSecureAsync(path, userId, ct);

    public IAsyncEnumerable<MeshNode> GetChildrenSecureAsync(string? parentPath, string? userId)
        => _inner.GetChildrenSecureAsync(parentPath, userId);

    public IAsyncEnumerable<MeshNode> GetDescendantsSecureAsync(string? parentPath, string? userId)
        => _inner.GetDescendantsSecureAsync(parentPath, userId);

    public IAsyncEnumerable<object> QuerySecureAsync(string query, string path, string? userId)
        => _inner.QuerySecureAsync(query, path, userId);

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _flushTimer.Dispose();

        // Final flush
        FlushPendingActivitiesAsync().GetAwaiter().GetResult();

        _flushLock.Dispose();
    }

    #endregion
}
