using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="StaticRepoImporter.RetireSource"/> — what a deleted source folder takes with it, and
/// what it must leave (<c>Doc/Architecture/SourceRetirement</c>). The two NodeType rules are the
/// load-bearing ones: a type with an instance that SURVIVES is held, together with every folder node
/// above it (a delete is recursive); a type whose only instances are the package's own goes with
/// them.
/// </summary>
public class RetireSourceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // ——— pure: the plan ————————————————————————————————————————————————————————————————

    [Fact]
    public void AnAncestorOfAHeldType_IsKept_SoTheRecursiveDeleteCannotTakeTheType()
    {
        var candidates = new[] { Page("Pkg", "Area"), TypeNode("Pkg/Area", "Gadget"), Page("Pkg", "Doc") };
        var plan = StaticRepoImporter.PlanRetirement(candidates,
            [new NodeTypeInstanceProbe.StrandedInstances("Pkg/Area/Gadget", ["User/g1"], 1, false)]);

        plan.Held.Select(h => h.NodeTypePath).Should().Equal("Pkg/Area/Gadget");
        plan.Delete.Select(n => n.Path).Should().Equal(new[] { "Pkg/Doc" },
            "Pkg/Area is a folder node ABOVE the held type: deleting it would delete the type too");
    }

    [Fact]
    public void ATypeWhoseOnlyInstancesAreRetiredWithIt_IsNotHeld()
    {
        var candidates = new[] { TypeNode("Pkg", "Widget"), Instance("Pkg", "MyWidget", "Pkg/Widget") };
        var plan = StaticRepoImporter.PlanRetirement(candidates,
            [new NodeTypeInstanceProbe.StrandedInstances("Pkg/Widget", ["Pkg/MyWidget"], 1, false)]);

        plan.Held.Should().BeEmpty("its one instance is the package's own and goes in the same pass");
        plan.Delete.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal)
            .Should().Equal("Pkg/MyWidget", "Pkg/Widget");
    }

    [Fact]
    public void AnUnnamedOrTruncatedInstanceCount_Holds()
    {
        var candidates = new[] { TypeNode("Pkg", "Widget"), Instance("Pkg", "MyWidget", "Pkg/Widget") };

        StaticRepoImporter.PlanRetirement(candidates,
                [new NodeTypeInstanceProbe.StrandedInstances("Pkg/Widget", ["Pkg/MyWidget"], 11, false)])
            .Held.Should().HaveCount(1, "an instance the probe counted but did not name may survive");
        StaticRepoImporter.PlanRetirement(candidates,
                [new NodeTypeInstanceProbe.StrandedInstances("Pkg/Widget", ["Pkg/MyWidget"], 1, true)])
            .Held.Should().HaveCount(1, "a truncated probe reached no verdict, and unknown holds");
    }

    [Fact]
    public void AnInstanceKeptUnderAHeldType_HoldsItsOwnTypeToo()
    {
        // Pkg/Area/Gadget is held by a user's instance; Pkg/Area/Gadget/Sample is an instance of
        // Pkg/Widget that the hold KEEPS — so Pkg/Widget must be held as well (the fixed point).
        var candidates = new[]
        {
            TypeNode("Pkg/Area", "Gadget"),
            Instance("Pkg/Area/Gadget", "Sample", "Pkg/Widget"),
            TypeNode("Pkg", "Widget"),
        };
        var plan = StaticRepoImporter.PlanRetirement(candidates,
        [
            new NodeTypeInstanceProbe.StrandedInstances("Pkg/Area/Gadget", ["User/g1"], 1, false),
            new NodeTypeInstanceProbe.StrandedInstances("Pkg/Widget", ["Pkg/Area/Gadget/Sample"], 1, false),
        ]);

        plan.Held.Select(h => h.NodeTypePath).OrderBy(p => p, StringComparer.Ordinal)
            .Should().Equal("Pkg/Area/Gadget", "Pkg/Widget");
        plan.Delete.Should().BeEmpty();
    }

    // ——— against a mesh ————————————————————————————————————————————————————————————————

    [Fact(Timeout = 300000)]
    public async Task ARetiredSource_TakesItsOwnTypesAndInstances_AndHoldsATypeAUserStillUses()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var partition = "Rt" + Guid.NewGuid().ToString("N")[..8];
        var gadget = $"{partition}/Area/Gadget";
        var userInstance = $"{TestPartition}/g{Guid.NewGuid():N}"[..(TestPartition.Length + 9)];

        var imported = await StaticRepoImporter
            .ImportSource(Mesh, new FakeRepoSource(partition)
            {
                Root = Space(partition),
                Nodes =
                [
                    TypeNode(partition, "Widget"),
                    Instance(partition, "MyWidget", $"{partition}/Widget"),
                    Page(partition, "Doc"),
                    Page(partition, "Area"),
                    TypeNode($"{partition}/Area", "Gadget"),
                ],
            })
            .FirstAsync().Timeout(180.Seconds()).Await(TestContext.Current.CancellationToken);
        imported.Failed.Should().Be(0, "the fixture import must land cleanly");

        await meshService.CreateNode(Instance(TestPartition, userInstance.Split('/')[^1], gadget))
            .Take(1).Should().Within(60.Seconds()).Emit("a user's instance of the package's type",
                cancellationToken: TestContext.Current.CancellationToken);
        await WaitForInstanceListing(gadget, userInstance);

        var retired = await StaticRepoImporter.RetireSource(Mesh, partition, $"{partition} test retirement")
            .FirstAsync().Timeout(180.Seconds()).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"outcome={retired.Outcome} pruned=[{string.Join(", ", retired.PrunedPaths)}] "
                         + $"held=[{string.Join(", ", retired.HeldNodeTypePaths)}]");

        retired.Outcome.Should().Be(StaticRepoImporter.RetiredOutcome);
        string.Join(",", retired.PrunedPaths.OrderBy(p => p, StringComparer.Ordinal)).Should().Be(
            $"{partition}/Doc,{partition}/MyWidget,{partition}/Widget",
            "Widget's only instance was the package's own, so both go — and the page with them");
        retired.HeldNodeTypePaths.Should().Equal(gadget);
        retired.Preserved.Should().Be(1, "a held type leaves the retirement unconverged, so the next sync asks again");

        (await Exists(gadget)).Should().BeTrue("a user still has an instance of it");
        (await Exists($"{partition}/Area")).Should().BeTrue(
            "the folder node above a held type stays — deleting it would delete the type recursively");
        (await Exists(userInstance)).Should().BeTrue("the user's data is never the source's to delete");
        (await Exists(partition)).Should().BeTrue("the partition root stays");
    }

    // ——— helpers ————————————————————————————————————————————————————————————————————

    private async Task WaitForInstanceListing(string typePath, string instancePath)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(typePath)).AsSystem())
                .Take(1))
            .Where(c => c.Items.Any(n => string.Equals(n.Path, instancePath, StringComparison.OrdinalIgnoreCase)))
            .FirstAsync().Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);
    }

    private async Task<bool> Exists(string path)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return await meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}").AsSystem())
            .Take(1)
            .Select(c => c.Items.Any(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)))
            .Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);
    }

    private static MeshNode Space(string partition) => new(partition)
    {
        Name = "Retirement fixture", NodeType = "Space", State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "# Retirement fixture" },
    };

    private static MeshNode Page(string ns, string id) => new(id, ns)
    {
        NodeType = "Markdown", Name = id, State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}" },
    };

    private static MeshNode Instance(string ns, string id, string typePath) => new(id, ns)
    {
        NodeType = typePath, Name = id, State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}" },
    };

    private static MeshNode TypeNode(string ns, string id) => new(id, ns)
    {
        NodeType = MeshNode.NodeTypePath, Name = id, State = MeshNodeState.Active,
        Content = new NodeTypeDefinition { Configuration = "config => config" },
    };

    private sealed class FakeRepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; set; } = [];
        public MeshNode? Root { get; set; }
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
    }
}
