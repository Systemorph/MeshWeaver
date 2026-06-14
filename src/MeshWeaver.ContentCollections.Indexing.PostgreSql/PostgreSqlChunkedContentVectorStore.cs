using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace MeshWeaver.ContentCollections.Indexing.PostgreSql;

/// <summary>
/// Separate-Postgres (pgvector) <see cref="IChunkedContentVectorStore"/>. Owns its OWN
/// <see cref="NpgsqlDataSource"/> (built with <c>.UseVector()</c>) against a vector connection
/// string that is independent of the mesh's primary storage Postgres — the indexing vector store
/// can live in its own database/server.
///
/// <para><b>Reactive + pooled, mirroring <c>PostgreSqlPartitionStorageProvider</c>.</b> Every public
/// method composes <see cref="EnsureProvisioned"/> then runs the real DB round-trip through the
/// per-adapter <see cref="IIoPool"/> (<c>pg:vector</c>, cap 1 = one Npgsql connection — the gate IS
/// the connection). The ONLY async/await lives inside the <c>_pool.Invoke(async ct =&gt; ...)</c>
/// leaves; the public surface is <see cref="IObservable{T}"/>. There is NO
/// <c>Observable.FromAsync</c> anywhere (forbidden — see ControlledIoPooling.md).</para>
///
/// <para>Provisioning is a promise-cache: an instance <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// (never static) holds the single eager <see cref="IoPoolExtensions.Run"/> observable so the
/// CREATE EXTENSION + CREATE TABLE DDL runs at most once per store, replayed to every later
/// subscriber.</para>
/// </summary>
public sealed class PostgreSqlChunkedContentVectorStore : IChunkedContentVectorStore, IDisposable
{
    private const string ProvisionKey = "content_chunks";

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _dimensions;
    private readonly IIoPool _pool;

    /// <summary>
    /// Promise-cache of the one-shot CREATE EXTENSION + CREATE TABLE provisioning. Instance field
    /// (never static) so its lifetime is this store's. <see cref="IoPoolExtensions.Run"/> is eager +
    /// <see cref="System.Reactive.Subjects.ReplaySubject{T}"/>-backed: the first subscriber kicks the
    /// DDL off on the pool, every later subscriber replays the cached completion.
    /// </summary>
    private readonly ConcurrentDictionary<string, IObservable<Unit>> _provisioned = new(StringComparer.Ordinal);

    /// <param name="vectorConnectionString">
    /// Connection string for the SEPARATE pgvector store (its own DB/server, independent of the
    /// mesh's primary storage Postgres).
    /// </param>
    /// <param name="ioPoolRegistry">
    /// Mesh-scoped pool resolver. The store resolves the cap-1 <c>pg:vector</c> write pool from it
    /// so the gate mirrors this adapter's single Npgsql connection. Falls back to
    /// <see cref="IoPool.Unbounded"/> only when constructed outside DI (tests) — still off the hub
    /// scheduler, never <c>FromAsync</c>.
    /// </param>
    /// <param name="dimensions">Embedding dimensionality — the <c>vector({dim})</c> column width.</param>
    public PostgreSqlChunkedContentVectorStore(
        string vectorConnectionString,
        IoPoolRegistry? ioPoolRegistry,
        int dimensions)
    {
        if (string.IsNullOrWhiteSpace(vectorConnectionString))
            throw new ArgumentException("Vector connection string is required.", nameof(vectorConnectionString));
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Embedding dimensionality must be positive.");

        _dimensions = dimensions;

        // Own NpgsqlDataSource with the pgvector type plugin, mirroring every other PG adapter
        // (PostgreSqlPartitionStorageProvider / PostgreSqlStorageAdapterFactory). Cap the pool at 1
        // so the single connection matches the cap-1 pg:vector IIoPool gate.
        var csb = new NpgsqlConnectionStringBuilder(vectorConnectionString)
        {
            MaxPoolSize = 1,
            ConnectionIdleLifetime = 15,
        };
        var dsBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        dsBuilder.UseVector();
        _dataSource = dsBuilder.Build();

        _pool = ioPoolRegistry?.Get($"{IoPoolNames.PostgresAdapterPrefix}vector") ?? IoPool.Unbounded;
    }

    /// <summary>The pgvector store the indexer writes/queries.</summary>
    internal NpgsqlDataSource DataSource => _dataSource;

