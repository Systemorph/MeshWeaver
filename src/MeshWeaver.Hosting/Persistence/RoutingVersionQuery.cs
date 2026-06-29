using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Routes <see cref="IVersionQuery"/> calls by first path segment to
/// per-partition <see cref="IVersionQuery"/> instances. All operations are
/// observable end-to-end.
/// </summary>
public class RoutingVersionQuery : IVersionQuery
{
    private readonly ConcurrentDictionary<string, IVersionQuery> _queries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers the <see cref="IVersionQuery"/> that handles version queries
    /// for a partition, keyed by its first path segment (case-insensitive).
    /// Replaces any provider previously registered for the same partition.
    /// </summary>
    /// <param name="partition">The partition key (first path segment) to route.</param>
    /// <param name="query">The version-query provider for that partition.</param>
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

    /// <inheritdoc />
    public IObservable<MeshNodeVersion> GetVersions(string path)
        => GetQuery(path)?.GetVersions(path) ?? Observable.Empty<MeshNodeVersion>();

    /// <inheritdoc />
    public IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options)
        => GetQuery(path)?.GetVersion(path, version, options) ?? Observable.Return<MeshNode?>(null);

    /// <inheritdoc />
    public IObservable<MeshNode?> GetVersionBefore(string path, long beforeVersion, JsonSerializerOptions options)
        => GetQuery(path)?.GetVersionBefore(path, beforeVersion, options) ?? Observable.Return<MeshNode?>(null);

    /// <inheritdoc />
    public IObservable<MeshNode> WriteVersion(MeshNode node, JsonSerializerOptions options)
        => GetQuery(node.Path)?.WriteVersion(node, options) ?? Observable.Return(node);
}

/// <summary>
/// No-op implementation of <see cref="IVersionQuery"/> for environments
/// without version history support.
/// </summary>
public class NoOpVersionQuery : IVersionQuery
{
    /// <inheritdoc />
    public IObservable<MeshNodeVersion> GetVersions(string path)
        => Observable.Empty<MeshNodeVersion>();

    /// <inheritdoc />
    public IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options)
        => Observable.Return<MeshNode?>(null);

    /// <inheritdoc />
    public IObservable<MeshNode?> GetVersionBefore(string path, long beforeVersion, JsonSerializerOptions options)
        => Observable.Return<MeshNode?>(null);
}
