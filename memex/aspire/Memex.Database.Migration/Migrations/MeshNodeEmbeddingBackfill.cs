using MeshWeaver.Hosting.Embeddings;
using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// General mesh-node embedding backfill — the cross-partition counterpart of
/// <see cref="DocumentationBackfill"/> (which only handles the <c>doc</c> schema). For EVERY partition
/// schema's <c>mesh_nodes</c> table it:
/// <list type="number">
///   <item>reconciles the <c>embedding</c> column to the embedding provider's dimension — a model change
///   (e.g. default 1536 → bge-m3 1024) re-sizes the column (NULLing it) and rebuilds the HNSW index; and</item>
///   <item>embeds every row that has no embedding yet, from <c>name + node_type</c> (the same text the
///   write path <see cref="PostgreSqlStorageAdapter"/> embeds).</item>
/// </list>
/// Always-run when a provider is configured; a strict ENHANCEMENT — the hybrid query already surfaces
/// un-embedded rows lexically, so this only adds semantic ranking. Idempotent: only NULL-embedding rows
/// are embedded, so re-runs are cheap. Embedding failures are logged + skipped, never aborting the migration.
/// </summary>
public static class MeshNodeEmbeddingBackfill
{
    private const int LogEvery = 200;

    public static async Task RunAsync(
        NpgsqlDataSource baseDataSource,
        PostgreSqlStorageOptions options,
        string connectionString,
        IEmbeddingProvider? embeddingProvider,
        ILogger logger)
    {
        if (embeddingProvider is null)
        {
            logger.LogInformation("[EmbeddingBackfill] no embedding provider configured — skipped (hybrid/FTS search only)");
            return;
        }
        // The PROVIDER's dimension is the source of truth (bge-m3 = 1024), independent of any stale
        // options.VectorDimensions — every column is reconciled to it.
        var dim = embeddingProvider.Dimensions;

        // Partition schemas that have a mesh_nodes table. public.mesh_nodes is empty by design (harmless
        // to reconcile); skip system schemas.
        var schemas = new List<string>();
        await using (var q = baseDataSource.CreateCommand(
            "SELECT table_schema FROM information_schema.tables WHERE table_name = 'mesh_nodes' " +
            "AND table_schema <> 'information_schema' AND table_schema NOT LIKE 'pg_%' ORDER BY table_schema"))
        await using (var r = await q.ExecuteReaderAsync())
            while (await r.ReadAsync()) schemas.Add(r.GetString(0));

        logger.LogInformation("[EmbeddingBackfill] {Count} partition schema(s); target dim {Dim} (options={Opt})",
            schemas.Count, dim, options.VectorDimensions);

        int totalEmbedded = 0, totalResized = 0, totalFailed = 0;
        foreach (var schema in schemas)
        {
            await using var ds = SchemaHelpers.BuildSchemaDataSource(connectionString, schema);

            // 1. Reconcile the column dimension to the provider's (resize NULLs existing vectors; the
            //    backfill below re-embeds them). Mirrors PostgreSqlSchemaInitializer's resize branch.
            var resized = await ReconcileDimAsync(ds, schema, dim, logger);
            if (resized) totalResized++;

            // 2. Backfill rows with no embedding. Embed name + node_type (the write path's text).
            var rows = new List<(string Ns, string Id, string Text)>();
            await using (var sel = ds.CreateCommand(
                "SELECT namespace, id, TRIM(COALESCE(name,'') || ' ' || COALESCE(node_type,'')) " +
                "FROM mesh_nodes WHERE embedding IS NULL AND (COALESCE(name,'') <> '' OR COALESCE(node_type,'') <> '')"))
            await using (var rdr = await sel.ExecuteReaderAsync())
                while (await rdr.ReadAsync()) rows.Add((rdr.GetString(0), rdr.GetString(1), rdr.GetString(2)));

            int embedded = 0;
            foreach (var (ns, id, text) in rows)
            {
                float[]? vec;
                try { vec = await embeddingProvider.GenerateEmbeddingAsync(text); }
                catch (Exception ex)
                {
                    totalFailed++;
                    if (totalFailed <= 3) logger.LogWarning(ex, "[EmbeddingBackfill] embed failed {Schema} {Ns}/{Id}", schema, ns, id);
                    continue;
                }
                if (vec is null) continue;
                await using var upd = ds.CreateCommand("UPDATE mesh_nodes SET embedding = $1 WHERE namespace = $2 AND id = $3");
                upd.Parameters.AddWithValue(new Vector(vec));
                upd.Parameters.AddWithValue(ns);
                upd.Parameters.AddWithValue(id);
                await upd.ExecuteNonQueryAsync();
                embedded++;
                if (embedded % LogEvery == 0)
                    logger.LogInformation("[EmbeddingBackfill] {Schema}: {Embedded}/{Total}…", schema, embedded, rows.Count);
            }
            totalEmbedded += embedded;
            if (embedded > 0 || resized)
                logger.LogInformation("[EmbeddingBackfill] {Schema}: {Embedded} embedded{Resized}",
                    schema, embedded, resized ? " (column resized → re-embedded)" : "");
        }
        logger.LogInformation(
            "[EmbeddingBackfill] done: {Embedded} node(s) embedded across {Schemas} schema(s), {Resized} column(s) resized, {Failed} failed",
            totalEmbedded, schemas.Count, totalResized, totalFailed);
    }

    /// <summary>
    /// Resize the schema's <c>mesh_nodes.embedding</c> column to <paramref name="dim"/> if it differs,
    /// dropping then rebuilding the HNSW index (the index must be dropped before ALTER TYPE). atttypmod
    /// holds the pgvector dimension directly (consistent with PostgreSqlSchemaInitializer's resize).
    /// </summary>
    private static async Task<bool> ReconcileDimAsync(NpgsqlDataSource ds, string schema, int dim, ILogger logger)
    {
        int? cur = null;
        await using (var q = ds.CreateCommand(
            "SELECT atttypmod FROM pg_attribute WHERE attrelid = 'mesh_nodes'::regclass AND attname = 'embedding' AND atttypmod > 0"))
        {
            if (await q.ExecuteScalarAsync() is int m) cur = m;
        }
        if (cur == dim) return false;

        await using (var cmd = ds.CreateCommand(
            "DROP INDEX IF EXISTS idx_mn_embedding; " +
            $"ALTER TABLE mesh_nodes ALTER COLUMN embedding TYPE vector({dim}) USING NULL; " +
            "CREATE INDEX idx_mn_embedding ON mesh_nodes USING hnsw (embedding vector_cosine_ops)"))
            await cmd.ExecuteNonQueryAsync();
        logger.LogInformation("[EmbeddingBackfill] {Schema}: resized embedding column {Cur}→{Dim}",
            schema, cur?.ToString() ?? "none", dim);
        return true;
    }
}
