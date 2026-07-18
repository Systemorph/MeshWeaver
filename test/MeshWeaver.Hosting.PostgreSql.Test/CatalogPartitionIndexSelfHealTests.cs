using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Boot-time self-heal/assert that a SERVED AI-catalog partition (Provider / Agent / Skill / Harness)
/// is provisioned into a PG schema AND registered in the cross-schema search index
/// (<c>public.searchable_schemas</c>) after the static-repo import — the residual of #354.
///
/// <para>The primary fix (#407) removed the config-alias/allow-list drift that stopped the catalogs
/// being imported at all. The residual: nothing at boot ASSERTED that a served catalog actually landed
/// in the search index. Because the import's own queries are all partition-PINNED, they never trigger a
/// <c>SyncSearchableSchemasAsync</c>, so a freshly-provisioned catalog schema stays absent from
/// <c>searchable_schemas</c> until the first unpinned fan-out query happens to run one — meanwhile every
/// index query (<c>search nodeType:ModelProvider</c>, children listing, the catalog areas) returns
/// EMPTY although exact-path <c>get</c> works ("materialized but unindexed").</para>
///
/// <para><see cref="StaticRepoImporter.ImportAll"/> now takes the served catalog set and, after the
/// import, FORCES one searchable-schemas rebuild for it (bypassing the query-hot-path throttle — a
/// one-time boot action) and surfaces a startup-failure notification for any served catalog still
/// unindexed. These tests pin: (1) the served catalog is registered + a cross-schema search finds its
/// seeded nodes; (2) a searchable-schemas row that later goes MISSING (the "materialized but unindexed"
/// drift) is re-registered by the next boot's self-heal, EVEN INSIDE the 30 s sync throttle window — the
/// force=true bypass is what makes that deterministic (a non-forced sync would be throttled and leave it
/// gone).</para>
/// </summary>
[Collection("PostgreSql")]
public class CatalogPartitionIndexSelfHealTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    // Capitalized partition so the lowercase-schema invariant is observable; not in the cross-schema
    // ExcludedSchemas set. A property (reads _source) avoids the field-order trap.
    private readonly FakeCatalogSource _source = NewSource("Cat" + Guid.NewGuid().ToString("N")[..10]);
    private string _partition => _source.Partition;

    // The #354 served-catalog assertion set the portal boot passes (here: just this fake catalog).
    private IReadOnlySet<string> Assertions =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _partition };

    private static FakeCatalogSource NewSource(string partition) => new(partition)
    {
        Root = new MeshNode(partition)
        {
            Name = "Catalog", NodeType = "Space", State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# {partition}\n\nCatalog root." }
        },
        Nodes =
        [
            new MeshNode("Anthropic", partition)
            {
                NodeType = "ModelProvider", Name = "Anthropic", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "Anthropic provider." }
            },
            new MeshNode("code", partition)
            {
                NodeType = "Skill", Name = "code", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "The code skill." }
            }
        ]
    };

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
            {
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString);
                services.AddSingleton<IStaticRepoSource>(_source);
                return services;
            })
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            // The catalog's instance node types (mirrors the real Provider/Skill catalogs) so the import's
            // canonical CreateOrUpdate accepts them and the cross-schema search can filter by node_type.
            .AddMeshNodes(
                new MeshNode("ModelProvider") { Name = "Model Provider" },
                new MeshNode("Skill") { Name = "Skill" });
    }

    private Task<IList<StaticRepoImportResult>> Import() =>
        StaticRepoImporter.ImportAll(Mesh, null, null, Assertions)
            .ToList().Should().Within(120.Seconds()).Emit();

    /// <summary>COUNT of <paramref name="partition"/>'s (lowercased) schema in public.searchable_schemas.</summary>
    private Task<long> SearchableSchemaCount(string partition, CancellationToken ct) =>
        _fixture.DataSource.ScalarLong(
            "SELECT COUNT(*) FROM public.searchable_schemas WHERE schema_name = @s",
            new[] { ("s", (object)partition.ToLowerInvariant()) }, ct)
            .Should().Within(30.Seconds()).Emit();

    /// <summary>
    /// Cross-schema search for <paramref name="nodeType"/> as System (userId=null → no access filter),
    /// over the CURRENT searchable-schemas registry — the same fan-out the AI catalog areas use.
    /// </summary>
    private Task<List<MeshNode>> SearchByNodeType(string nodeType, CancellationToken ct)
    {
        var crossSchema = Mesh.ServiceProvider.GetRequiredService<ICrossSchemaQueryProvider>();
        return crossSchema.GetSearchableSchemasAsync(ct).Run()
            .SelectMany(schemas => crossSchema
                .QueryAcrossSchemasAsync(
                    new QueryParser().Parse($"nodeType:{nodeType}"),
                    Mesh.JsonSerializerOptions, schemas, userId: null, ct)
                .Collect(ct))
            .Should().Within(30.Seconds()).Emit();
    }

    /// <summary>
    /// After the boot import, the served catalog partition is registered in searchable_schemas and a
    /// cross-schema search finds its seeded catalog nodes — the exact index-based access #354 reported
    /// as returning empty on affected instances.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task Import_RegistersServedCatalog_AndCrossSchemaSearchFindsNodes()
    {
        var ct = TestContext.Current.CancellationToken;

        var results = await Import();
        results.Should().Contain(r => r.Partition == _partition && r.Outcome == "Imported");
        // The self-heal step emits its own result — all served catalogs indexed (0 missing).
        results.Should().Contain(r => r.Partition == "_CatalogIndexAssert" && r.Outcome == "Indexed");

        // (a) The core #354 fix: the served catalog's schema is now in the cross-schema search index.
        (await SearchableSchemaCount(_partition, ct)).Should().Be(
            1L, "the boot self-heal must register the served catalog partition in searchable_schemas");

        // (b) End-to-end: the index-based access that returned empty on affected instances now finds
        // the seeded nodes (search nodeType:ModelProvider / nodeType:Skill).
        var providers = await SearchByNodeType("ModelProvider", ct);
        providers.Select(n => n.Id).Should().Contain("Anthropic",
            "search nodeType:ModelProvider must find the served catalog's provider node (#354)");

        var skills = await SearchByNodeType("Skill", ct);
        skills.Select(n => n.Id).Should().Contain("code",
            "search nodeType:Skill must find the served catalog's skill node (#354)");
    }

    /// <summary>
    /// Revert-proven self-heal: a served catalog whose searchable_schemas row later goes MISSING (the
    /// "materialized but unindexed" drift — the schema + its nodes are intact, only the index registration
    /// is gone) is re-registered by the next boot's self-heal. This works EVEN INSIDE the 30 s sync
    /// throttle window because the self-heal forces the rebuild (force=true) — a plain throttled sync
    /// would skip and leave the catalog unindexed. Remove the force bypass (or the self-heal) and this
    /// fails: the row stays gone.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task Reimport_ReRegistersDroppedSearchableSchema_BypassingThrottle()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = _partition.ToLowerInvariant();

        // First boot: import + self-heal → the catalog schema is registered (and the throttle's
        // last-sync timestamp is now set, so a NON-forced re-sync within 30 s would be a no-op).
        await Import();
        (await SearchableSchemaCount(_partition, ct)).Should().Be(1L);

        // Simulate the residual drift: the schema + its nodes stay (materialized), but the index
        // registration is dropped — index queries would now return empty for this catalog.
        await _fixture.DataSource.ExecuteNonQuery(
                $"DELETE FROM public.searchable_schemas WHERE schema_name = '{schema}'", ct)
            .Should().Within(30.Seconds()).Emit();
        (await SearchableSchemaCount(_partition, ct)).Should().Be(0L, "the row was just deleted");

        // Next boot: the content import short-circuits on the unchanged fingerprint, but the self-heal
        // runs unconditionally and FORCE-re-syncs — re-registering the catalog despite the throttle.
        await Import();
        (await SearchableSchemaCount(_partition, ct)).Should().Be(
            1L, "the boot self-heal force-re-syncs (bypassing the throttle) so a dropped searchable_schemas "
            + "row is re-registered — a non-forced sync would be throttled within 30 s and leave it gone");
    }

    /// <summary>In-memory fake catalog: instance-typed nodes + a Space root, like the real AI catalogs.</summary>
    private sealed class FakeCatalogSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; set; } = [];
        public MeshNode? Root { get; set; }
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
    }
}
