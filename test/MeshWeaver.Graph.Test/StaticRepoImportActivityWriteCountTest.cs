using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
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
/// The import's activity log must cost <b>O(number of items)</b>, not O(items²).
///
/// <para>Every <c>stream.Update</c> on an activity node re-serialises that node's WHOLE content to
/// compute its patch, so a per-item <c>AppendLog</c> over n items serialises 1 + 2 + … + n copies of
/// a growing document. On memex-cloud that shape produced <b>5,239 writes and ~719 MB serialised for
/// ONE import activity</b> (141 kB of content), ~7.1 GB across 25,579 activity nodes, and was the
/// dominant term in the pod's 51%-of-CFS-periods throttling. Not one non-import node appeared in the
/// top 20 — the importer's per-item logging was the burner.</para>
///
/// <para>The fix is to append <b>once per PHASE</b> (upsert phase, prune phase) with an
/// <c>AddRange</c>-style call instead of once per item, so the number of writes an import makes is
/// independent of how many items that phase handled. The deliberate, accepted cost is coarser live
/// progress: a long import shows per-phase progress, not per-file.</para>
///
/// <para>What is asserted is the WRITE COUNT — the activity node's own revision counter
/// (<see cref="MeshNode.Version"/>, "+1 per real modification") — never the log text, so the test
/// cannot be satisfied by rewording a message. RED before the batching fix (writes grew 1:1 with the
/// item count), GREEN after (identical for both sizes).</para>
/// </summary>
public class StaticRepoImportActivityWriteCountTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Item counts that must produce the SAME number of activity writes.</summary>
    private const int SmallImport = 4;
    private const int LargeImport = 16;

    /// <summary>
    /// 🚨 THE REGRESSION GUARD. Two imports whose conflict phase keeps 4 and 16 nodes respectively
    /// must write the activity log the same number of times. Before batching, each kept node cost its
    /// own <c>stream.Update</c> (and each of those re-serialised the whole, by then larger, node), so
    /// the counts differed by exactly the item delta — the O(n²) shape.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task ImportActivityWrites_DoNotGrowWithTheNumberOfItemsInAPhase()
    {
        var small = await ActivityWritesForConflictPhase(SmallImport);
        var large = await ActivityWritesForConflictPhase(LargeImport);

        Output.WriteLine(
            $"activity writes: {SmallImport} kept items -> {small}; {LargeImport} kept items -> {large}");

        large.Should().Be(small,
            "the import must log its conflict phase in ONE write regardless of how many items it "
            + $"handled — {LargeImport} items cost {large} writes vs {small} for {SmallImport}, i.e. the "
            + "per-item AppendLog is back and every one of those writes re-serialises the whole "
            + "activity node (O(n²) CPU — the memex-cloud CFS-throttling burner)");

        // …and the absolute figure stays a small constant: opening line, root line, one phase line,
        // terminal summary. A test that only compared the two sizes would still pass if BOTH grew.
        large.Should().BeLessThan(LargeImport,
            "the write count is per-phase, so it must stay well under the per-item count");
    }

    /// <summary>
    /// Runs a two-step import into a fresh partition and returns how many times the SECOND import's
    /// attempt-activity node was written.
    /// <list type="number">
    ///   <item>import <paramref name="items"/> nodes (creates them);</item>
    ///   <item>re-import the same paths with CHANGED content under a two-way policy whose baseline is
    ///     <see cref="DateTimeOffset.MinValue"/> — so every live node counts as "newer on the server"
    ///     and every one of them takes the <c>↩ Kept local change</c> branch.</item>
    /// </list>
    /// Step 2 writes no content at all (everything is preserved), so the attempt node's revisions are
    /// exactly the import's own logging — the quantity under test, isolated.
    /// </summary>
    private async Task<long> ActivityWritesForConflictPhase(int items)
    {
        var partition = "Wc" + Guid.NewGuid().ToString("N")[..8];
        var source = new FakeRepoSource(partition)
        {
            Root = new MeshNode(partition)
            {
                Name = "Write Count", NodeType = "Space", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "# Write Count\n\nfixture." }
            },
            Nodes = Pages(partition, items, "v1")
        };

        var first = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(120.Seconds());
        first.Outcome.Should().Be("Imported");
        first.Count.Should().Be(items, "the first import must actually materialize the fixture");

        var beforeAttempts = await AttemptIds(partition);

        // Same paths, different content → new fingerprint (no short-circuit) and a REAL conflict on
        // every node (past the unchanged-token skip, which logs nothing).
        source.Nodes = Pages(partition, items, "v2");
        var conflicted = await StaticRepoImporter.ImportSource(
                Mesh, source, null,
                new ImportConflictPolicy(PreserveServerNewer: true, Since: DateTimeOffset.MinValue))
            .FirstAsync().Timeout(120.Seconds());
        conflicted.Preserved.Should().Be(items,
            "every node must take the kept-local-change branch — that is the phase being measured");

        // The second import opens exactly one fresh attempt node — that is the one being measured.
        var attemptPath = Assert.Single(
            (await AttemptIds(partition)).Except(beforeAttempts, StringComparer.Ordinal).ToArray());

        // The terminal write is the LAST one the import makes (the per-path write queue is serial),
        // so a non-Running status means every earlier write has landed and Version is final.
        var attempt = await Mesh.GetWorkspace().GetMeshNodeStream(attemptPath)
            .Where(n => n.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions)
                is { Status: not ActivityStatus.Running })
            .FirstAsync().Timeout(60.Seconds());

        return attempt!.Version;
    }

    /// <summary>The per-run attempt nodes under the partition's <c>_Activity</c> namespace.</summary>
    private async Task<IReadOnlyList<string>> AttemptIds(string partition)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var change = await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{partition}/_Activity scope:children nodeType:{ActivityNodeType.NodeType}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync().Timeout(30.Seconds());
        // The lock is `import-{fingerprint}`; an attempt is that id plus a per-run suffix, and its
        // Name carries "attempt" (StaticRepoImporter.Import). Manifests are separate ids.
        return change.Items
            .Where(n => n.Name?.Contains("attempt", StringComparison.Ordinal) == true)
            .Select(n => n.Path)
            .ToArray();
    }

    private static List<MeshNode> Pages(string partition, int count, string revision) =>
        Enumerable.Range(0, count)
            .Select(i => new MeshNode($"Page{i}", partition)
            {
                NodeType = "Markdown", Name = $"Page {i}", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = $"# Page {i}\n\n{revision}" }
            })
            .ToList();

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
