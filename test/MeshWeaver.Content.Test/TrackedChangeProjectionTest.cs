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

    private IObservable<MeshNode> CurrentNode(string path) =>
        Mesh.GetWorkspace().GetMeshNodeStream(path).Where(n => n is not null).Take(1)!;

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

        var current = await CurrentNode(path).Should().Within(Step).Emit();
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();

        var changes = await ChangeProjection
            .FromHistory(versionQuery, current, Mesh.JsonSerializerOptions)
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
        await WaitForVersions(path, 2).Should().Within(Step).Emit();

        var edited = await CurrentNode(path).Should().Within(Step).Emit();
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var change = (await ChangeProjection.FromHistory(versionQuery, edited, Mesh.JsonSerializerOptions)
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
