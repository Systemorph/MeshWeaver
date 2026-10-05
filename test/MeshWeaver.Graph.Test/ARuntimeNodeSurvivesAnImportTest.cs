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

/// <summary>
/// 🚨 <b>A source import prunes only what the source PUT there</b> — policy
/// <c>prune-requires-provenance</c> (<c>Doc/Architecture/SourcesSyncOnPush</c>).
///
/// <para><b>Measured on the control instance, 2026-10-05.</b> A manual import of <c>Hosting</c>
/// (MeshWeaver.Plugins main) pruned 16 nodes as "absent from the repo", among them
/// <c>Hosting/Babysitter</c> — the PR babysitter's live state node, written by the running portal —
/// and every <c>Hosting/Queues/*</c> entry. The partition ran the default <c>FullReplace</c>, which
/// pruned EVERY extra; a repository cannot carry state the portal creates at runtime, so its absence
/// there said nothing.</para>
///
/// <para><b>Shape.</b> Pass 1 imports two source nodes. A runtime writer (not a person — the
/// two-way "human edit" protection is deliberately NOT what saves it here; this import runs with no
/// conflict policy at all) creates a node of its own. Pass 2 is the same repository with one source
/// node DELETED. The deleted source node is pruned — that is the positive control, without which
/// "the runtime node survived" could mean the prune never ran — and the runtime node stays.</para>
/// </summary>
public class ARuntimeNodeSurvivesAnImportTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
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
    /// The runtime node survives a FullReplace import that prunes the source node the repository
    /// deleted.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARuntimeNodeAbsentFromTheRepository_SurvivesTheImport_WhileARetiredSourceNodeIsPruned()
    {
        var partition = "Rn" + Guid.NewGuid().ToString("N")[..8];
        var retired = $"{partition}/Retired";
        var runtime = $"{partition}/Babysitter";

        var first = await StaticRepoImporter
            .ImportSource(Mesh, new RepoSource(partition)
            {
                Root = Space(partition),
                Nodes = [Page(partition, "Kept"), Page(partition, "Retired")],
            })
            .FirstAsync().Timeout(240.Seconds()).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"pass 1 = {first.Outcome} ({first.Count} node(s))");
        first.Outcome.Should().Be("Imported");

        // The portal writes runtime state into the synced partition — no repository carries it.
        IObservable<MeshNode> write;
        using (Access.SwitchAccessContext(RuntimeWriter))
            write = MeshService.CreateNode(Page(partition, "Babysitter"));
        await write.FirstAsync().Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);

        // Pass 2: the repository deleted Retired. Default sync mode (FullReplace), no conflict policy.
        var second = await StaticRepoImporter
            .ImportSource(Mesh, new RepoSource(partition)
            {
                Root = Space(partition),
                Nodes = [Page(partition, "Kept")],
            })
            .FirstAsync().Timeout(240.Seconds()).Await(TestContext.Current.CancellationToken);
        Output.WriteLine(
            $"pass 2 = {second.Outcome}, pruned [{string.Join(", ", second.PrunedPaths)}]");

        second.PrunedPaths.Should().Contain(retired,
            "the POSITIVE control: the repository put this node there and then deleted it, so it is "
            + "the repository's to prune — without this the runtime assertion below could pass on an "
            + "import that never pruned anything");
        second.PrunedPaths.Should().NotContain(runtime,
            "no import manifest records this node — the running portal created it — so its absence "
            + "from the repository is not evidence of a deletion (policy prune-requires-provenance; "
            + "the Hosting/Babysitter prune on the control instance)");

        // And it is still in the mesh, read through the index once the prune of the retired node has
        // settled there (the read model trails the confirmed writes).
        var children = await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{partition} scope:children"))
                .Take(1))
            .Select(c => c.Items.Select(n => n.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray())
            .Where(paths => !paths.Contains(retired, StringComparer.OrdinalIgnoreCase))
            .FirstAsync()
            .Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);
        children.Should().Contain(runtime, "the runtime node survived the import");
    }
}
