using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// 🚨 AN UNPINNED CROSS-SCHEMA QUERY ANSWERS WITH A PAGE, AND THE CALLER CANNOT TELL.
///
/// <para><c>PostgreSqlCrossSchemaQueryProvider</c> serves every query that carries no
/// <c>path:</c> and no <c>namespace:</c> by UNION-ing every partition schema, ordering by
/// <c>last_modified DESC</c> and clipping at a default row count. That is right for a search and
/// catastrophic for an ENUMERATION — and the two are the same call, so the mistake is invisible at
/// the call site and invisible in the result.</para>
///
/// <para><b>Twice in production.</b> #1216: the batch bake's global <c>nodeType:Code</c> fetch
/// resolved 50 Code nodes for 237 pending types, and 169 types compiled against nothing. #1326: the
/// GitHub webhook's <c>nodeType:GitHubSyncConfig</c> fan-out saw 50 of the mesh's configs, so 9 of
/// 43 Spaces never re-synced while every delivery reported success. Both are self-reinforcing:
/// processing a row rewrites it, which refreshes <c>last_modified</c>, which keeps the rows that
/// DID get processed inside the window and pushes the stragglers further out — so the same
/// handful stays stale forever and the set never heals.</para>
///
/// <para>This pins the cure: <see cref="MeshQueryRequest.NoLimit"/> (what
/// <c>MeshQueryRequest.Complete()</c> sets) means EVERY match, a stated positive limit means that
/// many, and only a caller that states nothing gets the page. The 50-row page is asserted too — it
/// is the behaviour the enumeration callers have to opt out of, so it must not drift silently.</para>
/// </summary>
[Collection("PostgreSql")]
public class CompleteEnumerationFanOutTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    private readonly PostgreSqlFixture _fixture = fixture;
    private readonly JsonSerializerOptions _options = new();

    /// <summary>
    /// Rows per partition. Two partitions ⇒ 66, comfortably above
    /// <see cref="PostgreSqlCrossSchemaQueryProvider.DefaultFanOutLimit"/> — at or below it the test
    /// reproduces nothing.
    /// </summary>
    private const int RowsPerPartition = 33;

    private const int TotalRows = 2 * RowsPerPartition;

    /// <summary>The two seeded partitions. The stored-procedure fan-out resolves its own schema
    /// list from <c>public.searchable_schemas</c>; the parameter is part of the shared signature.
    /// </summary>
    private static readonly string[] Schemas = ["alpha", "beta"];

    /// <summary>A node type nothing else in the fixture writes, so the fan-out's result set is
    /// exactly what this test seeded.</summary>
    private const string ProbeNodeType = "FanOutEnumerationProbe";

    [Fact(Timeout = 120000)]
    public async Task UnpinnedFanOut_PagesByDefault_ButReturnsEveryMatchWhenTheCallerDeclaresCompleteness()
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

        // 1. No limit stated → a PAGE. Documented, not accidental: an unanchored UNION over every
        //    partition schema needs a bound, and a search wants one anyway.
        var page = await cross.QueryAcrossSchemasAsync(parsed, _options, Schemas, ct: ct).Collect(ct)
            .Should().Within(60.Seconds()).Emit();
        output.WriteLine($"default page: {page.Count} of {TotalRows}");
        page.Count.Should().Be(PostgreSqlCrossSchemaQueryProvider.DefaultFanOutLimit,
            "a query that states no limit is served the default page — which is precisely why an "
            + "enumeration must say Complete() rather than trust the absence of a limit");

        // 2. Complete() → EVERY match. This is the assertion that fails before the fix: NoLimit is
        //    non-positive, and the provider used to fold every non-positive value into `?? 50`
        //    (after PostgreSqlPartitionedMeshQuery had already refused to propagate it at all).
        var complete = await cross
            .QueryAcrossSchemasAsync(parsed with { Limit = MeshQueryRequest.NoLimit }, _options, Schemas, ct: ct)
            .Collect(ct).Should().Within(60.Seconds()).Emit();
        output.WriteLine($"complete: {complete.Count} of {TotalRows}");
        complete.Count.Should().Be(TotalRows,
            "MeshQueryRequest.NoLimit declares the read an enumeration — every match must come back, "
            + "or a fan-out over sync sources silently stops updating the Spaces that sank out of "
            + "the last_modified window (#1326)");
        complete.Select(n => n.Path).Distinct().Count().Should().Be(TotalRows,
            "the complete set must be the union of both partitions, not one partition twice");

        // 3. A stated POSITIVE limit is still honoured — Complete() must not have turned the
        //    limit into a no-op for everyone else.
        var ten = await cross
            .QueryAcrossSchemasAsync(parsed with { Limit = 10 }, _options, Schemas, ct: ct)
            .Collect(ct).Should().Within(60.Seconds()).Emit();
        ten.Count.Should().Be(10, "an explicit positive limit still clips");
    }

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
