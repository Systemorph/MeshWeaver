using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Routes IVersionQuery calls by first path segment to per-partition IVersionQuery instances.
/// </summary>
public class RoutingVersionQuery : IVersionQuery
{
    private readonly ConcurrentDictionary<string, IVersionQuery> _queries = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string partition, IVersionQuery query)
    {
        _queries[partition] = query;
    }

    private IVersionQuery? GetQuery(string path)
    {
        var segment = PathPartition.GetFirstSegment(path);
        if (segment == null) return null;
        return _queries.TryGetValue(segment, out var q) ? q : null;
    }

    public IAsyncEnumerable<MeshNodeVersion> GetVersionsAsync(
        string path, CancellationToken ct = default)
    {
        var query = GetQuery(path);
        return query?.GetVersionsAsync(path, ct) ?? EmptyAsyncEnumerable<MeshNodeVersion>.Instance;
    }

    public Task<MeshNode?> GetVersionAsync(
        string path, long version, JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var query = GetQuery(path);
        return query?.GetVersionAsync(path, version, options, ct) ?? Task.FromResult<MeshNode?>(null);
    }

    public Task<MeshNode?> GetVersionBeforeAsync(
        string path, long beforeVersion, JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var query = GetQuery(path);
        return query?.GetVersionBeforeAsync(path, beforeVersion, options, ct) ?? Task.FromResult<MeshNode?>(null);
    }

    public Task WriteVersionAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var query = GetQuery(node.Path);
        return query?.WriteVersionAsync(node, options, ct) ?? Task.CompletedTask;
    }
}

/// <summary>
/// No-op implementation of IVersionQuery for environments without version history support.
/// </summary>
public class NoOpVersionQuery : IVersionQuery
{
    public IAsyncEnumerable<MeshNodeVersion> GetVersionsAsync(string path, CancellationToken ct = default)
        => EmptyAsyncEnumerable<MeshNodeVersion>.Instance;

    public Task<MeshNode?> GetVersionAsync(string path, long version, JsonSerializerOptions options, CancellationToken ct = default)
        => Task.FromResult<MeshNode?>(null);

    public Task<MeshNode?> GetVersionBeforeAsync(string path, long beforeVersion, JsonSerializerOptions options, CancellationToken ct = default)
        => Task.FromResult<MeshNode?>(null);
}

/// <summary>
/// Helper to provide an empty IAsyncEnumerable without requiring System.Linq.AsyncEnumerable.
/// </summary>
internal static class EmptyAsyncEnumerable<T>
{
    public static readonly IAsyncEnumerable<T> Instance = CreateEmpty();

    private static async IAsyncEnumerable<T> CreateEmpty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
