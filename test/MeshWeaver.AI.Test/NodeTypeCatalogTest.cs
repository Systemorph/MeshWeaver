using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Phase-1 contract for the unified <b>NodeType-catalog</b> pattern, on the <c>Harness</c> catalog
/// (see <c>Doc/Architecture/NodeTypeCatalogs.md</c>).
///
/// <para>A NodeType catalog ships INSTANCES of a NodeType (Agent / Harness / Skill / Model) and is
/// served from the database when its partition is DB-synced. The bug this pins fixed: the in-memory
/// <c>Harness</c> type-definition (<c>AddMeshNodes</c>, path = the discriminator "Harness") and the
/// DB-imported partition root (<c>IStaticRepoSource.PartitionRoot</c>, formerly <c>nodeType:Space</c>,
/// path = RootNamespace "Harness") BOTH claimed the bare path <c>@Harness</c>. With two claimants a
/// <c>GetDataRequest</c> for the bare partition bounced between hubs, re-entered the routing-loop
/// guard, and faulted <c>ds/Harness</c> — taking the harness picker down with it.</para>
///
/// <para>The fix: the partition root becomes a single <c>nodeType:NodeType</c> node that LINKS
/// (<see cref="NodeTypeDefinition.StaticTypeName"/>) to the registered static "Harness" type for its
/// HubConfiguration, and the in-memory type-def is registered DEFINITION-ONLY
/// (<see cref="MeshNode.IsDefinitionOnly"/>) — it still supplies the HubConfiguration delegate by
/// name + proves the type exists, but is never served / queried as the runtime node at <c>@Harness</c>.
/// Postgres is the sole runtime owner of the bare partition path.</para>
/// </summary>
public class NodeTypeCatalogTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // DB-synced Harness partition (as the distributed portal wires it): the in-memory static surfaces
    // are gated off, and the catalog is materialized into persistence by HarnessStaticRepoSource. The
    // other AI catalogs stay in-memory (not under test here).
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddAI(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { HarnessNodeType.RootNamespace })
            .ConfigureServices(s => s.AddSingleton<IStaticRepoSource>(sp =>
                new HarnessStaticRepoSource(sp.GetRequiredService<BuiltInHarnessProvider>())));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Materialize the Harness catalog into persistence (the DB-synced/import path).</summary>
    private async Task ImportAsync(CancellationToken ct)
    {
        var results = await StaticRepoImporter.ImportAll(Mesh).ToList().FirstAsync().ToTask(ct);
        foreach (var r in results)
            Output.WriteLine($"import: partition={r.Partition} outcome={r.Outcome} count={r.Count}");
    }

    /// <summary>Run a query, retrying (in-memory persistence is not lagged, but be robust to
    /// post-import propagation) until <paramref name="predicate"/> holds, then return the items.</summary>
    private Task<IReadOnlyList<MeshNode>> QueryUntil(
        string query, Func<IReadOnlyList<MeshNode>, bool> predicate, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                .Take(1)
                .Select(c => (IReadOnlyList<MeshNode>)c.Items))
            .Where(predicate)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask(ct);
    }

    /// <summary>Authoritative single-node read under System (bypasses RLS) waiting for a predicate —
    /// the per-node-hub stream, not the lagged query. Returns null on timeout / not-found.</summary>
    private async Task<MeshNode?> ReadWhenSystem(string path, Func<MeshNode, bool> predicate, CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
            return await Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Where(n => n is not null && predicate(n))
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Catch((Exception _) => Observable.Return<MeshNode?>(null))
                .FirstAsync()
                .ToTask(ct);
    }

    /// <summary>
    /// (1) The catalog partition root is a SINGLE <c>nodeType:NodeType</c> node — there is no second
    /// claimant at <c>@Harness</c>. The in-memory type-def still exists as a DEFINITION (it carries the
    /// HubConfiguration delegate + proves the type), but it is marked definition-only and is excluded
    /// from query results, so the bare partition path resolves to exactly the PG root.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task PartitionRoot_IsSingle_NodeTypeNode_WithNoCollidingClaimant()
    {
        var ct = Ct;
        await ImportAsync(ct);

        // Exactly one node at path "Harness", and it is the nodeType:NodeType partition root.
        var items = await QueryUntil("path:Harness",
            list => list.Any(n => string.Equals(n.Path, "Harness", StringComparison.OrdinalIgnoreCase)),
            ct);
        var atPath = items
            .Where(n => string.Equals(n.Path, "Harness", StringComparison.OrdinalIgnoreCase))
            .ToList();
        atPath.Should().HaveCount(1,
            "the catalog partition path must have exactly ONE claimant — the PG nodeType:NodeType root; "
            + "the in-memory type-def is dissociated (definition-only, excluded from queries)");
        atPath[0].NodeType.Should().Be(MeshNode.NodeTypePath,
            "the partition root is the catalog's NodeType definition, not a nodeType:Space root");

        // Authoritatively confirm the root's content links to the static "Harness" type.
        var root = await ReadWhenSystem("Harness", n => n.NodeType == MeshNode.NodeTypePath, ct);
        root.Should().NotBeNull("the @Harness partition root must be served from the database");
        (root!.Content as NodeTypeDefinition)?.StaticTypeName.Should().Be(HarnessNodeType.NodeType,
            "the NodeType root links to the registered static C# 'Harness' type for its HubConfiguration");

        // The in-memory definition STILL exists for HubConfiguration-by-name (role B) + type existence,
        // but is marked definition-only so it is never served / queried as the @Harness node.
        var def = Mesh.ServiceProvider.FindStaticNode("Harness");
        def.Should().NotBeNull("the static 'Harness' definition must remain for HubConfiguration resolution");
        def!.IsDefinitionOnly.Should().BeTrue(
            "a DB-synced catalog's in-memory type-def is dissociated from runtime node-serving");
        def.HubConfiguration.Should().NotBeNull(
            "the definition-only node still supplies the (non-serialisable) HubConfiguration delegate by name");
    }

    /// <summary>
    /// (2) The bare partition address resolves and serves WITHOUT a routing loop, and the catalog's
    /// instances list via the picker query. This is the harness-picker path that vanished when
    /// <c>ds/Harness</c> faulted.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task BarePartitionAddress_Resolves_AndPickerQueryLists_Instances()
    {
        var ct = Ct;
        await ImportAsync(ct);

        // The bare partition address is readable (authoritative owner round-trip activates its hub) —
        // a routing loop / faulted ds/Harness would time out to null here.
        var root = await ReadNode("Harness").FirstAsync().ToTask(ct);
        root.Should().NotBeNull("@Harness must resolve to its single PG NodeType root without a routing loop");
        root!.NodeType.Should().Be(MeshNode.NodeTypePath);

        // The harness picker query returns the catalog instances (served from the DB partition).
        var instances = await QueryUntil($"namespace:{HarnessNodeType.RootNamespace} nodeType:{HarnessNodeType.NodeType}",
            list => list.Any(n => n.Id == Harnesses.MeshWeaver), ct);
        instances.Should().Contain(n => n.Id == Harnesses.MeshWeaver,
            "namespace:Harness nodeType:Harness must return the materialized harness instances");
        instances.Should().OnlyContain(n => n.NodeType == HarnessNodeType.NodeType);
    }

    /// <summary>
    /// (3) A <c>nodeType:Harness</c> child activates and enriches with the Harness HubConfiguration —
    /// its per-node hub serves the harness node (the picker binding's read). Enrichment resolves the
    /// delegate via the definition-only static node BY NAME (role B), proving the dissociation kept the
    /// definition available even though it is no longer served at the bare path.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task HarnessChild_Activates_AndEnrichesWith_HubConfiguration()
    {
        var ct = Ct;
        await ImportAsync(ct);

        var path = $"{HarnessNodeType.RootNamespace}/{Harnesses.MeshWeaver}";
        var node = await ReadNode(path).FirstAsync().ToTask(ct);

        node.Should().NotBeNull(
            $"the per-node hub for '{path}' must activate and serve its MeshNode — a null read means "
            + "enrichment produced no usable HubConfiguration (the catalog's instances enrich via the "
            + "definition-only 'Harness' type-def by name)");
        node!.NodeType.Should().Be(HarnessNodeType.NodeType);
        node.Content.Should().NotBeNull("the harness data source serves the harness content");
    }

    /// <summary>
    /// (4) Regression: reading the catalog root + an instance REPEATEDLY does not fault the data source
    /// (the original symptom was a one-shot fault that then stranded every subscriber). Each read is an
    /// authoritative owner round-trip; all must succeed.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task RepeatedReads_OfRootAndInstance_DoNotFault_DataSource()
    {
        var ct = Ct;
        await ImportAsync(ct);

        var instancePath = $"{HarnessNodeType.RootNamespace}/{Harnesses.MeshWeaver}";
        for (var i = 0; i < 3; i++)
        {
            var root = await ReadNode("Harness").FirstAsync().ToTask(ct);
            root.Should().NotBeNull($"@Harness read #{i + 1} must not fault the data source");

            var instance = await ReadNode(instancePath).FirstAsync().ToTask(ct);
            instance.Should().NotBeNull($"{instancePath} read #{i + 1} must not fault the data source");
        }
    }
}
