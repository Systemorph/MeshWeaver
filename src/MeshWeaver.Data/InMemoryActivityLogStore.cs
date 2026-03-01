using System.Collections.Concurrent;

namespace MeshWeaver.Data;

/// <summary>
/// In-memory implementation of IActivityLogStore for testing and non-database scenarios.
/// </summary>
public class InMemoryActivityLogStore : IActivityLogStore
{
    private readonly ConcurrentBag<(string HubPath, ActivityLog Log)> _logs = new();

    public Task SaveActivityLogAsync(string hubPath, ActivityLog log, CancellationToken ct = default)
    {
        _logs.Add((hubPath, log));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActivityLog>> GetActivityLogsAsync(
        string hubPath,
        string? user = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var query = _logs
            .Where(l => string.Equals(l.HubPath, hubPath, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Log);

        if (user != null)
            query = query.Where(l => l.User?.Email == user || l.User?.DisplayName == user);
        if (from.HasValue)
            query = query.Where(l => l.Start >= from.Value);
        if (to.HasValue)
            query = query.Where(l => l.End <= to.Value);

        return Task.FromResult<IReadOnlyList<ActivityLog>>(
            query.OrderByDescending(l => l.Start).Take(limit).ToList());
    }

    public Task<IReadOnlyList<ActivityLog>> GetRecentActivityLogsAsync(
        string? user = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        var query = _logs.Select(l => l.Log with { HubPath = l.HubPath });

        if (user != null)
            query = query.Where(l => l.User?.Email == user || l.User?.DisplayName == user);
        if (from.HasValue)
            query = query.Where(l => l.Start >= from.Value);
        if (to.HasValue)
            query = query.Where(l => l.End <= to.Value);

        return Task.FromResult<IReadOnlyList<ActivityLog>>(
            query.OrderByDescending(l => l.Start).Take(limit).ToList());
    }
}
