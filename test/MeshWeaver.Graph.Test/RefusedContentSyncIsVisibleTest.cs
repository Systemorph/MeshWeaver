using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Issue #3101 — a refusal nobody can SEE, and nobody can EXPLAIN.</b>
///
/// <para>#3149 closed the first half: a refused content sync no longer stamps a green marker, so the
/// next boot re-attempts instead of skipping the Space for ever. What it could not answer is the two
/// questions the person holding the problem actually has.</para>
///
/// <list type="number">
///   <item><b>WHY?</b> Both content passes discarded the response's <c>Error</c> and the exception's
///     message, keeping only a node path. The activity was then reduced to guessing at the cause in
///     prose — <i>"most often a delivery over the transport's size budget"</i> — while the producer
///     had measured every file in order to split the write and thrown the numbers away. Three very
///     different problems (an asset tree the transport cannot carry; a content collection that is not
///     configured on the node; a path escaping the collection root) reported identically, and only
///     one of them is about size.</item>
///   <item><b>WHERE DOES THE AUTHOR LOOK?</b> The refusal lived on
///     <c>{Partition}/_Activity/import-…</c> — one operator record per import, in the partition's
///     bookkeeping. The person who committed the video opens the SPACE, and the Space said nothing.
///     Reading a partition-wide import attempt to discover that your own page's assets are missing is
///     the same distance from the problem as the learner who finds out by clicking play.</item>
/// </list>
///
/// <para><b>What these tests pin.</b> The reason is MEASURED, not boilerplate (test 1 asserts the
/// budget sentence names the file, its packaged size and the limit; test 2 is the control that could
/// falsify it — an identical refusal whose files all fit must NOT be reported as a size problem), and
/// the verdict is DURABLE ON THE NODE (test 1 reads the <c>_Activity/content-sync</c> ledger back off
/// the Space itself; test 3 is the control that it is not written for a Space that has no content
/// at all).</para>
///
/// <para>🚨 <b>The refusal is produced deterministically by the handler, not by the transport —
/// deliberately.</b> An over-budget delivery is only actually REFUSED where the Orleans transport is
/// in the path, and a monolith carries it perfectly well; a test that shipped 12 MB and waited for a
/// refusal would either pass for the wrong reason or need a cluster to say anything at all. What is
/// under test is the VISIBILITY of a refusal, not the transport's decision to make one. So the sync
/// is aimed at a folder that escapes the collection root — refused outright by
/// <c>ContentImportExtensions.SyncFiles</c>' own guard, and refused ahead of that in this mesh
/// because no <c>content</c> collection is mounted on a bare test node. Either way the delivery does
/// not land, which is the premise; the oversized file supplies the MEASUREMENT, and sizing it over
/// the budget is what makes the two halves separable — test 2 changes ONLY that.</para>
/// </summary>
public class RefusedContentSyncIsVisibleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A source that ships nodes and, optionally, inline content syncs.</summary>
    private sealed class ContentRepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; init; } = [];
        public MeshNode? Root { get; init; }
        public List<StaticContentSync> Syncs { get; init; } = [];

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
        public IReadOnlyList<StaticContentSync> EnumerateInlineContentSyncs() => Syncs;
    }

    private static MeshNode Space(string partition) =>
        new(partition) { Name = partition, NodeType = "Space", State = MeshNodeState.Active };

    private static MeshNode Page(string partition, string id) =>
        new(id, partition) { Name = id, NodeType = "Markdown", State = MeshNodeState.Active };

    /// <summary>The one collection-relative folder the handler refuses outright — it escapes the root.</summary>
    private const string RefusedTargetPath = "../escape";

    /// <summary>
    /// A file whose base64 form alone is over <see cref="ContentDeliveryBudget.BudgetBytes"/>, so no
    /// partitioning can put it in a delivery that fits — the shape of every one of the 25 course
    /// videos measured over budget in <c>MeshWeaver.Education</c>.
    /// </summary>
    private static InlineContentFile OversizedVideo() =>
        new("videos/module1-intro.mp4", new byte[ContentDeliveryBudget.BudgetBytes + 1]);

    private static InlineContentFile SmallPoster() =>
        new("videos/module1-intro.png", new byte[1024]);

    private static string Partition() => "Rv" + Guid.NewGuid().ToString("N")[..8];

    private static ContentRepoSource SourceWith(string partition, params InlineContentFile[] files) =>
        new(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "Lesson")],
            Syncs = [new StaticContentSync(partition, files, TargetPath: RefusedTargetPath)],
        };

    /// <summary>
    /// 🚨 THE PIN. A refused content sync must say WHY — naming the file, its packaged size and the
    /// budget it exceeds — and must leave that verdict on the SPACE, where the author of the content
    /// looks, not only in the partition's import bookkeeping.
    ///
    /// <para>Pre-fix: the reason was discarded at both call sites (only the path survived), so
    /// <c>RefusedContent</c> did not exist and no ledger was written anywhere near the node.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARefusedContentSync_NamesTheReasonAndLandsOnTheSpacesOwnLedger()
    {
        var partition = Partition();
        var source = SourceWith(partition, OversizedVideo(), SmallPoster());

        var result = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(240.Seconds());

        Output.WriteLine($"outcome = {result.Outcome}");
        foreach (var refused in result.RefusedContent)
            Output.WriteLine($"refused: {refused.NodePath} — {refused.Reason}");

        result.Outcome.Should().Be("ImportedWithRefusedContent",
            "the source declares assets the sync could not deliver (#3149)");

        var entry = result.RefusedContent.Should().ContainSingle(
            "exactly one owning node's content sync was refused").Subject;
        entry.NodePath.Should().Be(partition);

        entry.Reason.Should().NotBeNullOrWhiteSpace(
            "a refusal with no reason is the silence this issue is about, one layer in");
        entry.Reason.Should().Contain($"{ContentDeliveryBudget.BudgetBytes:N0}",
            "the LIMIT must be named — an operator cannot tell an over-budget delivery from any "
            + "other refusal without it (#3101)");
        entry.Reason.Should().Contain("module1-intro.mp4",
            "the file that cannot fit any delivery must be named — 'the Space is too big' is not "
            + "something anyone can act on, and the axis is per-file, not aggregate");
        entry.Reason.Should().Contain(
            $"{ContentDeliveryBudget.PackagedCost(OversizedVideo()):N0}",
            "the SIZE must be named, measured by the same cost function the partitioner uses so the "
            + "report can never describe a delivery nobody built");

        // 🚨 The durable, author-visible half: the verdict is ON THE SPACE, read back off the mesh.
        var ledger = await Mesh
            .GetMeshNode($"{partition}/_Activity/content-sync", 60.Seconds())
            .FirstAsync().Timeout(90.Seconds());

        ledger.Should().NotBeNull(
            "the person who committed the assets opens the Space, not the partition's import "
            + "bookkeeping — a refusal they cannot see is the defect (#3101)");
        var log = ledger!.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions);
        log.Should().NotBeNull();
        log!.Status.Should().Be(ActivityStatus.Warning,
            "the node's assets are NOT in the mesh, and a green ledger on a stale Space is exactly "
            + "the indistinguishability this issue reports");
        var text = string.Join(" ", log.Messages.Select(m => m.Message));
        Output.WriteLine($"ledger = {log.Status}: {text}");
        text.Should().Contain($"{ContentDeliveryBudget.BudgetBytes:N0}",
            "the ledger carries the same named size and limit as the import activity — it is the "
            + "copy the author actually reads");
    }

    /// <summary>
    /// 🚨 THE CONTROL THAT COULD FALSIFY THE ABOVE. Identical refusal, identical code path — only the
    /// file sizes change. A reason that always blames the budget would satisfy test 1 just as well
    /// and would be strictly worse than the guess it replaced: it would send every reader hunting a
    /// size problem that is not there.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARefusalWithNoOversizedFile_IsNotReportedAsASizeProblem()
    {
        var partition = Partition();
        var source = SourceWith(partition, SmallPoster());

        var result = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(240.Seconds());

        var entry = result.RefusedContent.Should().ContainSingle().Subject;
        Output.WriteLine($"refused: {entry.NodePath} — {entry.Reason}");

        entry.Reason.Should().NotBeNullOrWhiteSpace(
            "the refusal still has to say what went wrong");
        entry.Reason.Should().NotContain("per-delivery content budget",
            "no file here is over the budget, so blaming it would be boilerplate rather than a "
            + "measurement — the very substitution of prose for fact this change removes");
        entry.Reason.Should().NotContain($"{ContentDeliveryBudget.BudgetBytes:N0}",
            "the limit is quoted only where it was actually exceeded");
    }

    /// <summary>
    /// 🚨 THE SECOND CONTROL. The ledger is a statement about content, so a Space that declares NONE
    /// must not get one. A ledger written unconditionally would be noise on every node in the mesh
    /// and would make the entries that matter unfindable.
    ///
    /// <para>Absence is established from a CHILDREN LISTING, never a point read: a point read of a
    /// node that does not exist is a framework defect (it terminates the stream with a routing
    /// NotFound and opens the storm-breaker on the path), and a stale-negative listing is harmless
    /// here because the import has already completed.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ASpaceWithNoContent_GetsNoContentSyncLedger()
    {
        var partition = Partition();
        var source = new ContentRepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "Lesson")],
            // No content syncs at all — the overwhelmingly common case.
        };

        var result = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(240.Seconds());

        result.Outcome.Should().Be("Imported");
        result.RefusedContent.Should().BeEmpty();

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var children = await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{partition}/_Activity scope:children"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => c.Items)
            .FirstAsync().Timeout(90.Seconds());

        Output.WriteLine($"_Activity children = {string.Join(", ", children.Select(n => n.Id))}");
        children.Should().NotContain(n => n.Id == "content-sync",
            "a Space that declares no content has nothing to report, and a ledger on every node "
            + "would bury the ones that do");
    }
}
