using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Pins where the markdown redline is allowed to appear.
/// <para>
/// Reading a document is not reviewing it, so the document's own page renders WITHOUT any
/// tracked-change overlay. The redline is switched on in exactly one place — the version comparison
/// — and only once the reader has said which version is being compared to which. These tests assert
/// both halves: the Overview carries no comparison, and VersionDiff carries the one that was asked
/// for (pinned to a historical target, or following the live document).
/// </para>
/// File-system persistence so <c>FileSystemVersionStore</c> writes real version snapshots.
/// </summary>
public class VersionCompareViewTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private string? _tempDir;

    private string GetTempDir()
    {
        if (_tempDir != null) return _tempDir;
        _tempDir = Path.Combine(Path.GetTempPath(), "MeshWeaverVersionCompareTest", $"test_{Guid.NewGuid():N}");
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

    private static TimeSpan Step => TimeSpan.FromSeconds(15);

    private const string Baseline =
        "The quarterly report covers revenue, headcount and outlook.\n\n" +
        "Revenue grew steadily. Headcount stayed flat. Outlook remains cautious.\n";

    /// <summary>Creates a markdown node and edits it twice, so the history holds three versions.</summary>
    private async Task<string> SeedEditedDocument()
    {
        var path = $"test/cmp-{Guid.NewGuid():N}"[..20];
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "Comparable Report",
            NodeType = MarkdownNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = Baseline }
        }).Should().Within(Step).Emit();

        await Edit(path, "grew steadily", "grew SEVENTEEN percent");
        await Edit(path, "remains cautious", "remains OPTIMISTIC");
        return path;
    }

    private async Task Edit(string path, string from, string to) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(node => MarkdownOverviewLayoutArea.WithMarkdownContent(
                node,
                MarkdownOverviewLayoutArea.GetMarkdownContent(node).Replace(from, to),
                Mesh.JsonSerializerOptions))
            .Should().Within(Step).Emit();

    /// <summary>The recorded versions of a node, oldest first, once at least <paramref name="atLeast"/> exist.</summary>
    private async Task<IReadOnlyList<long>> Versions(string path, int atLeast)
    {
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var rows = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => versionQuery.GetVersions(path).ToList())
            .Where(v => v.Count >= atLeast)
            .Should().Within(Step).Emit();
        return rows.Select(r => r.Version).OrderBy(v => v).ToList();
    }

    /// <summary>
    /// The document's OWN page shows the document — no comparison, therefore no redline. This is the
    /// default for every markdown node, and nothing on the page can turn it on.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Overview_CarriesNoComparison_SoTheRedlineIsOff()
    {
        var path = await SeedEditedDocument();
        await Versions(path, 3);

        var markdown = await RenderMarkdownBody(path, MeshNodeLayoutAreas.OverviewArea, null);

        markdown.CompareFromVersion.Should().BeNull(
            "a document page declares no comparison, so the tracked-change redline never renders on it");
        markdown.CompareToVersion.Should().BeNull();
    }

    /// <summary>
    /// Comparing two historical versions PINS the view: the text is the document as of the `to`
    /// version, and reverting — which would write to the live document — is not offered.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task VersionDiff_BetweenTwoVersions_RendersThePinnedComparison()
    {
        var path = await SeedEditedDocument();
        var versions = await Versions(path, 3);
        var (from, to) = (versions[0], versions[1]);

        var markdown = await RenderMarkdownBody(
            path, MeshNodeLayoutAreas.VersionDiffArea, $"?from={from}&to={to}");

        markdown.CompareFromVersion.Should().Be(from, "the reader named the baseline");
        markdown.CompareToVersion.Should().Be(to, "and named the target — the view is pinned to it");
        markdown.CanEdit.Should().BeFalse(
            "a revert would write to the live document, which is not what is on screen");
        markdown.CanComment.Should().BeFalse("comments belong on the document's own page");
    }

    /// <summary>
    /// Comparing a version with the CURRENT document leaves the target open, so the redline follows
    /// further edits and each change can be reverted out of the live document.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task VersionDiff_AgainstCurrent_FollowsTheLiveDocument()
    {
        var path = await SeedEditedDocument();
        var versions = await Versions(path, 3);

        var markdown = await RenderMarkdownBody(
            path, MeshNodeLayoutAreas.VersionDiffArea, $"?version={versions[0]}");

        markdown.CompareFromVersion.Should().Be(versions[0]);
        markdown.CompareToVersion.Should().BeNull("the target is the live document, not a snapshot");
        markdown.CanEdit.Should().BeTrue("the test user may update the node, so reverting is offered");
    }

    /// <summary>
    /// The raw source diff stays one click away — <c>?view=source</c> renders the side-by-side
    /// editor instead of the redline, so front matter and link syntax remain inspectable.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task VersionDiff_SourceView_RendersTheSideBySideDiff()
    {
        var path = await SeedEditedDocument();
        var versions = await Versions(path, 3);

        var controls = await RenderAreaControls(
            path, MeshNodeLayoutAreas.VersionDiffArea, $"?version={versions[0]}&view=source");

        controls.OfType<DiffEditorControl>().Should().ContainSingle(
            "?view=source asks for the source diff");
        controls.OfType<CollaborativeMarkdownControl>().Should().BeEmpty(
            "the two views are alternatives, not a stack of both");
    }

    /// <summary>
    /// The version list is the picker: every version offers itself as either endpoint, and the
    /// one-click "compare with current" is on every row EXCEPT the current one — where it would
    /// compare a version with itself.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Versions_OffersCompareWithCurrent_OnEveryVersionButTheCurrentOne()
    {
        var path = await SeedEditedDocument();
        var versions = await Versions(path, 3);

        var controls = await RenderAreaControls(path, MeshNodeLayoutAreas.VersionsArea, null);
        var labels = controls.OfType<ButtonControl>()
            .Select(b => b.Data?.ToString() ?? "")
            .ToList();
        Output.WriteLine($"Buttons: {string.Join(" | ", labels)}");

        labels.Count(l => l == "Compare with current")
            .Should().Be(versions.Count - 1,
                "every version but the current one can be compared with the current document");

        // Both endpoints are offered per row, and Compare itself is present but inert until the
        // reader has named both.
        labels.Count(l => l.StartsWith("From")).Should().Be(versions.Count);
        labels.Count(l => l.StartsWith("To")).Should().Be(versions.Count);
        var compare = controls.OfType<ButtonControl>().Single(b => (b.Data?.ToString() ?? "") == "Compare");
        compare.Disabled.Should().Be(true, "a comparison needs two endpoints, and none are chosen yet");
    }

    /// <summary>Renders an area and returns the markdown body control it composes.</summary>
    private async Task<CollaborativeMarkdownControl> RenderMarkdownBody(string path, string area, string? id)
    {
        var controls = await RenderAreaControls(path, area, id);
        var markdown = controls.OfType<CollaborativeMarkdownControl>().FirstOrDefault();
        markdown.Should().NotBeNull($"'{area}' should compose a markdown body for a markdown node");
        return markdown!;
    }

    /// <summary>
    /// Renders an area and walks its whole control tree — rows are nested stacks, so a single level
    /// of children would miss every per-row button.
    /// </summary>
    private async Task<IReadOnlyList<UiControl>> RenderAreaControls(string path, string area, string? id)
    {
        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference(area) { Id = id };
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(path), reference);

        var root = await stream.GetControlStream(reference.Area!)
            .Should().Within(Step).Match(c => c is StackControl);

        var collected = new List<UiControl>();
        await Collect(stream, (UiControl)root!, collected);
        return collected;
    }

    private static async Task Collect(
        ISynchronizationStream<JsonElement> stream, UiControl control, List<UiControl> into)
    {
        into.Add(control);
        if (control is not StackControl stack)
            return;
        foreach (var area in stack.Areas)
        {
            var name = area.Area?.ToString();
            if (string.IsNullOrEmpty(name))
                continue;
            var child = await stream.GetControlStream(name)
                .Should().Within(TimeSpan.FromSeconds(10)).Match(c => c != null);
            if (child is UiControl ui)
                await Collect(stream, ui, into);
        }
    }
}
