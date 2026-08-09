using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// End-to-end proof of #734: the tracked-change view is COMPUTED from the version history, not
/// stored. A document edited N times by N users yields N tracked entries carrying the right authors
/// and texts — and no <c>_Tracking</c> satellite exists anywhere under the document.
/// <para>
/// Uses file-system persistence so <c>FileSystemVersionStore</c> writes real version snapshots; the
/// projection reads them back through the registered <see cref="IVersionQuery"/> exactly as the
/// collaborative markdown view does.
/// </para>
/// </summary>
public class TrackedChangeProjectionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private string? _tempDir;

    private string GetTempDir()
    {
        if (_tempDir != null) return _tempDir;
        _tempDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTrackedChangeTest", $"test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        return _tempDir;
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(GetTempDir())
            .AddGraph()
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>Per-step wait budget — a step slower than this is stuck, not slow.</summary>
    private static TimeSpan Step => TimeSpan.FromSeconds(8);

    private const string Baseline =
        "The quarterly report covers revenue, headcount and outlook.\n\n" +
        "Revenue grew steadily. Headcount stayed flat. Outlook remains cautious.\n";

    /// <summary>
    /// Edits the document as <paramref name="author"/>, replacing <paramref name="from"/> with
    /// <paramref name="to"/>. The write carries the author's identity, so the version snapshot
    /// records who made it — that stamp is the whole attribution mechanism.
    /// </summary>
    private async Task EditAs(string path, string author, string from, string to)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetCircuitContext(new AccessContext { ObjectId = author, Name = author });
        try
        {
            await Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Update(node =>
                {
                    var markdown = MarkdownOverviewLayoutArea.GetMarkdownContent(node);
                    markdown.Should().Contain(from, "the edit must find its target text");
                    return MarkdownOverviewLayoutArea.WithMarkdownContent(
                        node, markdown.Replace(from, to), Mesh.JsonSerializerOptions);
                })
                .Should().Within(Step).Emit();
        }
        finally
        {
            access.SetCircuitContext(null);
        }
    }

    private IObservable<IList<MeshNodeVersion>> WaitForVersions(string path, int atLeast)
    {
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        return Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => versionQuery.GetVersions(path).ToList())
            .Where(v => v.Count >= atLeast);
    }

    /// <summary>
    /// The node ONCE it has caught up to <paramref name="atLeastVersion"/> — the newest version the
    /// history says its edits landed at.
    ///
    /// <para>🚨 Deliberately NOT a bare <c>Take(1)</c> on the stream. <c>GetMeshNodeStream</c>
    /// replays the node's currently cached snapshot, and immediately after an edit that is still the
    /// PRE-edit one: <c>stream.Update</c> returns the locally computed node optimistically and the
    /// storage layer stamps the reconciled version a moment later ("take the next emission off the
    /// same handle" for the owner's reconciled state). <c>ChangeProjection.Between</c> projects
    /// only versions STRICTLY OLDER than the node it is handed, so a stale snapshot makes its
    /// step set empty and the projection comes back EMPTY — with every version row already
    /// sitting in the store. Waiting on the version ROWS does not imply the NODE has caught up: the
    /// history and the node are two different stores, and a bare <c>Take(1)</c> silently asserted
    /// against whichever snapshot happened to be cached.</para>
    ///
    /// <para>The live view never trips on this, which is why it stayed hidden:
    /// <c>CollaborativeMarkdownView</c> re-projects with <c>Switch</c> on every node emission, so a
    /// stale emission yields an empty list for an instant and is immediately superseded. Only a
    /// ONE-SHOT assertion has to wait for the emission that actually carries the edit.</para>
    /// </summary>
    private IObservable<MeshNode> NodeAtVersion(string path, long atLeastVersion) =>
        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && n.Version >= atLeastVersion)
            .Take(1)!;

    [Fact(Timeout = 60000)]
    public async Task TrackedChanges_AreProjectedFromVersionHistory_WithNoTrackingSatellite()
    {
        var path = $"test/tracked-{Guid.NewGuid():N}"[..24];
        var node = MeshNode.FromPath(path) with
        {
            Name = "Quarterly Report",
            NodeType = MarkdownNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = Baseline }
        };
        await NodeFactory.CreateNode(node).Should().Within(Step).Emit();

        // Three edits by three different people — each a normal versioned write, nothing "suggested".
        await EditAs(path, "alice", "grew steadily", "grew SEVENTEEN percent");
        await EditAs(path, "bob", "stayed flat", "stayed FLATTISH");
        await EditAs(path, "carol", "remains cautious", "remains OPTIMISTIC");

        // 4 snapshots: the create plus the three edits.
        var versions = await WaitForVersions(path, 4).Should().Within(Step).Emit();
        Output.WriteLine($"Version snapshots: {string.Join(", ", versions.Select(v => v.Version))}");

        // Same discipline as the revert test: read the node AT the newest recorded version. Three
        // edits give this one more slack than the single-edit case, but the hazard is identical —
        // a stale snapshot projects nothing (see NodeAtVersion).
        var current = await NodeAtVersion(path, versions.Max(v => v.Version)).Should().Within(Step).Emit();
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();

        // The reader names the baseline — here the version the document was created at, so the
        // projection covers every edit since.
        var changes = await ChangeProjection
            .Between(versionQuery, current, versions.Min(v => v.Version), Mesh.JsonSerializerOptions)
            .Should().Within(Step).Emit();

        foreach (var change in changes)
            Output.WriteLine($"  {change.ChangeType} by '{change.Author}' v{change.Version}: " +
                             $"'{change.OriginalText}' -> '{change.NewText}'");

        changes.Should().HaveCount(3, "three edits landed, so three tracked entries are derivable");

        var alice = changes.Should().ContainSingle(c => c.NewText != null && c.NewText.Contains("SEVENTEEN")).Subject;
        alice.Author.Should().Be("alice");
        alice.OriginalText.Should().Contain("steadily");

        var bob = changes.Should().ContainSingle(c => c.NewText != null && c.NewText.Contains("FLATTISH")).Subject;
        bob.Author.Should().Be("bob");

        var carol = changes.Should().ContainSingle(c => c.NewText != null && c.NewText.Contains("OPTIMISTIC")).Subject;
        carol.Author.Should().Be("carol");

        changes.Select(c => c.Version).Should().OnlyHaveUniqueItems("each edit is its own version");
        changes.Should().AllSatisfy(c => c.CreatedAt.Should().BeAfter(DateTimeOffset.UnixEpoch));

        // 🚨 The point of the issue: NOTHING is persisted for any of this.
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var satellites = await mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{path}/{AnnotationExtensions.TrackingPartition}"))
            .Select(c => c.Items.Count)
            .Take(1)
            .Should().Within(Step).Emit();
        satellites.Should().Be(0, "tracked changes are computed from history — no _Tracking satellite is written");
    }

    /// <summary>
    /// The reader states the range and gets exactly that range. Picking a baseline AFTER an earlier
    /// edit excludes that edit from the redline — which is the whole point of naming two versions
    /// rather than being shown "everything that ever happened".
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Between_CoversOnlyTheChosenRange()
    {
        var path = $"test/range-{Guid.NewGuid():N}"[..22];
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "Ranged Report",
            NodeType = MarkdownNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = Baseline }
        }).Should().Within(Step).Emit();

        await EditAs(path, "alice", "grew steadily", "grew SEVENTEEN percent");
        var afterAlice = (await WaitForVersions(path, 2).Should().Within(Step).Emit()).Max(v => v.Version);

        await EditAs(path, "bob", "stayed flat", "stayed FLATTISH");
        await EditAs(path, "carol", "remains cautious", "remains OPTIMISTIC");

        var versions = await WaitForVersions(path, 4).Should().Within(Step).Emit();
        var current = await NodeAtVersion(path, versions.Max(v => v.Version)).Should().Within(Step).Emit();
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();

        // Baseline = the version alice's edit produced, so her change is BEHIND the baseline.
        var ranged = await ChangeProjection
            .Between(versionQuery, current, afterAlice, Mesh.JsonSerializerOptions)
            .Should().Within(Step).Emit();

        foreach (var change in ranged)
            Output.WriteLine($"  {change.ChangeType} by '{change.Author}': '{change.OriginalText}' -> '{change.NewText}'");

        ranged.Should().HaveCount(2, "only the two edits after the chosen baseline are in range");
        ranged.Select(c => c.Author).Order().Should().Equal("bob", "carol");
        ranged.Should().NotContain(c => c.NewText != null && c.NewText.Contains("SEVENTEEN"),
            "alice's edit is at the baseline, so it is not part of what changed SINCE it");

        // A baseline at (or past) the target is not a range at all — it yields nothing rather than
        // quietly falling back to some other pair.
        var empty = await ChangeProjection
            .Between(versionQuery, current, current.Version, Mesh.JsonSerializerOptions)
            .Should().Within(Step).Emit();
        empty.Should().BeEmpty("comparing a version with itself has no changes to show");
    }

    /// <summary>
    /// A range wider than the read cap still shows the FULL redline — it is the endpoint diff — but
    /// credits nobody. Loading a subset of the steps would leave a version-gap after the baseline,
    /// and the consecutive-pair attribution would hand every edit across that gap to whoever made
    /// the first surviving step: exactly the "attribute to the wrong person" this module refuses.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Between_RangeWiderThanTheCap_KeepsTheRedlineButCreditsNobody()
    {
        var path = $"test/cap-{Guid.NewGuid():N}"[..20];
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "Capped Report",
            NodeType = MarkdownNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = Baseline }
        }).Should().Within(Step).Emit();

        await EditAs(path, "alice", "grew steadily", "grew SEVENTEEN percent");
        await EditAs(path, "bob", "stayed flat", "stayed FLATTISH");
        await EditAs(path, "carol", "remains cautious", "remains OPTIMISTIC");

        var versions = await WaitForVersions(path, 4).Should().Within(Step).Emit();
        var current = await NodeAtVersion(path, versions.Max(v => v.Version)).Should().Within(Step).Emit();
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var oldest = versions.Min(v => v.Version);

        // maxSteps = 2 while the range holds 3 versions (the create plus alice's and bob's edits),
        // so the cap bites and attribution is dropped for the whole comparison.
        var capped = await ChangeProjection
            .Between(versionQuery, current, oldest, Mesh.JsonSerializerOptions, maxSteps: 2)
            .Should().Within(Step).Emit();

        capped.Should().HaveCount(3, "the redline is the endpoint diff — the read cap never narrows it");
        capped.Should().AllSatisfy(c => c.Author.Should().BeEmpty(
            "a partially-loaded history cannot say who made which edit, so it says nobody"));

        // Same range, cap high enough to load every step: attribution comes back.
        var attributed = await ChangeProjection
            .Between(versionQuery, current, oldest, Mesh.JsonSerializerOptions, maxSteps: MaxStepsForFullRange)
            .Should().Within(Step).Emit();
        attributed.Select(c => c.Author).Order().Should().Equal("alice", "bob", "carol");
    }

    /// <summary>Comfortably above the 3 versions the cap test's range holds.</summary>
    private const int MaxStepsForFullRange = 10;

    /// <summary>
    /// Reverting a projected change is a NORMAL versioned write: the text goes back and the revert
    /// itself becomes the newest version — which is what makes "reject" auditable instead of a
    /// satellite quietly disappearing.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task RevertingAProjectedChange_PutsTheTextBack_AndIsItselfVersioned()
    {
        var path = $"test/revert-{Guid.NewGuid():N}"[..23];
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "Revertible",
            NodeType = MarkdownNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = Baseline }
        }).Should().Within(Step).Emit();

        await EditAs(path, "dave", "remains cautious", "remains RECKLESS");
        var rows = await WaitForVersions(path, 2).Should().Within(Step).Emit();

        // Read the node AT the version the history just recorded — not whatever snapshot the
        // stream has cached. The rows landing does not mean the node has caught up (see
        // NodeAtVersion): projecting from a stale pre-edit node yields nothing at all.
        var edited = await NodeAtVersion(path, rows.Max(r => r.Version)).Should().Within(Step).Emit();
        MarkdownOverviewLayoutArea.GetMarkdownContent(edited).Should().Contain("RECKLESS",
            "the projection is only meaningful against the node that actually carries the edit");
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var change = (await ChangeProjection
                .Between(versionQuery, edited, rows.Min(r => r.Version), Mesh.JsonSerializerOptions)
                .Should().Within(Step).Emit())
            .Should().ContainSingle().Subject;
        change.Author.Should().Be("dave");

        // Revert exactly the way the view does: re-resolve against the LIVE node inside the lambda.
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(live =>
            {
                var clean = MarkdownOverviewLayoutArea.GetMarkdownContent(live);
                var resolved = ChangeRendering.ResolveEffective(change, clean, live.Version);
                return MarkdownOverviewLayoutArea.WithMarkdownContent(
                    live, ChangeRendering.Revert(clean, resolved), Mesh.JsonSerializerOptions);
            })
            .Should().Within(Step).Emit();

        // Wait on the CONDITION, not on the optimistic emission: the update returns the locally
        // computed node, whose Version the storage layer has not stamped yet.
        var live = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Select(n => MarkdownOverviewLayoutArea.GetMarkdownContent(n))
            .Where(text => text == Baseline)
            .Take(1)
            .Should().Within(Step).Emit();
        live.Should().Be(Baseline, "reverting the only change restores the baseline text exactly");

        var afterRevert = await WaitForVersions(path, 3).Should().Within(Step).Emit();
        afterRevert.Count.Should().BeGreaterThanOrEqualTo(3,
            "the revert is a normal versioned write, so it lands in the history like any other edit");
    }
}
