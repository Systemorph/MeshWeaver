using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Sqlite;

/// <summary>
/// A durable, embedded <see cref="IStorageAdapter"/> backed by SQLite — the local-first counterpart
/// to the Postgres adapter (the right engine for an on-device mesh; iOS/Android/desktop ship SQLite).
///
/// <para>Single logical store: one <c>mesh_nodes</c> table keyed by path (namespace + node_type
/// indexed), the whole <see cref="MeshNode"/> persisted as JSON. SQLite is single-writer, so every
/// operation is a SYNC-BLOCKING leaf run through <see cref="IIoPool.InvokeBlocking"/> off the hub
/// scheduler (no async/await, no <c>Observable.FromAsync</c>) and serialised on one held connection
/// via an internal lock — correct regardless of the pool's cap. Use <c>Data Source=:memory:</c> for
/// tests (the held connection keeps the in-memory DB alive) or a file path for a real local mesh.
/// Schema is created on construction.</para>
/// </summary>
public sealed class SqliteStorageAdapter : IStorageAdapter, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IIoPool _ioPool;
    private readonly ITextEmbedder? _embedder;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly Subject<DataChangeNotification> _changes = new();

    public SqliteStorageAdapter(string connectionString, IIoPool? ioPool = null,
        ITextEmbedder? embedder = null, ILogger<SqliteStorageAdapter>? logger = null)
    {
        _ioPool = ioPool ?? IoPool.Unbounded;
        _embedder = embedder;
        _logger = logger;
        // One dedicated connection held for the adapter's lifetime — pooling is pointless and would
        // retain the file handle past Dispose (so the DB file can't be deleted/reopened cleanly).
        var csb = new SqliteConnectionStringBuilder(connectionString) { Pooling = false };
        _connection = new SqliteConnection(csb.ConnectionString);
        _connection.Open();
        EnsureSchema();
    }

    public IObservable<DataChangeNotification> Changes => _changes.AsObservable();

    private static string Norm(string? path) => path?.Trim('/') ?? "";

    private void EnsureSchema()
    {
        using (var cmd = _connection.CreateCommand())
        {
            // embedding BLOB = the node's vector (raw little-endian float[]), populated on write
            // when an ITextEmbedder is wired. NULL when embeddings are off — vector search simply
            // skips those rows (WHERE embedding IS NOT NULL), the same contract as Postgres.
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS mesh_nodes (
                    path       TEXT PRIMARY KEY,
                    namespace  TEXT NOT NULL,
                    id         TEXT NOT NULL,
                    node_type  TEXT,
                    data       TEXT NOT NULL,
                    embedding  BLOB
                );
                CREATE INDEX IF NOT EXISTS ix_mesh_nodes_namespace ON mesh_nodes(namespace);
                CREATE INDEX IF NOT EXISTS ix_mesh_nodes_node_type ON mesh_nodes(node_type);
                CREATE TABLE IF NOT EXISTS partition_objects (
                    partition_key TEXT PRIMARY KEY,
                    data          TEXT NOT NULL,
                    last_modified TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // A DB created before the embedding column existed: CREATE TABLE IF NOT EXISTS won't add
        // it, so ALTER — idempotent, guarded by a column probe (SQLite has no ADD COLUMN IF NOT EXISTS).
        bool hasEmbedding;
        using (var probe = _connection.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM pragma_table_info('mesh_nodes') WHERE name = 'embedding'";
            hasEmbedding = probe.ExecuteScalar() is not null;
        }
        if (!hasEmbedding)
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE mesh_nodes ADD COLUMN embedding BLOB";
            alter.ExecuteNonQuery();
        }
    }

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => _ioPool.InvokeBlocking<MeshNode?>(_ =>
        {
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT data FROM mesh_nodes WHERE path = $p";
                cmd.Parameters.AddWithValue("$p", Norm(path));
                return cmd.ExecuteScalar() is string data
                    ? JsonSerializer.Deserialize<MeshNode>(data, options)
                    : null;
            }
        });

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
    {
        var path = Norm(node.Path);
        if (path.Length == 0) return Observable.Return<MeshNode?>(node);

        // The embedding is an async I/O leaf (HTTP to a model server); the SQLite insert is a
        // sync-blocking leaf. Run the embed on the pool FIRST (best-effort — a failure stores a
        // NULL embedding, never wedges the write), then the blocking insert. Embedding text mirrors
        // the Postgres adapter exactly ("{Name} {NodeType}") so the two backends share a contract.
        var embedText = EmbeddingText(node);
        var embedding = _embedder is { } embedder && !string.IsNullOrEmpty(embedText)
            ? _ioPool.Invoke(ct => EmbedSafe(embedder, embedText!, ct))
            : Observable.Return<float[]?>(null);

        return embedding.SelectMany(vector => _ioPool.InvokeBlocking<MeshNode?>(_ =>
        {
            var data = JsonSerializer.Serialize(node, options);
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO mesh_nodes(path, namespace, id, node_type, data, embedding)
                    VALUES ($p, $ns, $id, $nt, $d, $emb)
                    ON CONFLICT(path) DO UPDATE SET namespace = $ns, id = $id, node_type = $nt, data = $d, embedding = $emb
                    """;
                cmd.Parameters.AddWithValue("$p", path);
                cmd.Parameters.AddWithValue("$ns", node.Namespace ?? "");
                cmd.Parameters.AddWithValue("$id", node.Id ?? "");
                cmd.Parameters.AddWithValue("$nt", (object?)node.NodeType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$d", data);
                cmd.Parameters.AddWithValue("$emb", vector is null ? DBNull.Value : ToBlob(vector));
                cmd.ExecuteNonQuery();
            }
            try { _changes.OnNext(DataChangeNotification.Updated(path, node)); } catch { /* never throw */ }
            return node;
        }));
    }

    /// <summary>Embedding text — Name + NodeType, identical to the Postgres adapter's contract.</summary>
    private static string EmbeddingText(MeshNode node)
        => string.Join(" ", new[] { node.Name, node.NodeType }.Where(s => !string.IsNullOrEmpty(s)));

    private async Task<float[]?> EmbedSafe(ITextEmbedder embedder, string text, CancellationToken ct)
    {
        try { return await embedder.EmbedAsync(text, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            // Embeddings are a best-effort search index, not data: a transient embed failure must
            // NOT fail the write. Surface it (don't silently swallow) — the row stores a NULL
            // embedding and becomes searchable on its next write.
            _logger?.LogWarning(ex, "[Sqlite] embedding failed; storing NULL embedding for the node");
            return null;
        }
    }

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBlob(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
        return vector;
    }

    /// <summary>
    /// Snapshot of every node that carries an embedding, paired with its vector — the brute-force
    /// candidate set for <see cref="SqliteVectorMeshQuery"/>'s cosine ranking. One pooled scan;
    /// fine at the local/single-user scale SQLite targets.
    /// </summary>
    public IObservable<IReadOnlyList<(MeshNode Node, float[] Embedding)>> AllEmbeddings(JsonSerializerOptions options)
        => _ioPool.InvokeBlocking<IReadOnlyList<(MeshNode, float[])>>(_ =>
        {
            var result = new List<(MeshNode, float[])>();
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT data, embedding FROM mesh_nodes WHERE embedding IS NOT NULL";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(1)) continue;
                    if (JsonSerializer.Deserialize<MeshNode>(r.GetString(0), options) is not { } node) continue;
                    result.Add((node, FromBlob((byte[])r[1])));
                }
            }
            return result;
        });

    public IObservable<string> Delete(string path)
        => _ioPool.InvokeBlocking(_ =>
        {
            var p = Norm(path);
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM mesh_nodes WHERE path = $p";
                cmd.Parameters.AddWithValue("$p", p);
                cmd.ExecuteNonQuery();
            }
            try { _changes.OnNext(DataChangeNotification.Deleted(p, null)); } catch { /* never throw */ }
            return path;
        });

    public IObservable<bool> Exists(string path)
        => _ioPool.InvokeBlocking(_ =>
        {
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM mesh_nodes WHERE path = $p LIMIT 1";
                cmd.Parameters.AddWithValue("$p", Norm(path));
                return cmd.ExecuteScalar() is not null;
            }
        });

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => _ioPool.InvokeBlocking<(IEnumerable<string>, IEnumerable<string>)>(_ =>
        {
            var normalized = Norm(parentPath);
            var prefix = normalized.Length == 0 ? "" : normalized + "/";
            var expectedDepth = normalized.Length == 0
                ? 1
                : normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Length + 1;

            var all = new List<string>();
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                if (prefix.Length == 0)
                    cmd.CommandText = "SELECT path FROM mesh_nodes";
                else
                {
                    cmd.CommandText = "SELECT path FROM mesh_nodes WHERE path = $pp OR path LIKE $pfx";
                    cmd.Parameters.AddWithValue("$pp", normalized);
                    cmd.Parameters.AddWithValue("$pfx", prefix + "%");
                }
                using var r = cmd.ExecuteReader();
                while (r.Read()) { var k = r.GetString(0); all.Add(k); present.Add(k); }
            }

            // Depth logic mirrors InMemoryStorageAdapter so GetDescendants behaves identically.
            var nodePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in all)
            {
                if (prefix.Length == 0 && k.Contains('/')) { directoryPaths.Add(k.Split('/', 2)[0]); continue; }
                var segments = k.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == expectedDepth) nodePaths.Add(k);
                else if (segments.Length > expectedDepth)
                {
                    var dir = string.Join("/", segments.Take(expectedDepth));
                    if (!present.Contains(dir)) directoryPaths.Add(dir);
                }
            }
            return ((IEnumerable<string>)nodePaths, (IEnumerable<string>)directoryPaths);
        });

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => _ioPool.InvokeBlocking<(MeshNode?, int)>(_ =>
        {
            var normalized = Norm(fullPath);
            if (normalized.Length == 0) return (null, 0);
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                // Longest path that equals or is a prefix of the requested path — one round-trip.
                cmd.CommandText =
                    "SELECT path, data FROM mesh_nodes WHERE path = $p OR $p LIKE path || '/%' ORDER BY LENGTH(path) DESC LIMIT 1";
                cmd.Parameters.AddWithValue("$p", normalized);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return (null, 0);
                var matchedPath = r.GetString(0);
                var node = JsonSerializer.Deserialize<MeshNode>(r.GetString(1), options);
                return (node, matchedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length);
            }
        });

    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(string fullPath, JsonSerializerOptions options)
        => FindBestPrefixMatch(fullPath, options);

    // Partition objects: the whole collection is stored as one JSON array per partition key.
    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => _ioPool.InvokeBlocking(_ =>
        {
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT data FROM partition_objects WHERE partition_key = $k";
                cmd.Parameters.AddWithValue("$k", PartitionKey(nodePath, subPath));
                return cmd.ExecuteScalar() is string json
                    ? (JsonSerializer.Deserialize<List<object>>(json, options) ?? new()).ToArray()
                    : [];
            }
        }).SelectMany(arr => arr.ToObservable());

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => _ioPool.InvokeBlocking(_ =>
        {
            var json = JsonSerializer.Serialize(objects, options);
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO partition_objects(partition_key, data, last_modified)
                    VALUES ($k, $d, $t)
                    ON CONFLICT(partition_key) DO UPDATE SET data = $d, last_modified = $t
                    """;
                cmd.Parameters.AddWithValue("$k", PartitionKey(nodePath, subPath));
                cmd.Parameters.AddWithValue("$d", json);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
            return Unit.Default;
        });

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => _ioPool.InvokeBlocking(_ =>
        {
            var key = PartitionKey(nodePath, subPath);
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM partition_objects WHERE partition_key = $k OR partition_key LIKE $kp";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$kp", key + "/%");
                cmd.ExecuteNonQuery();
            }
            return Unit.Default;
        });

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => _ioPool.InvokeBlocking<DateTimeOffset?>(_ =>
        {
            var key = PartitionKey(nodePath, subPath);
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT MAX(last_modified) FROM partition_objects WHERE partition_key = $k OR partition_key LIKE $kp";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$kp", key + "/%");
                return cmd.ExecuteScalar() is string s && DateTimeOffset.TryParse(s, out var ts) ? ts : null;
            }
        });

    private static string PartitionKey(string nodePath, string? subPath)
    {
        var key = Norm(nodePath);
        return string.IsNullOrEmpty(subPath) ? key : $"{key}/{Norm(subPath)}";
    }

    public void Dispose()
    {
        try { _changes.OnCompleted(); } catch { /* ignore */ }
        _changes.Dispose();
        _connection.Dispose();
    }
}
