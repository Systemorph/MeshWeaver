using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Documentation;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Mirrors the embedded MeshWeaver documentation into a Postgres <c>doc</c> schema so the docs
/// surface in the main search bar — both full-text (the <c>idx_mn_text_search</c> index over
/// name + description + node_type) and semantic vector search (the <c>idx_mn_embedding</c> HNSW
/// index). Reads/navigation still come from the in-memory <c>EmbeddedResourceStorageAdapter</c>
/// (the named partition rule out-ranks the Postgres wildcard provider), so these rows are a
/// pure search index — content is intentionally NULL.
///
/// <para><b>Always-run, NOT a versioned <see cref="IMigration"/>.</b> Docs change every release,
/// so this must refresh on every deploy on both fresh and existing DBs. It runs between Phase 2
/// (versioned repairs) and Phase 3 (searchable-schemas refresh) so Phase 3 automatically adds
/// <c>doc</c> to <c>public.searchable_schemas</c>.</para>
///
/// <para><b>Full replace + incremental embed.</b> Every run upserts the current doc set and
/// prunes rows whose source file no longer ships (<c>doc.mesh_nodes</c> exclusively holds these
/// docs). A per-doc content hash in <c>doc.documentation_index</c> means the (paid) embedding
/// call only fires when a doc's content actually changed — or when an embedding provider becomes
/// available for a row indexed earlier without one, or when the vector dimensions change.</para>
/// </summary>
public static class DocumentationBackfill
{
    private const string Schema = "doc";
    private const string Partition = "Doc"; // path prefix on the doc nodes (schema = lowercase)

    public static async Task RunAsync(
        NpgsqlDataSource baseDataSource,
        PostgreSqlStorageOptions options,
        string connectionString,
        IEmbeddingProvider? embeddingProvider,
        ILogger logger)
    {
        // 1. Schema + mesh/satellite tables (idempotent), plus our bookkeeping table.
        await using (var create = baseDataSource.CreateCommand($"CREATE SCHEMA IF NOT EXISTS \"{Schema}\""))
            await create.ExecuteNonQueryAsync();

        await using var ds = SchemaHelpers.BuildSchemaDataSource(connectionString, Schema);
        var schemaOptions = SchemaHelpers.BuildSchemaOptions(connectionString, Schema, options.VectorDimensions);

        await PostgreSqlSchemaInitializer.InitializeMeshTablesAsync(ds, schemaOptions);
        await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
            ds, schemaOptions, PartitionDefinition.DefaultSegmentTableMappings().Values);

        await using (var bk = ds.CreateCommand("""
            CREATE TABLE IF NOT EXISTS documentation_index (
                path         TEXT PRIMARY KEY,
                content_hash TEXT NOT NULL,
                embedded     BOOLEAN NOT NULL DEFAULT false,
                indexed_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )
            """))
            await bk.ExecuteNonQueryAsync();

        // 2. Load the source docs with adapter-aligned paths.
        var docs = DocumentationNodeProvider.LoadIndexableNodes();
        logger.LogInformation("[DocBackfill] {Count} documentation pages found ({Embeddings})",
            docs.Count, embeddingProvider != null ? "embeddings ON" : "embeddings OFF — FTS only");

        var existing = new Dictionary<string, (string Hash, bool Embedded)>(StringComparer.Ordinal);
        await using (var read = ds.CreateCommand("SELECT path, content_hash, embedded FROM documentation_index"))
        await using (var rdr = await read.ExecuteReaderAsync())
            while (await rdr.ReadAsync())
                existing[rdr.GetString(0)] = (rdr.GetString(1), rdr.GetBoolean(2));

        var currentPaths = new HashSet<string>(StringComparer.Ordinal);
        int upserted = 0, skipped = 0, embedded = 0;

        foreach (var node in docs)
        {
            var path = node.Path;
            currentPaths.Add(path);
            var hash = ComputeHash(node, options.VectorDimensions);

            // Re-index only when content changed — or when this row was indexed without an
            // embedding but a provider is now available.
            if (existing.TryGetValue(path, out var prev)
                && prev.Hash == hash
                && (prev.Embedded || embeddingProvider == null))
            {
                skipped++;
                continue;
            }

            float[]? vector = null;
            if (embeddingProvider != null)
            {
                try
                {
                    vector = await embeddingProvider.GenerateEmbeddingAsync(BuildEmbeddingText(node));
                }
                catch (Exception ex)
                {
                    // Never abort the migration on an embedding failure — FTS still works.
                    logger.LogWarning(ex, "[DocBackfill] embedding failed for {Path}", path);
                }
            }

            await UpsertNodeAsync(ds, node, vector);
            await UpsertHashAsync(ds, path, hash, vector != null);
            upserted++;
            if (vector != null) embedded++;
        }

        // 3. Full replace — drop rows whose source file no longer ships.
        await using (var prune = ds.CreateCommand("DELETE FROM mesh_nodes WHERE NOT (path = ANY($1))"))
        {
            prune.Parameters.AddWithValue(currentPaths.ToArray());
            var deleted = await prune.ExecuteNonQueryAsync();
            if (deleted > 0) logger.LogInformation("[DocBackfill] pruned {Deleted} stale doc rows", deleted);
        }
        await using (var pruneIdx = ds.CreateCommand("DELETE FROM documentation_index WHERE NOT (path = ANY($1))"))
        {
            pruneIdx.Parameters.AddWithValue(currentPaths.ToArray());
            await pruneIdx.ExecuteNonQueryAsync();
        }

