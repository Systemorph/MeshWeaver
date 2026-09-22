using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>The actual generic Data area must follow node edits even when both HTML caches lag.</summary>
public class OverviewMarkdownFreshnessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Path = "Freshness/Page";
    private const string Stale = "<p>STALE CACHED BODY</p>";
    private const string Source = "# Page\n\nCurrent **body**\n\n@@(\"area/Search\")";

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddMeshNodes(new MeshNode("Freshness") { NodeType = "Space" },
            new MeshNode("Page", "Freshness")
            {
                Name = "Page", NodeType = "Markdown",
                Content = new MarkdownContent { Content = Source, PrerenderedHtml = Stale },
                PreRenderedHtml = Stale,
            },
            new MeshNode("Legacy", "Freshness") { PreRenderedHtml = Stale },
            new MeshNode("Record", "Freshness") { Content = new Space { Body = "A body property" } });

    [Fact]
    public async Task TheLiveDataArea_RendersCurrentSource_ThenItsEditAndClear()
    {
        var stored = await Mesh.GetMeshNode(Path).Should().Emit();
        Assert.Equal(Stale, stored?.PreRenderedHtml);
        var bodies = Bodies(Path);
        using var live = bodies.Subscribe();
        var initial = await bodies.Where(x => x.Length == 1).Should().Emit();
        AssertBody(initial[0], "Current <strong>body</strong>");
        Assert.Contains("layout-area", initial[0].Html?.ToString());
        Assert.DoesNotContain("<h1", initial[0].Html?.ToString());
        Assert.DoesNotContain("# Page", initial[0].Markdown?.ToString());

        const string edited = "# Page\n\nEdited **body**\n\n@@(\"area/Search\")";
        await WriteAndObserve(edited);
        var changed = await bodies.Where(x => x.Length == 1
            && x[0].Html?.ToString()?.Contains("Edited") == true).Should().Emit();
        AssertBody(changed[0], "Edited <strong>body</strong>");
        Assert.Contains("Edited **body**", changed[0].Markdown?.ToString());

        await WriteAndObserve("");
        await bodies.Where(x => x.Length == 0).Should().Emit();
    }

    [Fact]
    public async Task ASourceLessNode_KeepsItsLegacyHtml()
    {
        var body = await Bodies("Freshness/Legacy").Where(x => x.Length == 1).Should().Emit();
        Assert.Equal(Stale, body[0].Html);
        Assert.Equal("Freshness/Legacy", body[0].NodePath);
    }

    [Fact]
    public async Task ABodyPropertyAlone_DoesNotAddADocumentToTheGenericDataArea()
    {
        var body = await Bodies("Freshness/Record").Should().Emit();
        Assert.Empty(body);
    }

    [Fact]
    public async Task LegacyPascalCaseJson_StillUsesCurrentSource()
    {
        const string legacySource = "# Page\n\nLegacy **body**";
        await Mesh.GetMeshNodeStream(Path).Update(node => node with
        {
            Content = JsonSerializer.SerializeToElement(new { Content = legacySource, PrerenderedHtml = Stale }),
            PreRenderedHtml = Stale,
        }).Should().Emit();
        var body = await Bodies(Path).Where(x => x.Length == 1
            && x[0].Html?.ToString()?.Contains("Legacy") == true).Should().Emit();
        AssertBody(body[0], "Legacy <strong>body</strong>");
    }

    private static void AssertBody(MarkdownControl body, string expected)
    {
        Assert.Contains(expected, body.Html?.ToString());
        Assert.DoesNotContain("STALE", body.Html?.ToString());
        Assert.Equal(Path, body.NodePath);
    }

    private async Task WriteAndObserve(string source)
    {
        await Mesh.GetMeshNodeStream(Path).Update(node => node with
        {
            Content = new MarkdownContent { Content = source, PrerenderedHtml = Stale },
            PreRenderedHtml = Stale,
        }).Should().Emit();
        var stored = await Mesh.GetMeshNodeStream(Path)
            .Where(node => MarkdownBody.Of(node, Mesh.JsonSerializerOptions) == source).Should().Emit();
        Assert.Equal(Stale, stored.PreRenderedHtml);
    }

    private IObservable<MarkdownControl[]> Bodies(string path)
    {
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ContentDataArea);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(path), reference);
        return stream.GetControlStream(reference.Area!).OfType<StackControl>()
            .Select(root => root.Areas.Select(area => stream.GetControlStream(area.Area!.ToString()!))
                .CombineLatest().Select(children => children.OfType<MarkdownControl>().ToArray()))
            .Switch();
    }
}
