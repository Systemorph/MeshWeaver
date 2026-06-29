using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Regression test for the <c>search_across_schemas</c> fan-out breaking on
/// <b>read-only / public schemas that have <c>mesh_nodes</c> but NO per-partition
/// permission tables</b> (the mirrored documentation lives in the <c>doc</c>
/// schema — created by <c>DocumentationBackfill</c> — which ships
/// <c>mesh_nodes</c> only, never <c>user_effective_permissions</c> /
/// <c>node_type_permissions</c>).
///
/// <para><b>The bug.</b> For an authenticated user the proc appended an access
/// sub-clause referencing <c>{schema}.user_effective_permissions</c> and
/// <c>{schema}.node_type_permissions</c> for EVERY searchable schema. When one of
/// those schemas (e.g. <c>doc</c>) lacked the tables, the whole UNION failed to
/// plan with <c>42P01 relation "doc.user_effective_permissions" does not exist</c>
/// → the entire fan-out returned nothing → every Space / cross-partition search
/// came back empty for every logged-in user (catalog blank). System (null user)
/// queries worked because they skip the access clause entirely.</para>
///
/// <para><b>The fix.</b> A schema with no <c>user_effective_permissions</c> table
/// is treated as PUBLIC read-only content — its rows are included unfiltered and
/// the access sub-clause is skipped (guarded with <c>to_regclass</c>).
/// Access-controlled partitions still get the full
/// <c>partition_access</c> + node-permission check.</para>
/// </summary>
[Collection("PostgreSql")]
public class SearchAcrossSchemasReadOnlyTest
{
    private readonly PostgreSqlFixture _fixture;

    public SearchAcrossSchemasReadOnlyTest(PostgreSqlFixture fixture) => _fixture = fixture;

    private const string Cols =
        "t(id TEXT, namespace TEXT, name TEXT, node_type TEXT, category TEXT, icon TEXT, " +
        "display_order INT, last_modified TIMESTAMPTZ, version BIGINT, state SMALLINT, " +
        "content JSONB, desired_id TEXT, main_node TEXT)";

    [Fact(Timeout = 120000)]
    public async Task SearchAcrossSchemas_ReadOnlySchemaWithoutPermissionTables_NoError_RowsArePublic()
    {
        // Clean slate for the two schemas this test owns.
        await ExecAsync("DROP SCHEMA IF EXISTS acme CASCADE; DROP SCHEMA IF EXISTS docpub CASCADE;");

        // A normal access-controlled partition + the public objects (proc,
        // searchable_schemas, partition_access) the proc reads from.
        await InitSchemaAsync("acme");
        // A read-only/public schema (doc-style): full init, then DROP the
        // per-partition permission tables so it has mesh_nodes ONLY — exactly
        // the shape DocumentationBackfill leaves the `doc` schema in.
        await InitSchemaAsync("docpub");
        await ExecAsync("""
            DROP TABLE IF EXISTS docpub.user_effective_permissions;
            DROP TABLE IF EXISTS docpub.user_effective_permissions_shadow;
            DROP TABLE IF EXISTS docpub.node_type_permissions;
            """);

        // One public row in the read-only schema.
        await ExecAsync("""
            INSERT INTO docpub.mesh_nodes (namespace, id, name, node_type, state, version, main_node)
            VALUES ('', 'doc1', 'Doc One', 'Markdown', 2, 1, 'doc1');
            """);

        // Only these two schemas participate in the fan-out for this test.
        await ExecAsync("""
            DELETE FROM public.searchable_schemas;
            INSERT INTO public.searchable_schemas (schema_name) VALUES ('acme'), ('docpub');
            """);

        // BEFORE the fix this throws PostgresException 42P01
        // ("relation docpub.user_effective_permissions does not exist").
        var idsAuth = await SearchAsAsync("alice");
        idsAuth.Should().Contain("doc1",
            "a searchable schema without permission tables is read-only/public — it must be " +
            "included unfiltered, not break the whole authenticated fan-out with 42P01");

        // Sanity: the same query as system (no access filter) also returns it.
        var idsSystem = await SearchAsAsync(null);
        idsSystem.Should().Contain("doc1");
    }

    private async Task ExecAsync(string sql)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> SearchAsAsync(string? userId)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            $"SELECT id FROM public.search_across_schemas('', @u, 'n.last_modified DESC', 50) AS {Cols}");
        cmd.Parameters.Add(new NpgsqlParameter("u", (object?)userId ?? System.DBNull.Value));
        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private async Task InitSchemaAsync(string schema)
    {
        await ExecAsync($"CREATE SCHEMA IF NOT EXISTS \"{schema}\";");
        var opts = new PostgreSqlStorageOptions { Schema = schema };
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            SearchPath = $"{schema},public",
            MaxPoolSize = 1
        };
        var dsBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        dsBuilder.UseVector();
        await using var ds = dsBuilder.Build();
        await PostgreSqlSchemaInitializer.InitializeAsync(ds, opts);
    }
}
