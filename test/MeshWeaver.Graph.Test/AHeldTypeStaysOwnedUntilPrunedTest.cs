using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A path the source put in the partition stays the source's until it is actually removed</b>
/// (<c>Doc/Architecture/SourcesSyncOnPush</c> → "Ownership outlives a run that did not
/// prune"). Since policy <c>prune-requires-provenance</c> a node is pruned only when the PRIOR import
/// manifest lists it, so the manifest is the ONLY memory of what the repository owns. It used to be
/// rebuilt from the source nodes of the current run, with exactly one carry-over: a NodeType held by
/// THAT SAME run. Any run that did not re-hold the type — a truncated listing, an
/// <c>UpsertOnly</c> partition, a prune whose delete failed, a node kept as a server edit — dropped it
/// from the manifest, and from then on no import could ever prune it: the retirement leaked silently.
///
/// <para>Measured on memex.systemorph.com: <c>Crm/Client</c> was held on every Crm import from
/// 2026-09-25 to 2026-10-03, was not mentioned by the 2026-10-06 07:05Z import ("Imported 29, pruned
/// 0", no hold), is absent from <c>Crm/_Activity/import-manifest</c>, and <c>/health</c> still lists
/// it as retired by its repository.</para>
///
/// <para>The negative control is <see cref="ImportDanglingNodeTypeTest.Prune_OfANodeTypeWithLiveInstances_IsRefused_UntilTheInstancesAreGone"/>:
/// the same sequence WITHOUT the intermediate run, which completed the retirement before this fix
/// and still does. The intermediate run here is asserted to have held nothing, so the scenario
/// cannot pass by the type having been re-held.</para>
/// </summary>
public class AHeldTypeStaysOwnedUntilPrunedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 300000)]
    public async Task AHeldType_ARunThatDoesNotReHoldIt_StillPrunedOnceTheInstancesAreGone()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var partition = "Ho" + Guid.NewGuid().ToString("N")[..8];
        var typePath = $"{partition}/Widget";
        var instanceId = "inst" + Guid.NewGuid().ToString("N")[..8];
        var instancePath = $"{TestPartition}/{instanceId}";

        // 1. The source ships the type and a page.
        var first = await StaticRepoImporter
            .ImportSource(Mesh, new Source(partition) { Nodes = [TypeNode(partition, "Widget"), Page(partition, "Doc")] })
            .FirstAsync().Timeout(180.Seconds()).Await(ct);
        first.Failed.Should().Be(0);

        // 2. An instance exists in another partition; the source retires the type → HELD.
        await meshService.CreateNode(Instance(TestPartition, instanceId, typePath))
            .Take(1).Should().Within(60.Seconds()).Emit("the instance must exist before the prune",
                cancellationToken: ct);
        await WaitForInstanceListing(typePath, instancePath, present: true);

        var held = await StaticRepoImporter
            .ImportSource(Mesh, new Source(partition) { Nodes = [Page(partition, "Doc")] })
            .FirstAsync().Timeout(180.Seconds()).Await(ct);
        Output.WriteLine($"held run: held=[{string.Join(", ", held.HeldNodeTypePaths)}]");
        held.HeldNodeTypePaths.Should().Contain(typePath, "the fixture must reach the held state first");

        // 3. A later run that does NOT re-evaluate the type: its listing is incomplete (a truncated
        //    repository tree), so it prunes — and therefore probes and holds — nothing.
        var notReHeld = await StaticRepoImporter
            .ImportSource(Mesh, new Source(partition)
            {
                Nodes = [Page(partition, "Doc"), Page(partition, "Doc2")],
                Complete = false,
            })
            .FirstAsync().Timeout(180.Seconds()).Await(ct);
        Output.WriteLine(
            $"incomplete run: outcome={notReHeld.Outcome} held=[{string.Join(", ", notReHeld.HeldNodeTypePaths)}] "
            + $"pruned=[{string.Join(", ", notReHeld.PrunedPaths)}]");
        notReHeld.HeldNodeTypePaths.Should().BeEmpty(
            "the scenario needs a run that did NOT re-hold the type — otherwise it measures the old carry");
        notReHeld.PrunedPaths.Should().BeEmpty("an incomplete listing prunes nothing (#3589)");

        // 4. The instances go; the next commit's complete import must complete the retirement.
        await meshService.DeleteNode(instancePath)
            .Take(1).Should().Within(60.Seconds()).Emit("the instance must be gone before the re-run",
                cancellationToken: ct);
        await WaitForInstanceListing(typePath, instancePath, present: false);

        var last = await StaticRepoImporter
            .ImportSource(Mesh, new Source(partition)
            {
                Nodes = [Page(partition, "Doc"), Page(partition, "Doc2"), Page(partition, "Doc3")],
            })
            .FirstAsync().Timeout(180.Seconds()).Await(ct);
        Output.WriteLine(
            $"last run: outcome={last.Outcome} held=[{string.Join(", ", last.HeldNodeTypePaths)}] "
            + $"pruned=[{string.Join(", ", last.PrunedPaths)}]");

        last.PrunedPaths.Should().Contain(typePath,
            "the source put the type here and nothing has removed it since — a run that did not "
            + "re-hold it must not have forfeited the source's ownership of it");
    }

    private async Task WaitForInstanceListing(string typePath, string instancePath, bool present)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(typePath)).AsSystem())
                .Take(1))
            .Where(c => c.Items.Any(n =>
                string.Equals(n.Path, instancePath, StringComparison.OrdinalIgnoreCase)) == present)
            .FirstAsync().Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);
    }

    private static MeshNode Space(string partition) => new(partition)
    {
        Name = "Held fixture", NodeType = "Space", State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "# Held fixture\n\nfixture." },
    };

    private static MeshNode Page(string partition, string id) => new(id, partition)
    {
        NodeType = "Markdown", Name = id, State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}\n\npage" },
    };

    private static MeshNode Instance(string partition, string id, string typePath) => new(id, partition)
    {
        NodeType = typePath, Name = id, State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}\n\ninstance" },
    };

    private static MeshNode TypeNode(string partition, string id) => new(id, partition)
    {
        NodeType = MeshNode.NodeTypePath, Name = id, State = MeshNodeState.Active,
        Content = new NodeTypeDefinition { Configuration = "config => config" },
    };

    private sealed class Source(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; set; } = [];
        public bool Complete { get; set; } = true;
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Space(partition);
        public bool ListingIsComplete => Complete;
    }
}