        // 4. Public + Anonymous read so everyone can search the docs.
        await SeedAccessAsync(ds, logger);

        logger.LogInformation(
            "[DocBackfill] done: {Upserted} upserted ({Embedded} embedded), {Skipped} unchanged, {Total} total",
            upserted, embedded, skipped, docs.Count);
    }

    private static async Task UpsertNodeAsync(NpgsqlDataSource ds, MeshNode node, float[]? vector)
    {
        // content is intentionally NULL — docs render from the in-memory embedded partition;
        // these rows exist only to feed FTS (name + description) and vector (embedding) search.
        await using var cmd = ds.CreateCommand("""
            INSERT INTO mesh_nodes (namespace, id, name, description, node_type, category, icon,
                                    last_modified, version, state, content, embedding, main_node)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, NULL, $11, $12)
            ON CONFLICT (namespace, id) DO UPDATE SET
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                node_type = EXCLUDED.node_type,
                category = EXCLUDED.category,
                icon = EXCLUDED.icon,
                last_modified = EXCLUDED.last_modified,
                version = EXCLUDED.version,
                state = EXCLUDED.state,
                embedding = EXCLUDED.embedding,
                main_node = EXCLUDED.main_node
            """);
        cmd.Parameters.AddWithValue(node.Namespace ?? "");
        cmd.Parameters.AddWithValue(node.Id);
        cmd.Parameters.AddWithValue((object?)node.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.NodeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.Icon ?? DBNull.Value);
        cmd.Parameters.AddWithValue(node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified);
        cmd.Parameters.AddWithValue(node.Version <= 0 ? 1L : node.Version);
        cmd.Parameters.AddWithValue((short)node.State); // MeshNodeState.Active = 2
        if (vector != null)
            cmd.Parameters.AddWithValue(new Vector(vector));
        else
            cmd.Parameters.AddWithValue(DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.MainNode ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task UpsertHashAsync(NpgsqlDataSource ds, string path, string hash, bool embedded)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO documentation_index (path, content_hash, embedded, indexed_at)
            VALUES ($1, $2, $3, NOW())
            ON CONFLICT (path) DO UPDATE SET
                content_hash = EXCLUDED.content_hash,
                embedded = EXCLUDED.embedded,
                indexed_at = NOW()
            """);
        cmd.Parameters.AddWithValue(path);
        cmd.Parameters.AddWithValue(hash);
        cmd.Parameters.AddWithValue(embedded);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Seeds Public + Anonymous Viewer assignments into <c>doc.access</c> and rebuilds the
    /// schema's effective-permissions, which syncs <c>public.partition_access</c> (the partition
    /// gate the cross-schema search uses). camelCase content keys match what
    /// <c>rebuild_user_effective_permissions()</c> reads (<c>content-&gt;&gt;'accessObject'</c>).
    /// </summary>
    private static async Task SeedAccessAsync(NpgsqlDataSource ds, ILogger logger)
    {
        await using (var cmd = ds.CreateCommand("""
            INSERT INTO access (id, namespace, name, node_type, content, main_node, last_modified, version, state)
            VALUES
              ('Public_Access', 'Doc/_Access', 'Public Access', 'AccessAssignment',
                 jsonb_build_object('accessObject', 'Public', 'displayName', 'All authenticated users',
                   'roles', jsonb_build_array(jsonb_build_object('role', 'Viewer'))),
                 'Doc', NOW(), 1, 2),
              ('Anonymous_Access', 'Doc/_Access', 'Anonymous Access', 'AccessAssignment',
                 jsonb_build_object('accessObject', 'Anonymous', 'displayName', 'Unauthenticated visitors',
                   'roles', jsonb_build_array(jsonb_build_object('role', 'Viewer'))),
                 'Doc', NOW(), 1, 2)
            ON CONFLICT (namespace, id) DO UPDATE SET
                content = EXCLUDED.content,
                main_node = EXCLUDED.main_node,
                state = EXCLUDED.state
            """))
            await cmd.ExecuteNonQueryAsync();

        try
        {
            await using var rebuild = ds.CreateCommand("SELECT rebuild_user_effective_permissions()");
            await rebuild.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[DocBackfill] doc permission rebuild failed — docs may not be searchable yet");
        }
    }

    /// <summary>Title + category + description + a prose slice of the body — drives semantic search.</summary>
    private static string BuildEmbeddingText(MeshNode node)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(node.Name)) parts.Add(node.Name!);
        if (!string.IsNullOrWhiteSpace(node.Category)) parts.Add(node.Category!);
        if (!string.IsNullOrWhiteSpace(node.Description)) parts.Add(node.Description!);
        if (node.Content is MarkdownContent mc && !string.IsNullOrWhiteSpace(mc.Content))
            parts.Add(StripForEmbedding(mc.Content));
        var text = string.Join("\n", parts);
        return text.Length > 1800 ? text[..1800] : text;
    }

    private static string StripForEmbedding(string body)
    {
        var noFences = Regex.Replace(body, "```.*?```", " ", RegexOptions.Singleline);
        var noHtml = Regex.Replace(noFences, "<[^>]+>", " ");
        var collapsed = Regex.Replace(noHtml, @"\s+", " ").Trim();
        return collapsed.Length > 1500 ? collapsed[..1500] : collapsed;
    }

    private static string ComputeHash(MeshNode node, int dims)
    {
        var body = node.Content is MarkdownContent mc ? mc.Content : "";
        var raw = string.Join("",
            node.Name, node.Description, node.Category, node.Icon, node.NodeType, body, dims.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
