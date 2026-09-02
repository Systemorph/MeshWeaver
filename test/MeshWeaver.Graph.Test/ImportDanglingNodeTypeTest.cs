using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
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
/// The two import-side halves of issue #2993, and — more importantly — the two COUNTERPARTIES that
/// make each of them a decision rather than a patch.
///
/// <list type="number">
///   <item><b>Hole A's counterparty.</b> The update path now refuses a <c>NodeType</c> that
///     resolves to nothing. <c>StaticRepoImporter</c> deliberately relied on it refusing nothing —
///     that is the ordering escape hatch for the cases <c>ImportWriteOrder</c> cannot sequence (a
///     cycle, a type carried by no source). So the import must STILL land such a write, through the
///     named bypass, and must SAY it did.</item>
///   <item><b>Hole B's counterparty.</b> Pruning a retired NodeType is intended, shipped behaviour
///     (<c>WhatsNew/2026-08-28-retired-node-prune</c>). So the prune must STILL prune — and now
///     also name the instances it stranded.</item>
/// </list>
///
/// <para>Without the counterparty halves this file would trade a known hole for an unknown
/// regression: a refusal on the update path freezes a repo's git baseline (#2556's non-convergent
/// loop), and a refusal on the prune path leaves the DEFINITION standing with no source to compile
/// against (<c>Doc/Architecture/RetiringANodeType</c>).</para>
/// </summary>
public class ImportDanglingNodeTypeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // ——— pure: what the probe selects, and what it says ———————————————————————————————

    [Fact(Timeout = 60000)]
    public void TheProbe_SelectsOnlyNodeTypeDefinitions()
    {
        var selected = NodeTypeInstanceProbe.NodeTypePathsAmong(
        [
            TypeNode("Pkg", "Widget"),
            Page("Pkg", "Doc"),
            Instance("Pkg", "Inst", "Pkg/Widget"),
        ]);

        // Only a NodeType DEFINITION can strand instances; probing every pruned node would cost one
        // mesh-wide query per deleted markdown page.
        selected.Should().BeEquivalentTo(new[] { "Pkg/Widget" }, JsonSerializerOptions.Default);
    }

    [Fact(Timeout = 60000)]
    public void TheReport_NamesTheTypeAndThePaths()
    {
        var text = NodeTypeInstanceProbe.Describe(
        [
            new NodeTypeInstanceProbe.StrandedInstances(
                "Pkg/Widget", ["TestData/a", "TestData/b"], 2, Truncated: false),
        ]);

        text.Should().NotBeNull();
        text.Should().Contain("Pkg/Widget").And.Contain("TestData/a").And.Contain("TestData/b");
        NodeTypeInstanceProbe.Describe([]).Should().BeNull(
            "a healthy prune must add no noise at all — a report that always fires is ignored");
    }

    // ——— hole A's counterparty: the ordering escape hatch still lands, and says so ————

    /// <summary>
    /// 🚨 A node that ALREADY EXISTS, retyped by the source to a NodeType this pass cannot put in
    /// place first (carried by no source, absent from the mesh). Before #2993 this landed because
    /// the update path checked nothing. It must still land — refusing it counts as a per-file
    /// FAILURE, and <c>Failed &gt; 0</c> holds the caller's git baseline, so one such node would
    /// freeze every later commit of the same repo.
    ///
    /// <para>And it must not be silent: the import activity carries a ⚠ line naming the path and
    /// the type, on every pass until the type lands.</para>
    /// </summary>
    [Fact(Timeout = 300000)]
    public async Task Reimport_RetypingAnExistingNodeToATypeNoSourceCarries_StillLands_AndSaysSo()
    {
        var partition = "Eh" + Guid.NewGuid().ToString("N")[..8];
        var foreignType = "Some/Other/Partition/Widget";

        var first = await StaticRepoImporter
            .ImportSource(Mesh, new FakeRepoSource(partition)
            {
                Root = Space(partition),
                Nodes = [Instance(partition, "Thing", "Markdown")],
            })
            .FirstAsync().Timeout(180.Seconds()).Await();
        first.Failed.Should().Be(0, "the fixture import must land cleanly");
        first.Outcome.Should().Be("Imported");

        var attemptsBefore = await AttemptPaths(partition);

        var second = await StaticRepoImporter
            .ImportSource(Mesh, new FakeRepoSource(partition)
            {
                Root = Space(partition),
                Nodes = [Instance(partition, "Thing", foreignType)],
            })
            .FirstAsync().Timeout(180.Seconds()).Await();

        Output.WriteLine(
            $"outcome={second.Outcome} failed={second.Failed} written=[{string.Join(", ", second.WrittenPaths)}]");

        second.Failed.Should().Be(0,
            "the import ordering escape hatch must survive #2993: a refusal here is a per-file "
            + "failure, and Failed>0 holds the git baseline — one cyclic or foreign-typed node "
            + "would freeze every LATER commit of the repo (#2556's non-convergent loop)");
        second.Outcome.Should().Be("Imported");
        second.WrittenPaths.Should().Contain($"{partition}/Thing",
            "the retype must actually be WRITTEN — an escape hatch that silently no-ops is the same "
            + "regression as a refusal, only harder to see");

        var report = await TerminalSummary(partition, attemptsBefore);
        report.Should().Contain($"{partition}/Thing",
            "the escape hatch is never silent — the activity must NAME the node it stranded");
        report.Should().Contain(foreignType,
            "and the type that is missing, or an operator cannot act on it");
    }

    // ——— hole B: the prune still prunes, and now names what it stranded ———————————————

    /// <summary>
    /// 🚨 THE ONE THAT MATTERS. A source drops a NodeType while the mesh still holds instances of
    /// it — in ANOTHER partition, which is the realistic shape (a package's type, a user's data).
    ///
    /// <para>Both halves are asserted together on purpose: the prune must still DELETE the
    /// definition (refusing would contradict the shipped retired-node prune and would strand the
    /// definition instead), and it must now REPORT the instances it stranded (they have no
    /// per-node hub — they read as Unavailable and render empty, with nothing naming why).</para>
    /// </summary>
    [Fact(Timeout = 300000)]
    public async Task Prune_OfANodeTypeWithLiveInstances_StillPrunes_AndNamesTheStranded()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var partition = "Pr" + Guid.NewGuid().ToString("N")[..8];
        var typePath = $"{partition}/Widget";
        var instanceId = "inst" + Guid.NewGuid().ToString("N")[..8];
        var instancePath = $"{TestPartition}/{instanceId}";

        // 1. The source ships the type and a page.
        var first = await StaticRepoImporter
            .ImportSource(Mesh, new FakeRepoSource(partition)
            {
                Root = Space(partition),
                Nodes = [TypeNode(partition, "Widget"), Page(partition, "Doc")],
            })
            .FirstAsync().Timeout(180.Seconds()).Await();
        first.Failed.Should().Be(0);

        // 2. An instance of it lands in a DIFFERENT partition — a user's data, which is exactly
        //    what a package retirement cannot see and must not silently break.
        await meshService.CreateNode(Instance(TestPartition, instanceId, typePath))
            .Take(1).Should().Within(60.Seconds()).Emit("the instance must exist before the prune");

        // The probe reads the eventually-consistent index, so wait for the index to have seen the
        // instance before pruning — otherwise the test measures index lag, not the report.
        await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{typePath}").AsSystem())
                .Take(1))
            .Where(c => c.Items.Any(n => string.Equals(n.Path, instancePath, StringComparison.OrdinalIgnoreCase)))
            .FirstAsync().Timeout(120.Seconds()).Await();

        var attemptsBefore = await AttemptPaths(partition);

        // 3. The source retires the type. FullReplace (the default) prunes it.
        var second = await StaticRepoImporter
            .ImportSource(Mesh, new FakeRepoSource(partition)
            {
                Root = Space(partition),
                Nodes = [Page(partition, "Doc")],
            })
            .FirstAsync().Timeout(180.Seconds()).Await();

        Output.WriteLine(
            $"outcome={second.Outcome} pruned=[{string.Join(", ", second.PrunedPaths)}] "
            + $"stranded=[{string.Join(", ", second.StrandedNodeTypePaths)}]");

        // 🚨 COUNTERPARTY: a retired NodeType must STILL be pruned. Refusing would contradict the
        // shipped behaviour and leave a definition with no source to compile against — a type
        // parked at compilationStatus Error that no re-import can clear.
        second.PrunedPaths.Should().Contain(typePath,
            "pruning a retired NodeType is intended, shipped behaviour — the report must not have "
            + "turned into a veto");

        // 🚨 THE REPORT: loud, specific, and naming what to act on.
        second.StrandedNodeTypePaths.Should().Contain(typePath,
            "the deletion took the renderer away from live instances — that must reach the caller, "
            + "not just a debug line");

        var report = await TerminalSummary(partition, attemptsBefore);
        report.Should().Contain(typePath);
        report.Should().Contain(instancePath,
            "naming the TYPE alone leaves an operator with the same manual `search nodeType:{Type}` "
            + "they had before — the instances are the actionable half");
    }

    // ——— helpers ————————————————————————————————————————————————————————————————————

    /// <summary>The terminal summary line of the newest import attempt for the partition.</summary>
    private async Task<string> TerminalSummary(string partition, IReadOnlyCollection<string> before)
    {
        var attemptPath = Assert.Single(
            (await AttemptPaths(partition)).Except(before, StringComparer.Ordinal).ToArray());
        var attempt = await Mesh.GetWorkspace().GetMeshNodeStream(attemptPath)
            .Where(n => n.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions)
                is { Status: not ActivityStatus.Running })
            .FirstAsync().Timeout(60.Seconds()).Await();
        var log = attempt.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions)!;
        var text = string.Join("\n", log.Messages.Select(m => m.Message));
        Output.WriteLine($"--- activity {attemptPath} (status {log.Status}) ---\n{text}");
        return text;
    }

    private async Task<IReadOnlyList<string>> AttemptPaths(string partition)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var change = await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{partition}/_Activity scope:children nodeType:{ActivityNodeType.NodeType}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync().Timeout(60.Seconds()).Await();
        return change.Items
            .Where(n => n.Name?.Contains("attempt", StringComparison.Ordinal) == true)
            .Select(n => n.Path)
            .ToArray();
    }

    private static MeshNode Space(string partition) => new(partition)
    {
        Name = "Dangling fixture", NodeType = "Space", State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "# Dangling fixture\n\nfixture." },
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
