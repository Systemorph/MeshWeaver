using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;

namespace MeshWeaver.Hosting.Sqlite;

/// <summary>
/// SQLite-local vector search. An <see cref="IMeshQueryProvider"/> that contributes cosine-ranked,
/// semantically-matched nodes to free-text <b>search</b> and <b>autocomplete</b> by brute-forcing
/// over the embeddings <see cref="SqliteStorageAdapter"/> stores — the on-device counterpart to
/// Postgres's HNSW vector path, sized for SQLite's local/single-user scale.
///
/// <para>It is purely <b>additive</b>: it sits alongside the pedestrian
/// <c>StorageAdapterMeshQueryProvider</c> in <see cref="MeshQuery"/>'s fan-in and contributes
/// vector hits with a cosine-derived <see cref="QueryResultChange{T}.Scores"/> value. The
/// aggregator sorts the merged set by score (when no explicit <c>sort:</c>), so semantic matches
/// surface by similarity — no edits to the shared provider, no Postgres coupling. When no
/// <see cref="ITextEmbedder"/> is wired (e.g. a device with no model server) it emits an empty
/// Initial and contributes nothing; lexical search is unchanged.</para>
/// </summary>
public sealed class SqliteVectorMeshQuery : IMeshQueryProvider
{
    private readonly SqliteStorageAdapter _adapter;
    private readonly ITextEmbedder? _embedder;
    private readonly IIoPool _ioPool;
    private readonly QueryParser _parser = new();
    private readonly QueryEvaluator _evaluator = new();
    private long _version;

    public SqliteVectorMeshQuery(SqliteStorageAdapter adapter, ITextEmbedder? embedder = null,
        IoPoolRegistry? ioPoolRegistry = null)
    {
        _adapter = adapter;
        _embedder = embedder;
        _ioPool = ioPoolRegistry?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
    }

    public bool Matches(IReadOnlyList<string> queryNamespaces) => _embedder is not null;

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
    {
        var parsed = _parser.Parse(request.EffectiveQueries.FirstOrDefault());
        // Contributes only to free-text MeshNode queries, and only with an embedder wired.
        if (typeof(T) != typeof(MeshNode) || _embedder is null || string.IsNullOrWhiteSpace(parsed.TextSearch))
            return EmptyInitial<T>(parsed);

        var topK = request.Limit ?? parsed.Limit ?? 50;
        // Embed the query (I/O leaf on the pool), then rank the stored vectors by cosine. A failed
        // embed → null → empty contribution, so lexical search still serves.
        return _ioPool.Invoke(ct => EmbedSafe(parsed.TextSearch!, ct))
            .SelectMany(queryVec => queryVec is null
                ? EmptyInitial<T>(parsed)
                : _adapter.AllEmbeddings(options)
                    .Select(all => RankToChange<T>(all, queryVec, parsed, topK)));
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
        string? contextPath = null, string? context = null)
    {
        if (_embedder is null || string.IsNullOrWhiteSpace(prefix))
            return Observable.Return(Empty);

        return _ioPool.Invoke(ct => EmbedSafe(prefix, ct))
            .SelectMany(queryVec => queryVec is null
                ? Observable.Return(Empty)
                : _adapter.AllEmbeddings(options).Select(all => RankToSuggestions(all, queryVec, basePath, limit)));
    }

    /// <inheritdoc />
    public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
        => Observable.Return<T?>(default);

    private static readonly IReadOnlyCollection<QueryResult> Empty = Array.Empty<QueryResult>();

    private QueryResultChange<T> RankToChange<T>(
        IReadOnlyList<(MeshNode Node, float[] Embedding)> all, float[] queryVec, ParsedQuery parsed, int topK)
    {
        var structural = parsed with { TextSearch = null };
        var ranked = Rank(all, queryVec, n => _evaluator.Matches(n, structural) && InScope(parsed, n))
            .Take(topK)
            .ToList();
        return new QueryResultChange<T>
        {
            ChangeType = QueryChangeType.Initial,
            Version = Interlocked.Increment(ref _version),
            Query = parsed,
            Items = ranked.Select(r => (T)(object)r.Node).ToList(),
            Scores = ranked.Select(r => r.Score).ToList(),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private IReadOnlyCollection<QueryResult> RankToSuggestions(
        IReadOnlyList<(MeshNode Node, float[] Embedding)> all, float[] queryVec, string? basePath, int limit)
    {
        var baseNorm = basePath?.Trim('/') ?? "";
        bool ScopeOk(MeshNode n)
        {
            if (baseNorm.Length == 0) return true;
            var p = n.Path ?? "";
            return p.Equals(baseNorm, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(baseNorm + "/", StringComparison.OrdinalIgnoreCase);
        }
        var name = ((IMeshQueryProvider)this).Name;
        return Rank(all, queryVec, ScopeOk)
            .Take(limit)
            .Select(r => QueryResult.FromNode(r.Node, r.Score, providerName: name))
            .ToList();
    }

    // Cosine scaled to ~0..100 so it competes with the lexical providers' fuzzy scores in the
    // aggregator's score-descending merge; the relative order is the cosine order regardless of scale.
    private static IEnumerable<(MeshNode Node, double Score)> Rank(
        IReadOnlyList<(MeshNode Node, float[] Embedding)> all, float[] queryVec, Func<MeshNode, bool> filter)
        => all.Where(x => filter(x.Node))
              .Select(x => (x.Node, Score: Cosine(queryVec, x.Embedding) * 100.0))
              .Where(x => x.Score > 0)
              .OrderByDescending(x => x.Score);

    private IObservable<QueryResultChange<T>> EmptyInitial<T>(ParsedQuery parsed)
        => Observable.Return(new QueryResultChange<T>
        {
            ChangeType = QueryChangeType.Initial,
            Version = Interlocked.Increment(ref _version),
            Query = parsed,
            Items = Array.Empty<T>(),
            Scores = Array.Empty<double>(),
            Timestamp = DateTimeOffset.UtcNow,
        });

    private async Task<float[]?> EmbedSafe(string text, CancellationToken ct)
    {
        // A query-time embed failure degrades gracefully to lexical search (this provider just
        // contributes nothing) — it is NOT a data fault to surface, unlike a write-time failure.
        try { return await _embedder!.EmbedAsync(text, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private static bool InScope(ParsedQuery parsed, MeshNode node)
    {
        if (string.IsNullOrEmpty(parsed.Path)) return true;
        var p = node.Path ?? "";
        return parsed.Scope == QueryScope.Exact
            ? p.Equals(parsed.Path, StringComparison.OrdinalIgnoreCase)
            : p.Equals(parsed.Path, StringComparison.OrdinalIgnoreCase)
              || p.StartsWith(parsed.Path + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
