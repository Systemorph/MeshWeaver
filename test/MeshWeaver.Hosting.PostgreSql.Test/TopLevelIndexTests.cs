using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// #16 — Central top-level index. Verifies the <c>public.rebuild_top_level_index()</c>
/// plpgsql function + the <c>public.top_level_index</c> MATERIALIZED VIEW: it materializes
/// exactly the <c>namespace=''</c> partition-root rows (one per partition) from every
/// searchable schema, WITHOUT copying/fanning out, so top-level autocomplete reads one
/// small indexed relation. The matview is the (a) "top-level" half of the autocomplete
/// composition (the (b) "within-partition" half is a scoped <c>namespace:&lt;path&gt;
/// scope:descendants</c> query against the single context partition — neither fans out).
/// <para>Node creation goes through the normal reactive <c>CreateNode</c>; the matview
/// reads use synchronous Npgsql (no async/await).</para>
/// </summary>
[Collection("PostgreSql")]
public class TopLevelIndexTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddGraph();
    }

    // Production sync shape: refresh searchable_schemas from information_schema, then
    // re-materialize the top-level index (the same two steps SyncSearchableSchemas /
    // SearchableSchemasUpdater run). Test-side so we don't race the 30s sync TTL.
    private const string SyncAndRebuildSql = """
        DELETE FROM public.searchable_schemas;
        INSERT INTO public.searchable_schemas (schema_name)
          SELECT s.schema_name FROM information_schema.schemata s
          WHERE EXISTS (SELECT 1 FROM information_schema.tables t
                        WHERE t.table_schema = s.schema_name AND t.table_name = 'mesh_nodes')
            AND s.schema_name NOT IN ('public','information_schema','pg_catalog','pg_toast','admin')
            AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\';
        SELECT public.rebuild_top_level_index();
        """;

    [Fact(Timeout = 60000)]
    public void TopLevelIndex_MaterializesPartitionRoots_WithoutFanOut()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Two partitions. A partition root is a single-segment path → (namespace='', id=partitionName);
        // the path router lazily CREATE SCHEMAs each on first write.
        var p1 = $"tli{Guid.NewGuid():N}".ToLowerInvariant()[..14];
        var p2 = $"tli{Guid.NewGuid():N}".ToLowerInvariant()[..14];

        meshService.CreateNode(new MeshNode(p1)
        { NodeType = "Markdown", Name = $"Space {p1}", State = MeshNodeState.Active })
            .Should().Within(30.Seconds()).Emit();
        meshService.CreateNode(new MeshNode(p2)
        { NodeType = "Markdown", Name = $"Space {p2}", State = MeshNodeState.Active })
            .Should().Within(30.Seconds()).Emit();

        // Also write a CHILD node in p1 — namespace=p1, id=child → it must NOT appear in the
        // top-level index (only namespace='' partition roots do).
        meshService.CreateNode(new MeshNode("child", p1)
        { NodeType = "Markdown", Name = "child doc", State = MeshNodeState.Active })
            .Should().Within(30.Seconds()).Emit();

        using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        conn.Open();
        using (var sync = new NpgsqlCommand(SyncAndRebuildSql, conn))
            sync.ExecuteNonQuery();

        var rows = new List<(string Id, string Ns, string? NodeType)>();
        using (var q = new NpgsqlCommand(
            "SELECT id, namespace, node_type FROM public.top_level_index ORDER BY id", conn))
        using (var rdr = q.ExecuteReader())
        {
            while (rdr.Read())
                rows.Add((rdr.GetString(0), rdr.GetString(1), rdr.IsDBNull(2) ? null : rdr.GetString(2)));
        }

        var ids = rows.ConvertAll(r => r.Id);
        ids.Should().Contain(p1, "the top-level index must materialize partition p1's root");
        ids.Should().Contain(p2, "the top-level index must materialize partition p2's root");
        ids.Should().NotContain("child", "a within-partition child (namespace != '') is NOT a top-level node");
        rows.Should().OnlyContain(r => r.Ns == "",
            "the central index holds only namespace='' partition roots — one row per partition");
    }

    [Fact(Timeout = 60000)]
    public void RebuildTopLevelIndex_IsIdempotent_AndQueryableWhenEmpty()
    {
        using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        conn.Open();

        // Repeated rebuilds must not error (DROP MATERIALIZED VIEW IF EXISTS + CREATE), and the
        // matview must stay queryable (the empty-partition fallback creates a typed empty view).
        using (var rebuild = new NpgsqlCommand(
            "SELECT public.rebuild_top_level_index(); SELECT public.rebuild_top_level_index();", conn))
            rebuild.ExecuteNonQuery();

        using (var count = new NpgsqlCommand("SELECT COUNT(*) FROM public.top_level_index", conn))
        {
            var n = count.ExecuteScalar();
            Convert.ToInt64(n).Should().BeGreaterThanOrEqualTo(0,
                "the matview is queryable after repeated rebuilds");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task AutocompleteTopLevel_ReturnsScoredMatches_FromMatview_NoFanOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var crossSchema = Mesh.ServiceProvider.GetRequiredService<ICrossSchemaQueryProvider>();

        var token = $"ac{Guid.NewGuid():N}".ToLowerInvariant()[..12];
        var pMatch = $"{token}x";                                  // id + name contain the prefix
        var pOther = $"zz{Guid.NewGuid():N}".ToLowerInvariant()[..12]; // unrelated

        meshService.CreateNode(new MeshNode(pMatch)
        { NodeType = "Markdown", Name = $"{token} space", State = MeshNodeState.Active })
            .Should().Within(30.Seconds()).Emit();
        meshService.CreateNode(new MeshNode(pOther)
        { NodeType = "Markdown", Name = "Unrelated", State = MeshNodeState.Active })
            .Should().Within(30.Seconds()).Emit();

        // Re-materialize the top-level index from the current schema set (prod path).
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var sync = new NpgsqlCommand(SyncAndRebuildSql, conn);
            await sync.ExecuteNonQueryAsync(ct);
        }

        // userId=null → system (no access filter): top-level autocomplete reads ONLY the
        // matview (never a cross-schema fan-out) and PG assigns the relevance score.
        var results = await crossSchema.AutocompleteTopLevelAsync(token, userId: null, limit: 20, ct);

        results.Should().Contain(r => r.Path == pMatch,
            "the partition root whose id/name contains the prefix is a top-level match");
        results.Should().NotContain(r => r.Path == pOther,
            "an unrelated root must not match the prefix");
        results.Where(r => r.Path == pMatch).Should().OnlyContain(r => r.Score > 0,
            "PG assigns a positive relevance score to a match (results sort by score, not alphabetically)");
    }
}
