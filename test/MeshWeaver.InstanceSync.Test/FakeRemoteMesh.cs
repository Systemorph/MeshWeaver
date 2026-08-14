using System.Collections.Concurrent;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Persistence.Http;
using MeshWeaver.Mesh;

namespace MeshWeaver.InstanceSync.Test;

/// <summary>
/// In-memory stand-in for a remote MeshWeaver instance behind <see cref="IRemoteMeshClient"/> —
/// the sanctioned test seam (see the interface docs: "Tests inject a stub"). One node store per
/// fake (shared by every client the factory hands out), an <see cref="Unreachable"/> toggle that
/// makes every call throw <see cref="HttpRequestException"/> (the offline scenario), and a full
/// call log so tests can assert exactly what reached the remote (e.g. that a pulled change is
/// never echoed back). Write stamps (Version / LastModified) are assigned the way a real remote
/// would assign its own.
/// </summary>
public sealed class FakeRemoteMesh : IRemoteMeshClientFactory
{
    private readonly ConcurrentDictionary<string, MeshNode> store = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Op, string Path)> calls = new();
    private long version;

    /// <summary>When true, every remote call throws <see cref="HttpRequestException"/>.</summary>
    public volatile bool Unreachable;

    /// <summary>Every remote call in order: (operation, path).</summary>
    public IReadOnlyList<(string Op, string Path)> Calls => calls.ToArray();

    /// <summary>The (url, token) pairs clients were created for.</summary>
    public ConcurrentQueue<(string Url, string Token)> CreatedClients { get; } = new();

    /// <summary>Snapshot of the remote store keyed by path.</summary>
    public IReadOnlyDictionary<string, MeshNode> Store => store;

    /// <summary>The write calls (create/update/delete) recorded for <paramref name="path"/>.</summary>
    public int WriteCount(string path) =>
        calls.Count(c => c.Path == path && c.Op is "create" or "update" or "delete");

    /// <summary>Reads a stored node (null when absent).</summary>
    public MeshNode? Node(string path) => store.GetValueOrDefault(path);

    /// <summary>Seeds a node as if a remote user had written it (own stamps).</summary>
    public void Seed(MeshNode node, DateTimeOffset? lastModified = null) =>
        store[node.Path] = node with
        {
            Version = Interlocked.Increment(ref version),
            LastModified = lastModified ?? DateTimeOffset.UtcNow,
        };

    /// <inheritdoc />
    public IRemoteMeshClient Create(string remoteBaseUrl, string remoteToken)
    {
        CreatedClients.Enqueue((remoteBaseUrl, remoteToken));
        return new Client(this);
    }

    private void ThrowIfUnreachable()
    {
        if (Unreachable)
            throw new HttpRequestException("Connection refused (fake remote is unreachable)");
    }

    private void Record(string op, string path) => calls.Enqueue((op, path));

    private sealed class Client(FakeRemoteMesh owner) : IRemoteMeshClient
    {
        public IObservable<MeshNode?> Get(string path) => Observable.Defer(() =>
        {
            owner.ThrowIfUnreachable();
            owner.Record("get", path);
            return Observable.Return(owner.store.GetValueOrDefault(path));
        });

        public IObservable<Unit> Create(MeshNode node) => Observable.Defer(() =>
        {
            owner.ThrowIfUnreachable();
            owner.Record("create", node.Path);
            var stamped = node with
            {
                Version = Interlocked.Increment(ref owner.version),
                LastModified = DateTimeOffset.UtcNow,
            };
            if (!owner.store.TryAdd(node.Path, stamped))
                throw new InvalidOperationException($"Node already exists: {node.Path}");
            return Observable.Return(Unit.Default);
        });

        public IObservable<Unit> Update(MeshNode node) => Observable.Defer(() =>
        {
            owner.ThrowIfUnreachable();
            owner.Record("update", node.Path);
            if (!owner.store.ContainsKey(node.Path))
                throw new InvalidOperationException($"Node does not exist: {node.Path}");
            owner.store[node.Path] = node with
            {
                Version = Interlocked.Increment(ref owner.version),
                LastModified = DateTimeOffset.UtcNow,
            };
            return Observable.Return(Unit.Default);
        });

        public IObservable<Unit> Delete(string path) => Observable.Defer(() =>
        {
            owner.ThrowIfUnreachable();
            owner.Record("delete", path);
            owner.store.TryRemove(path, out _);
            return Observable.Return(Unit.Default);
        });

        public IObservable<IReadOnlyList<string>> SearchPaths(string query) =>
            Search(query).Select(r => (IReadOnlyList<string>)r.Hits.Select(h => h.Path).ToList());

        /// <summary>Supports the two query shapes the sync worker emits:
        /// <c>path:{p} scope:descendants</c> and <c>path:{p} scope:children</c>.</summary>
        public IObservable<RemoteSearchResult> Search(string query, int limit = 50) => Observable.Defer(() =>
        {
            owner.ThrowIfUnreachable();
            owner.Record("search", query);
            var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = parts.FirstOrDefault(p => p.StartsWith("path:", StringComparison.Ordinal))?["path:".Length..]
                       ?? throw new InvalidOperationException($"Fake remote cannot parse query: {query}");
            var scope = parts.FirstOrDefault(p => p.StartsWith("scope:", StringComparison.Ordinal))?["scope:".Length..]
                        ?? "children";
            var prefix = path + "/";
            var matches = owner.store.Values
                .Where(n => n.Path.StartsWith(prefix, StringComparison.Ordinal))
                .Where(n => scope != "children" || !n.Path[prefix.Length..].Contains('/'))
                .OrderBy(n => n.Path, StringComparer.Ordinal)
                .ToList();
            var hits = matches.Take(limit)
                .Select(n => new RemoteSearchHit(n.Path, n.NodeType, n.Version, n.LastModified))
                .ToList();
            return Observable.Return(new RemoteSearchResult(hits, Truncated: matches.Count > limit));
        });
    }
}
