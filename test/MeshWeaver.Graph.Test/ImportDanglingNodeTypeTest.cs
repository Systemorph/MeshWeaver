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
/// The two import-side halves of issue #2993, and the COUNTERPARTIES that make each of them a
/// decision rather than a patch.
///
/// <list type="number">
///   <item><b>Hole A's counterparty.</b> The update path now refuses a <c>NodeType</c> that
///     resolves to nothing. <c>StaticRepoImporter</c> deliberately relied on it refusing nothing —
///     that is the ordering escape hatch for the cases <c>ImportWriteOrder</c> cannot sequence (a
///     cycle, a type carried by no source). So the import must STILL land such a write, through the
///     named bypass, and must SAY it did.</item>
///   <item><b>Hole B — the prune of a NodeType that still has instances is REFUSED</b> (since
///     2026-09-08; until then it was reported and done anyway). Its counterparty is that a
///     retirement must still COMPLETE: the type is held only while instances exist, the run is not
///     converged so the next sync asks again, and once the instances are gone the type is pruned
///     exactly as any other retired node. A type with no instances is never held.</item>
/// </list>
///
/// <para><b>Why hole B flipped from "report" to "refuse".</b> Measured on
/// <c>memex.systemorph.com</c> 2026-09-08 20:29:14Z: the Crm repository had retired
/// <c>Crm/Mail</c> on 09-06 with the one live record meant to be retyped on the portal before the
/// deploy; memex's Crm sync had been <c>Skipped</c> since 08-30, so the retirement arrived two days
/// late and before the retype; the import probed, found the instance, logged
/// <i>"Pruned 1 NodeType(s) that still have instances — STRANDED"</i>, and pruned. A client's record
/// then had no per-node hub, and the bake gate refused every rollout on the "regression". A warning
/// that arrives in the same breath as the deletion protects nothing.</para>
/// </summary>
public class ImportDanglingNodeTypeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // ——— pure: what the probe selects, what it keeps, and what it says ———————————————

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
    public void TheHold_KeepsTheDefinitionAndItsOwnSubtree_AndNothingElse()
    {
        var candidates = new[]
        {
            TypeNode("Pkg", "Widget"),
            Code("Pkg/Widget/Source", "Widget.cs"),
            Code("Pkg/Widget/Test", "WidgetTests.cs"),
            // A SHARED source in the partition's common folder is the repository's to retire —
            // keeping it would compile the retired code into every consumer for as long as one
            // instance existed, the exact state #3727 removed.
            Code("Pkg/Source", "WidgetView.cs"),
            Page("Pkg", "Doc"),
            // A sibling whose id merely STARTS with the held id is not under it.
            TypeNode("Pkg", "WidgetLegacy"),
        };

        var kept = NodeTypeInstanceProbe.WithoutHeld(candidates, ["Pkg/Widget"]);

        // The hold spares the definition and its default Source/Test subtree, nothing more.
        kept.Select(n => n.Path).Should().BeEquivalentTo(
            new[] { "Pkg/Source/WidgetView.cs", "Pkg/Doc", "Pkg/WidgetLegacy" },
            JsonSerializerOptions.Default);
        NodeTypeInstanceProbe.WithoutHeld(candidates, []).Should().HaveCount(candidates.Length,
            "with nothing held the prune set is untouched — the hold must not be a general filter");
    }

    [Fact(Timeout = 60000)]
    public void TheReport_NamesTheTypeAndThePaths_AndSaysItWasNotPruned()
    {
        var held = new NodeTypeInstanceProbe.StrandedInstances(
            "Pkg/Widget", ["TestData/a", "TestData/b"], 2, Truncated: false);

        var text = NodeTypeInstanceProbe.Describe([held]);

        text.Should().NotBeNull();
        text.Should().Contain("Pkg/Widget").And.Contain("TestData/a").And.Contain("TestData/b");
        text.Should().Contain("NOT pruned",
            "the line is read by the person who has to act — it must say the type is still there");
        text.Should().NotContain("STRANDED",
            "nothing is stranded any more; the old wording would send an operator to restore a "
            + "type that was never deleted");
        NodeTypeInstanceProbe.Describe([]).Should().BeNull(
            "a healthy prune must add no noise at all — a report that always fires is ignored");

        var stamp = NodeTypeInstanceProbe.PendingRetirementOf(
            held, "Pkg import abc123", new DateTimeOffset(2026, 9, 8, 20, 29, 14, TimeSpan.Zero));
        stamp.Should().StartWith("Retired by Pkg import abc123 ")
            .And.Contain("TestData/a")
            .And.Contain("2 instance(s)");
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

    // ——— hole B: a type with instances is HELD; the retirement completes once they are gone ———

    /// <summary>
    /// 🚨 THE ONE THAT MATTERS. A source drops a NodeType while the mesh still holds an instance of
    /// it — in ANOTHER partition, which is the realistic shape (a package's type, a user's data).
    ///
    /// <para>Three things are asserted in sequence, and the sequence is the contract:</para>
    /// <list type="number">
    ///   <item>The prune is REFUSED: the definition stays, stamped <c>PendingRetirement</c>; the
    ///     instance stays readable; the result names the type as held, not pruned, and is not
    ///     converged (so no marker licences the next run to skip the question).</item>
    ///   <item>The activity says so, naming the type AND the instance — the actionable half.</item>
    ///   <item>Once the instance is deleted, the SAME source content prunes the type — a deliberate
    ///     retirement still lands; the hold was a wait, not a veto.</item>
    /// </list>
    /// </summary>
    [Fact(Timeout = 300000)]
    public async Task Prune_OfANodeTypeWithLiveInstances_IsRefused_UntilTheInstancesAreGone()
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
        await WaitForInstanceListing(typePath, instancePath, present: true);

        var attemptsBefore = await AttemptPaths(partition);

        // 3. The source retires the type. FullReplace (the default) would prune it — and must not.
        var retired = new FakeRepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "Doc")],
        };
        var second = await StaticRepoImporter.ImportSource(Mesh, retired)
            .FirstAsync().Timeout(180.Seconds()).Await();

        Output.WriteLine(
            $"outcome={second.Outcome} pruned=[{string.Join(", ", second.PrunedPaths)}] "
            + $"held=[{string.Join(", ", second.HeldNodeTypePaths)}] preserved={second.Preserved}");

        second.PrunedPaths.Should().NotContain(typePath,
            "a NodeType that still has instances is never pruned by a repository-driven import — "
            + "deleting it takes the instances' per-node hub away (memex.systemorph.com, 2026-09-08)");
        second.HeldNodeTypePaths.Should().Contain(typePath,
            "the refusal reaches the caller as a fact it can act on, not only as a log line");
        second.Preserved.Should().BeGreaterThan(0,
            "a held type leaves the mesh AHEAD of the repo, and only a converged run may stamp a "
            + "green marker — otherwise the next trigger would skip the instance question for ever");
        second.Converged.Should().BeFalse();

        // The definition is still there, and it now says why.
        var definition = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.PendingRetirement
                is { Length: > 0 })
            .FirstAsync().Timeout(60.Seconds()).Await();
        var stamp = definition.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!.PendingRetirement!;
        Output.WriteLine($"PendingRetirement = {stamp}");
        stamp.Should().Contain(instancePath,
            "the stamp names what keeps the type alive, so the node itself explains its state");

        // The instance is still readable — it was the point.
        var instance = await Mesh.GetWorkspace().GetMeshNodeStream(instancePath)
            .Take(1).Timeout(60.Seconds()).Await();
        instance.NodeType.Should().Be(typePath);

        var report = await TerminalSummary(partition, attemptsBefore);
        report.Should().Contain(typePath);
        report.Should().Contain(instancePath,
            "naming the TYPE alone leaves an operator with the same manual `search nodeType:{Type}` "
            + "they had before — the instances are the actionable half");
        report.Should().NotContain("STRANDED");

        // 4. The instance is retyped/deleted — here, deleted — and the SAME content completes the
        //    retirement on the next run. The prior run's marker was not green, so nothing skips.
        await meshService.DeleteNode(instancePath)
            .Take(1).Should().Within(60.Seconds()).Emit("the instance must be gone before the re-run");
        await WaitForInstanceListing(typePath, instancePath, present: false);

        var third = await StaticRepoImporter.ImportSource(Mesh, retired)
            .FirstAsync().Timeout(180.Seconds()).Await();
        Output.WriteLine(
            $"outcome={third.Outcome} pruned=[{string.Join(", ", third.PrunedPaths)}] "
            + $"held=[{string.Join(", ", third.HeldNodeTypePaths)}]");

        third.HeldNodeTypePaths.Should().BeEmpty("with no instances left there is nothing to hold for");
        third.PrunedPaths.Should().Contain(typePath,
            "a retirement is a WAIT for the instances, never a veto — once they are gone the "
            + "repository's deletion lands exactly as for any other retired node");
    }

    /// <summary>
    /// The negative control for the hold: a retired NodeType WITHOUT instances is pruned on the
    /// first pass, as it always was. Without this the hold could quietly become "types are never
    /// pruned", which is the leak the retirement runbook exists to prevent.
    /// </summary>
    [Fact(Timeout = 300000)]
    public async Task Prune_OfANodeTypeWithoutInstances_Prunes()
    {
        var partition = "Pn" + Guid.NewGuid().ToString("N")[..8];
        var typePath = $"{partition}/Widget";

        var first = await StaticRepoImporter
            .ImportSource(Mesh, new FakeRepoSource(partition)
            {
                Root = Space(partition),
                Nodes = [TypeNode(partition, "Widget"), Page(partition, "Doc")],
            })
            .FirstAsync().Timeout(180.Seconds()).Await();
        first.Failed.Should().Be(0);

        var second = await StaticRepoImporter
            .ImportSource(Mesh, new FakeRepoSource(partition)
            {
                Root = Space(partition),
                Nodes = [Page(partition, "Doc")],
            })
            .FirstAsync().Timeout(180.Seconds()).Await();

        Output.WriteLine(
            $"outcome={second.Outcome} pruned=[{string.Join(", ", second.PrunedPaths)}] "
            + $"held=[{string.Join(", ", second.HeldNodeTypePaths)}]");

        second.HeldNodeTypePaths.Should().BeEmpty("nothing keeps an instance-less type alive");
        second.PrunedPaths.Should().Contain(typePath,
            "pruning a retired NodeType with no instances is the shipped contract and must stay so");
        second.Preserved.Should().Be(0);
    }

    // ——— helpers ————————————————————————————————————————————————————————————————————

    /// <summary>
    /// The probe reads the eventually-consistent index, so a test that changes the instance set
    /// waits for the index to reflect it before importing — otherwise it measures index lag, not
    /// the decision.
    /// </summary>
    private async Task WaitForInstanceListing(string typePath, string instancePath, bool present)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(typePath)).AsSystem())
                .Take(1))
            .Where(c => c.Items.Any(n =>
                string.Equals(n.Path, instancePath, StringComparison.OrdinalIgnoreCase)) == present)
            .FirstAsync().Timeout(120.Seconds()).Await();
    }

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

    private static MeshNode Code(string ns, string id) => new(id, ns)
    {
        NodeType = "Code", Name = id, State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "// code" },
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
