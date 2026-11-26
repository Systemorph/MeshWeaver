using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>The import-level wiring of the prune bound (policy declared-not-derived).</summary>
public class AMassPruneIsRefusedByTheImportTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A service identity, the shape of the portal's own runtime writers.</summary>
    private static readonly AccessContext RuntimeWriter = new()
    {
        ObjectId = "pr-babysitter",
        Name = "PR babysitter",
    };

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>A source that ships exactly the nodes it is given — the repo tree, in memory.</summary>
    private sealed class RepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; init; } = [];
        public MeshNode? Root { get; init; }

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
        public IReadOnlyList<StaticContentSync> EnumerateInlineContentSyncs() => [];
    }

    private static MeshNode Space(string partition) =>
        new(partition) { Name = partition, NodeType = "Space", State = MeshNodeState.Active };

    private static MeshNode Page(string partition, string id) =>
        new(id, partition)
        {
            Name = id,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# {id}" },
        };

    /// <summary>
    /// 🚨 The WIRING of the prune bound, through the real import: pass 2 ships 1 of 14 previously
    /// owned nodes, so the prune would take 13 of 15 — far over the bound — and is refused whole.
    /// Nothing is pruned and every node is still in the mesh (the pearl Provider deletion shape).
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnOversizedPrune_IsRefusedWhole_AndEveryNodeSurvives()
    {
        var partition = "Mp" + Guid.NewGuid().ToString("N")[..8];
        var ids = Enumerable.Range(0, 14).Select(i => $"N{i}").ToArray();

        var first = await StaticRepoImporter
            .ImportSource(Mesh, new RepoSource(partition)
            {
                Root = Space(partition),
                Nodes = ids.Select(id => Page(partition, id)).ToList(),
            })
            .FirstAsync().Timeout(240.Seconds()).Await(TestContext.Current.CancellationToken);
        first.Outcome.Should().Be("Imported");

        var second = await StaticRepoImporter
            .ImportSource(Mesh, new RepoSource(partition)
            {
                Root = Space(partition),
                Nodes = [Page(partition, "N0")],
            })
            .FirstAsync().Timeout(240.Seconds()).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"pass 2 = {second.Outcome}, pruned [{string.Join(", ", second.PrunedPaths)}]");

        second.PrunedPaths.Should().BeEmpty(
            "13 of 15 nodes is over the prune bound, so the whole prune is refused");

        var children = await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{partition} scope:children"))
                .Take(1))
            .Select(c => c.Items.Select(n => n.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray())
            .Where(paths => paths.Length >= ids.Length)
            .FirstAsync()
            .Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);
        children.Should().HaveCount(ids.Length, "nothing was deleted");
    }
}