    /// <summary>
    /// Idempotent CREATE EXTENSION + CREATE TABLE provisioning. Promise-cached: runs at most once
    /// per store on the cap-1 <c>pg:vector</c> pool, replayed to every later subscriber.
    /// </summary>
    public IObservable<Unit> EnsureProvisioned() =>
        _provisioned.GetOrAdd(ProvisionKey, _ =>
            _pool.Run(async ct =>
            {
                // CREATE EXTENSION as plain SQL first (works even before UseVector() can resolve the
                // type OID), then reload the type catalog so the vector parameters bind, then the
                // tables/indexes — exactly the order PostgreSqlSchemaInitializer.InitializeAsync uses.
                await using (var ext = _dataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector"))
                    await ext.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                await using (var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
                    await conn.ReloadTypesAsync(ct).ConfigureAwait(false);

                await using (var ddl = _dataSource.CreateCommand(PostgreSqlContentChunkSchema.GetSchemaScript(_dimensions)))
                    await ddl.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                return Unit.Default;
            }));

    /// <inheritdoc/>
    public IObservable<string?> GetFileHash(string collectionPath, string filePath) =>
        EnsureProvisioned().SelectMany(_ => _pool.Invoke<string?>(async ct =>
        {
            await using var cmd = _dataSource.CreateCommand(
                "SELECT content_hash FROM content_files WHERE collection_path = $1 AND file_path = $2");
            cmd.Parameters.AddWithValue(collectionPath);
            cmd.Parameters.AddWithValue(filePath);
            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return scalar as string;
        }));

    /// <inheritdoc/>
    public IObservable<Unit> ReplaceFileChunks(
        string collectionPath, string filePath, IReadOnlyList<ContentChunk> chunks) =>
        EnsureProvisioned().SelectMany(_ => _pool.Invoke(async ct =>
        {
            var snapshot = chunks as ContentChunk[] ?? chunks.ToArray();

            await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            // 1. Drop every existing chunk for this file (delete-then-insert == atomic replace, no dupes).
            await using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM content_chunks WHERE collection_path = $1 AND file_path = $2";
                del.Parameters.AddWithValue(collectionPath);
                del.Parameters.AddWithValue(filePath);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (snapshot.Length == 0)
            {
                // No chunks → the file produced no indexable text. Clear its hash so a later
                // content gain forces a re-attempt (mirrors InMemoryChunkedContentVectorStore).
                await using (var delFile = conn.CreateCommand())
                {
                    delFile.Transaction = tx;
                    delFile.CommandText = "DELETE FROM content_files WHERE collection_path = $1 AND file_path = $2";
                    delFile.Parameters.AddWithValue(collectionPath);
                    delFile.Parameters.AddWithValue(filePath);
                    await delFile.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return Unit.Default;
            }

            // 2. Upsert the file hash. Every chunk of a file carries the same whole-file
            //    ContentHash; take the first.
            var fileHash = snapshot[0].ContentHash;
            await using (var upsert = conn.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT INTO content_files (collection_path, file_path, content_hash, last_modified)
                    VALUES ($1, $2, $3, NOW())
                    ON CONFLICT (collection_path, file_path) DO UPDATE
                        SET content_hash = EXCLUDED.content_hash, last_modified = NOW()
                    """;
                upsert.Parameters.AddWithValue(collectionPath);
                upsert.Parameters.AddWithValue(filePath);
                upsert.Parameters.AddWithValue(fileHash);
                await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // 3. Bulk-insert the new chunk set.
            foreach (var chunk in snapshot)
            {
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO content_chunks
                        (collection_path, file_path, chunk_index, source_address, content_hash,
                         chunk_text, metadata, embedding, last_modified)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, NOW())
                    """;
                ins.Parameters.AddWithValue(chunk.CollectionPath);
                ins.Parameters.AddWithValue(chunk.FilePath);
                ins.Parameters.AddWithValue(chunk.ChunkIndex);
                // source_address: not carried on ContentChunk; reserve the column, store NULL.
                ins.Parameters.AddWithValue(DBNull.Value);
                ins.Parameters.AddWithValue((object?)chunk.ContentHash ?? DBNull.Value);
                ins.Parameters.AddWithValue((object?)chunk.Text ?? DBNull.Value);
                ins.Parameters.Add(new NpgsqlParameter
                {
                    Value = chunk.Metadata is { Count: > 0 }
                        ? JsonSerializer.Serialize(chunk.Metadata)
                        : (object)DBNull.Value,
                    NpgsqlDbType = NpgsqlDbType.Jsonb,
                });
                if (chunk.Embedding is { Length: > 0 })
                    ins.Parameters.AddWithValue(new Vector(chunk.Embedding));
                else
                    ins.Parameters.AddWithValue(DBNull.Value);
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return Unit.Default;
        }));

    /// <inheritdoc/>
    public IObservable<IReadOnlyList<ContentChunk>> Search(string collectionPath, float[] query, int topK) =>
        EnsureProvisioned().SelectMany(_ => _pool.Invoke<IReadOnlyList<ContentChunk>>(async ct =>
        {
            await using var cmd = _dataSource.CreateCommand("""
                SELECT collection_path, file_path, chunk_index, content_hash, chunk_text, metadata, embedding
                FROM content_chunks
                WHERE collection_path = $1 AND embedding IS NOT NULL
                ORDER BY embedding <=> $2
                LIMIT $3
                """);
            cmd.Parameters.AddWithValue(collectionPath);
            cmd.Parameters.AddWithValue(new Vector(query));
            cmd.Parameters.AddWithValue(Math.Max(0, topK));

            var results = new List<ContentChunk>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ImmutableDictionary<string, string>? metadata = null;
                if (!await reader.IsDBNullAsync(5, ct).ConfigureAwait(false))
                {
                    var json = reader.GetString(5);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict is { Count: > 0 })
                        metadata = dict.ToImmutableDictionary(StringComparer.Ordinal);
                }

                float[]? embedding = null;
                if (!await reader.IsDBNullAsync(6, ct).ConfigureAwait(false))
                    embedding = reader.GetFieldValue<Vector>(6).ToArray();

                results.Add(new ContentChunk(
                    CollectionPath: reader.GetString(0),
                    FilePath: reader.GetString(1),
                    ChunkIndex: reader.GetInt32(2),
                    Text: await reader.IsDBNullAsync(4, ct).ConfigureAwait(false) ? string.Empty : reader.GetString(4),
                    ContentHash: await reader.IsDBNullAsync(3, ct).ConfigureAwait(false) ? string.Empty : reader.GetString(3),
                    Embedding: embedding,
                    Metadata: metadata));
            }

            return results;
        }));

    /// <inheritdoc/>
    public void Dispose() => _dataSource.Dispose();
}
