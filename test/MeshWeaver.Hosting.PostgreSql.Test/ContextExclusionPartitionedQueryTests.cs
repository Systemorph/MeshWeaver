using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Regression tests for #2419 — <c>is:content</c> (and every other <c>context:</c>) was a silent
/// NO-OP on a PARTITIONED Postgres portal, on BOTH of that provider's routes.
///
/// <para>The visible symptom was a public Store package cover whose "Contents" catalog listed
/// <c>Access Policy</c> — the partition's <c>_Policy</c> node — as its only entry. The catalog's
/// query is <c>namespace:{path} scope:subtree is:main is:content</c>
/// (<c>MeshNodeLayoutAreas.BuildCatalog</c>), and <c>PartitionAccessPolicy</c> has declared
/// <c>ExcludeFromContext = {search, create, content}</c> all along. The declaration was correct,
/// registered, and reached <c>MeshConfiguration.GetExcludedNodeTypes</c> — it simply never reached
/// a query:</para>
/// <list type="number">
///   <item><b>Scoped route</b> — <c>PostgreSqlPartitionedMeshQuery.GetDelegateForPath</c> built every
///     per-schema <c>PostgreSqlMeshQuery</c> with <c>meshConfiguration: null</c>, so the delegate that
///     serves every <c>namespace:X</c> browse had no exclusion set to push into SQL.</item>
///   <item><b>Fan-out route</b> — the cross-schema UNION derived no excluded types and
///     <c>GenerateCrossSchemaSelectQuery</c> called <c>GenerateWhereClause(query)</c> without them.</item>
/// </list>
///
/// <para>🚨 Both halves are asserted here on a NON-satellite type. That matters: <c>AccessAssignment</c>
/// is kept out of catalogs by a different mechanism (<c>SatelliteTableMapping</c> +
/// <c>IsSatelliteType</c>), so a test over a satellite type passes whether context exclusion works or
/// not — which is exactly how this survived. The type used below is an ordinary partition-resident
/// node type, so the ONLY thing that can filter it is the context exclusion under test.</para>
/// </summary>
[Collection("PostgreSql")]
public class ContextExclusionPartitionedQueryTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public ContextExclusionPartitionedQueryTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private const string Partition = "Acme";
    private const string Schema = "acme";

    /// <summary>The excluded type — a plain, non-satellite node type, see the class remarks.</summary>
    private const string GovernanceType = "InstallLedger";

    /// <summary>
    /// The registration nodes a portal's <see cref="MeshConfiguration"/> holds: one type that opts out
    /// of the <c>content</c> context and one that does not. Nothing else distinguishes them.
    /// </summary>
    private static MeshConfiguration BuildMeshConfiguration() => new(
    [
        new MeshNode(GovernanceType)
        {
            NodeType = "NodeType",
            Name = "Install Ledger",
            ExcludeFromContext = new HashSet<string> { "search", "create", "content" }
        },
        new MeshNode("Markdown") { NodeType = "NodeType", Name = "Markdown" }
    ]);

    private async Task<PostgreSqlStorageAdapter> SeedPartitionAsync(CancellationToken ct)
    {
        await _fixture.CleanDataAsync(ct);

        var partitionDef = new PartitionDefinition
        {
            Namespace = Partition,
            Schema = Schema,
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (_, adapter) = await _fixture.CreateSchemaAdapterAsync(Schema, partitionDef);

        await adapter.WriteAsync(new MeshNode(Partition)
        {
            Name = "Acme", NodeType = "Markdown", State = MeshNodeState.Active
        }, _options, ct);

        // The one node a visitor is meant to see under "Contents".
        await adapter.WriteAsync(new MeshNode("Report", Partition)
        {
            Name = "Quarterly Report", NodeType = "Markdown", State = MeshNodeState.Active
        }, _options, ct);

        // The governance node that must NOT be listed as page content.
        await adapter.WriteAsync(new MeshNode("Ledger", Partition)
        {
            Name = "Install Ledger", NodeType = GovernanceType, State = MeshNodeState.Active
        }, _options, ct);

        await PopulateSearchableSchemasAsync([Schema], ct);
        return adapter;
    }

    private PostgreSqlPartitionStorageProvider CreatePartitionProvider()
        => new(
            _fixture.DataSource,
            _fixture.ConnectionString,
            new PostgreSqlStorageOptions { ConnectionString = _fixture.ConnectionString },
            partitions: null);

    private async Task PopulateSearchableSchemasAsync(IEnumerable<string> schemas, CancellationToken ct)
    {
        await using (var cmd = _fixture.DataSource.CreateCommand("DELETE FROM public.searchable_schemas"))
            await cmd.ExecuteNonQueryAsync(ct);

        foreach (var schema in schemas)
        {
            await using var cmd = _fixture.DataSource.CreateCommand(
                "INSERT INTO public.searchable_schemas (schema_name) VALUES ($1) ON CONFLICT DO NOTHING");
            cmd.Parameters.AddWithValue(schema);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<IReadOnlyList<MeshNode>> QueryAsync(
        PostgreSqlPartitionedMeshQuery query, string queryString, CancellationToken ct)
    {
        var change = await query
            .Query<MeshNode>(MeshQueryRequest.FromQuery(queryString, WellKnownUsers.System), _options)
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(ct);
        return change.Items;
    }

    /// <summary>
    /// The Store-cover shape, verbatim: a SCOPED catalog query over one package's subtree. Before the
    /// fix the per-schema delegate carried no <see cref="MeshConfiguration"/>, so the governance node
    /// came back as page content.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ScopedCatalogQuery_IsContent_ExcludesTheDeclaredType()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartitionAsync(ct);

        using var provider = CreatePartitionProvider();
        var query = new PostgreSqlPartitionedMeshQuery(
            new PostgreSqlCrossSchemaQueryProvider(_fixture.DataSource),
            partitionProvider: provider,
            meshConfiguration: BuildMeshConfiguration());

        // Control: without the context the governance node IS in the partition, so a later absence
        // can only be the exclusion — never a seeding or routing accident.
        var unfiltered = await QueryAsync(query, $"namespace:{Partition} scope:subtree", ct);
        unfiltered.Select(n => n.Path).Should().Contain($"{Partition}/Ledger");

        var catalog = await QueryAsync(query, $"namespace:{Partition} scope:subtree is:main is:content", ct);

        catalog.Select(n => n.Path).Should().Contain($"{Partition}/Report",
            "ordinary content still belongs in the catalog");
        catalog.Select(n => n.NodeType).Should().NotContain(GovernanceType,
            "a type declaring ExcludeFromContext 'content' must never be listed as page content — " +
            "this is the public Store cover's 'Contents' section (#2419)");
    }

    /// <summary>
    /// The same declaration must hold on the OTHER route — the cross-schema UNION a path-less query
    /// takes. A type filtered on one route and listed on the other means the same query answers
    /// differently depending on whether its path happens to pin to a single partition.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task FanOutQuery_IsContent_ExcludesTheDeclaredType()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartitionAsync(ct);

        using var provider = CreatePartitionProvider();
        var query = new PostgreSqlPartitionedMeshQuery(
            new PostgreSqlCrossSchemaQueryProvider(_fixture.DataSource),
            partitionProvider: provider,
            meshConfiguration: BuildMeshConfiguration());

        // Path-less ⇒ no partition to pin to ⇒ the cross-schema fan-out serves it.
        var unfiltered = await QueryAsync(query, $"nodeType:{GovernanceType}", ct);
        unfiltered.Select(n => n.Path).Should().Contain($"{Partition}/Ledger",
            "the fan-out must find the seeded node when no context is asked for");

        var filtered = await QueryAsync(query, $"nodeType:{GovernanceType} is:content", ct);

        filtered.Select(n => n.NodeType).Should().NotContain(GovernanceType,
            "the cross-schema UNION must apply the same context exclusion the pinned route does (#2419)");
    }
}
