using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// 🚨 AN UNPINNED CROSS-SCHEMA QUERY RETURNS EVERY MATCH — pinned on the overload the RUNTIME takes.
///
/// <para><c>PostgreSqlCrossSchemaQueryProvider</c> serves every query that carries no
/// <c>path:</c> and no <c>namespace:</c> by UNION-ing every partition schema and ordering by
/// <c>last_modified DESC</c>. It applies NO default clip: a caller that states no limit gets the
/// whole set, and a caller that states one gets exactly that many.</para>
///
/// <para><b>Twice in production, when it did clip.</b> #1216: the batch bake's global
/// <c>nodeType:Code</c> fetch resolved 50 Code nodes for 237 pending types, and 169 types compiled
/// against nothing. #1326: the GitHub webhook's <c>nodeType:GitHubSyncConfig</c> fan-out saw 50 of
/// the mesh's configs, so 9 of 43 Spaces never re-synced while every delivery reported success.
/// Both are self-reinforcing: processing a row rewrites it, which refreshes <c>last_modified</c>,
/// which keeps the rows that DID get processed inside the window and pushes the stragglers further
/// out — so the same handful stays stale forever and the set never heals.</para>
///
/// <para>🚨 <b>This test used to assert the 50-row page, and that is exactly what made it
/// worthless.</b> The default lived on a SECOND fan-out shape — the <c>search_across_schemas</c>
/// paging overload — that no runtime caller could reach:
/// <c>PostgreSqlPartitionedMeshQuery.EnumerateFanOutAsync</c>, the path for every unpinned query,
/// has only ever taken the table-name overload. So the test demonstrated a truncation the runtime
/// could not produce, and <c>Complete()</c> became unfalsifiable (#2048). The paging overload is
/// deleted; this now runs against <c>"mesh_nodes"</c> — the same call the runtime makes — and
/// fails if a default clip is ever reintroduced there.</para>
/// </summary>
[Collection("PostgreSql")]
public class CompleteEnumerationFanOutTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    private readonly PostgreSqlFixture _fixture = fixture;
    private readonly JsonSerializerOptions _options = new();

    /// <summary>
    /// Rows per partition. Two partitions ⇒ 66, comfortably above the 50 the deleted paging shape
    /// substituted — at or below that number a reintroduced default clip would pass unnoticed.
    /// </summary>
    private const int RowsPerPartition = 33;

    private const int TotalRows = 2 * RowsPerPartition;

    /// <summary>The two seeded partitions — the UNION's branches, exactly as the runtime passes
    /// them (resolved from <c>public.searchable_schemas</c> by the query layer above).</summary>
    private static readonly string[] Schemas = ["alpha", "beta"];

    /// <summary>A node type nothing else in the fixture writes, so the fan-out's result set is
    /// exactly what this test seeded.</summary>
    private const string ProbeNodeType = "FanOutEnumerationProbe";

    [Fact(Timeout = 120000)]
    public async Task UnpinnedFanOut_ReturnsEveryMatch_AndHonoursAStatedLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        await SeedAsync("alpha", "Alpha", partitionDef, ct);
        await SeedAsync("beta", "Beta", partitionDef, ct);

        var cross = new PostgreSqlCrossSchemaQueryProvider(_fixture.DataSource)
        {
            SyncTtl = System.TimeSpan.Zero
        };
        await cross.SyncSearchableSchemasAsync(ct);

        var parser = new QueryParser();
        var parsed = parser.Parse($"nodeType:{ProbeNodeType}");

        // 1. No limit stated → EVERY match. TotalRows is deliberately well above the 50 the
        //    deleted paging shape used to substitute, so a reintroduced default cannot pass here.
        var unlimited = await Collect(cross, parsed, ct);
        output.WriteLine($"no limit stated: {unlimited.Count} of {TotalRows}");
        unlimited.Count.Should().Be(TotalRows,
            "the runtime fan-out applies no default clip — a caller that states no limit is not "
            + "handed a page it cannot distinguish from the whole set (#1216, #1326, #2048)");
        unlimited.Select(n => n.Path).Distinct().Count().Should().Be(TotalRows,
            "the result must be the union of both partitions, not one partition twice");

        // 2. Complete() → every match, same answer. NoLimit is non-positive, and the one way it
        //    could go wrong is reaching SQL as a literal `LIMIT -1`.
        var complete = await Collect(cross, parsed with { Limit = MeshQueryRequest.NoLimit }, ct);
        output.WriteLine($"complete: {complete.Count} of {TotalRows}");
        complete.Count.Should().Be(TotalRows,
            "MeshQueryRequest.NoLimit declares the read an enumeration — every match must come "
            + "back, and it must never be emitted as LIMIT -1 or LIMIT 0");

        // 3. A stated POSITIVE limit is honoured — the absence of a DEFAULT is not the absence of
        //    limits.
        var ten = await Collect(cross, parsed with { Limit = 10 }, ct);
        ten.Count.Should().Be(10, "an explicit positive limit still clips");
    }

    /// <summary>
    /// The call the RUNTIME makes: <c>PostgreSqlPartitionedMeshQuery.EnumerateFanOutAsync</c>
    /// resolves a table (<c>"mesh_nodes"</c> for an ordinary query) and takes this overload for
    /// every unpinned read. Asserting through it is what makes the claims above falsifiable.
    /// </summary>
    private Task<System.Collections.Generic.List<MeshNode>> Collect(
        PostgreSqlCrossSchemaQueryProvider cross, ParsedQuery query, System.Threading.CancellationToken ct)
        => cross.QueryAcrossSchemasAsync(
                query, _options, Schemas, "mesh_nodes", userId: null, activityUserId: null, ct)
            .Collect(ct).Should().Within(60.Seconds()).Emit();

    private async Task SeedAsync(
        string schema, string ns, PartitionDefinition partitionDef, System.Threading.CancellationToken ct)
    {
        var (_, adapter) = await _fixture.CreateSchemaAdapterAsync(
            schema, partitionDef with { Namespace = ns, Schema = schema });

        for (var i = 0; i < RowsPerPartition; i++)
            await adapter.WriteAsync(
                new MeshNode($"Probe{i:D2}", ns)
                {
                    Name = $"Probe {i:D2}",
                    NodeType = ProbeNodeType,
                    State = MeshNodeState.Active
                },
                _options, ct);
    }
}
