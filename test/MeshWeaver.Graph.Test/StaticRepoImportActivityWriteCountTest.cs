using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    /// <summary>How many messages each side of the head-size comparison appends.</summary>
    private const int ShortActivity = ActivityLog.MessageWindowLimit;      // never overflows the window
    private const int LongActivity = ActivityLog.MessageWindowLimit * 4;   // overflows it three times over

    /// <summary>
    /// Messages per append call. Purely a test-runtime concession: a cross-hub activity write costs
    /// ~0.2 s here, so 2,500 single-message appends would take longer than the whole suite's budget.
    /// It does not soften what is measured — the quantity under test is the SIZE of the content each
    /// write serialises, and the window behaves identically however the messages arrive.
    /// </summary>
    private const int AppendBatch = 25;

    /// <summary>
    /// 🚨 THE SECOND REGRESSION GUARD: the cost of ONE append must not depend on how much the activity
    /// has already logged.
    ///
    /// <para>Batching per phase (the test above) bounds how MANY writes an import makes. It does nothing
    /// about how EXPENSIVE each one is: every <c>stream.Update</c> re-serialises the whole
    /// <c>MeshNode.Content</c> to compute its patch, so appending to a list that keeps growing is O(N²)
    /// however few writes you make — and the cross-hub path is worse, since an RFC 7396 merge patch
    /// clones the changed array whole and the three-way merge clones the previous one too, shipping
    /// ~2N elements to append one. No delta field escapes that; only bounding the head does.</para>
    ///
    /// <para>What is asserted is the SERIALISED SIZE of the activity's content — the exact quantity
    /// every write pays, measured, not modelled — after a short activity and after one four times as
    /// long. They must be within a small constant of each other. Before the window they differed by the
    /// message ratio (4×); the absolute bound below is what stops both sides simply growing together.</para>
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task AppendCost_DoesNotGrowWithTheLengthOfTheActivity()
    {
        var shortRun = await AppendAndMeasure(ShortActivity);
        var longRun = await AppendAndMeasure(LongActivity);

        Output.WriteLine(
            $"head bytes: {ShortActivity} msgs -> {shortRun.HeadBytes:N0}; {LongActivity} msgs -> {longRun.HeadBytes:N0}"
            + $" (ratio {(double)longRun.HeadBytes / shortRun.HeadBytes:F2}×)");
        Output.WriteLine(
            $"cumulative serialised bytes: {ShortActivity} msgs -> {shortRun.CumulativeBytes:N0}; "
            + $"{LongActivity} msgs -> {longRun.CumulativeBytes:N0}");
        Output.WriteLine(
            $"activity writes: {ShortActivity} msgs -> {shortRun.Writes}; {LongActivity} msgs -> {longRun.Writes}; "
            + $"segments sealed: {shortRun.Segments} / {longRun.Segments}");

        longRun.HeadBytes.Should().BeLessThan(shortRun.HeadBytes * 3 / 2,
            $"a {LongActivity}-message activity must not carry a bigger head than a {ShortActivity}-message "
            + "one — Messages is a bounded window, so the per-append serialisation cost is a constant. "
            + "A ratio near 4× means the window is gone and every append is re-serialising the whole "
            + "transcript again (the O(n²) shape that burned ~719 MB on one memex-cloud import).");

        // The write count must stay at one per append call plus the amortised seal traffic — the window
        // must not be bought by turning every append into several writes.
        var appendCalls = (LongActivity + AppendBatch - 1) / AppendBatch;
        longRun.Writes.Should().BeLessThan(appendCalls + longRun.Segments * 2 + 4,
            $"{appendCalls} append calls cost one write each; sealing adds ONE extra head write per "
            + "segment, amortised over "
            + $"{ActivityLog.MessageWindowLimit - ActivityLog.MessageWindowKeep} messages");

        // …and nothing is lost: every message is either still in the window or durable in a segment.
        longRun.TotalMessageCount.Should().Be(LongActivity);
        longRun.MessagesInSegments.Should().Be(LongActivity - longRun.MessagesInWindow,
            "every message that left the window must be durable in an ActivityLogSegment satellite");
        longRun.Segments.Should().BeGreaterThan(0, "a four-window activity must have sealed segments");
    }

    private sealed record AppendMeasurement(
        int HeadBytes, long CumulativeBytes, long Writes, int Segments,
        int TotalMessageCount, int MessagesInWindow, int MessagesInSegments);

    /// <summary>
    /// Creates one activity in a fresh partition, appends <paramref name="messages"/> log lines to it
    /// ONE AT A TIME through the canonical <see cref="ActivityLogAppender"/>, and reports what each of
    /// those appends cost.
    /// <para><c>CumulativeBytes</c> sums the activity content's serialised size at every append — the
    /// real work the patcher does per write, observed rather than assumed.</para>
    /// </summary>
    private async Task<AppendMeasurement> AppendAndMeasure(int messages)
    {
        var partition = "Ms" + Guid.NewGuid().ToString("N")[..8];
        var imported = await StaticRepoImporter.ImportSource(Mesh, new FakeRepoSource(partition)
            {
                Root = new MeshNode(partition)
                {
                    Name = "Append Cost", NodeType = "Space", State = MeshNodeState.Active,
                    Content = new MarkdownContent { Content = "# Append Cost\n\nfixture." }
                },
                Nodes = Pages(partition, 1, "v1")
            })
            .FirstAsync().Timeout(120.Seconds());
        imported.Outcome.Should().Be("Imported");

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var activityId = "measure" + Guid.NewGuid().ToString("N")[..8];
        var activityPath = $"{partition}/_Activity/{activityId}";
        await meshService.CreateNode(new MeshNode(activityId, $"{partition}/_Activity")
        {
            Name = "Append cost measurement",
            NodeType = ActivityNodeType.NodeType,
            MainNode = partition,
            State = MeshNodeState.Active,
            Content = new ActivityLog(ActivityCategory.DataUpdate)
            {
                Id = activityId, HubPath = partition, Status = ActivityStatus.Running
            }
        }).FirstAsync().Timeout(60.Seconds());

        var cumulative = 0L;
        var writes = 0;
        MeshNode last = null!;
        for (var sent = 0; sent < messages; sent += AppendBatch)
        {
            var batch = Enumerable.Range(sent, Math.Min(AppendBatch, messages - sent))
                .Select(i => new LogMessage(
                    $"line {i:D5}: a representative progress line for the measurement",
                    LogLevel.Information))
                .ToArray();
            last = await ActivityLogAppender.Append(Mesh, activityPath, batch)
                .FirstAsync().Timeout(60.Seconds());
            writes++;
            cumulative += ContentBytes(last);
        }

        var log = last.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions)!;
        var segments = await SegmentsOf(activityPath);

        Output.WriteLine(
            $"  {messages} messages in {writes} append calls -> node version {last.Version}, "
            + $"window {log.Messages.Count}, segments {segments.Count}");

        return new AppendMeasurement(
            HeadBytes: ContentBytes(last),
            CumulativeBytes: cumulative,
            Writes: last.Version,
            Segments: segments.Count,
            TotalMessageCount: log.TotalMessageCount,
            MessagesInWindow: log.Messages.Count,
            MessagesInSegments: segments.Sum(s => s.Messages.Count));
    }

    private int ContentBytes(MeshNode node) =>
        JsonSerializer.Serialize(node.Content, Mesh.JsonSerializerOptions).Length;

    /// <summary>
    /// The activity's sealed log segments, listed with a CHILDREN query — never a point-read of a
    /// segment path. A point-read of an absent satellite opens the shared stream cache's storm breaker
    /// on a path a concurrent write is about to use, and the breaker fast-fails writes too.
    /// </summary>
    private async Task<IReadOnlyList<ActivityLogSegment>> SegmentsOf(string activityPath)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var change = await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{ActivityLogAppender.SegmentNamespace(activityPath)} scope:children "
                + $"nodeType:{ActivityLogAppender.SegmentNodeType}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync().Timeout(30.Seconds());
        return change.Items
            .Select(n => n.ContentAs<ActivityLogSegment>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null)
            .Select(s => s!)
            .OrderBy(s => s.FirstOrdinal)
            .ToArray();
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
