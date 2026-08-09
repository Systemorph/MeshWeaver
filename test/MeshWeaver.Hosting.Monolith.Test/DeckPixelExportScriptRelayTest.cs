using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end test for the <b>pixel-faithful</b> Deck → PDF export (issue #990). The sibling
/// <see cref="DeckExportScriptRelayTest"/> covers the content-faithful default; this one drives the
/// same <c>Templates/Export/Pdf</c> script with <see cref="ExportFidelity.Pixel"/>, which takes the
/// branch that composes the deck into one self-contained HTML document and prints it with a
/// headless browser.
///
/// <para><b>Never skipped, deterministic in both worlds.</b> The pixel branch needs a browser, and
/// whether one exists is a property of the machine — so the test asks the renderer's own probe and
/// then asserts the contract that applies: with a browser, a real PDF with one page per slide and
/// the raw-HTML slide's text on it; without one, a FAILED activity carrying the actionable message.
/// Either way the script had to compile and the pixel branch had to be reached, which is the thing
/// this test exists to guard.</para>
/// </summary>
public class DeckPixelExportScriptRelayTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMarkdownExport();

    [Fact(Timeout = 180000)]
    public async Task DeckExport_PixelFidelity_RendersInABrowser_OrRefusesClearly()
    {
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Pixel Deck Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();

        var deck = $"{space}/pitch";
        var s1 = $"{deck}/intro";
        var s2 = $"{deck}/design";
        await NodeFactory.CreateNode(MeshNode.FromPath(deck) with
        {
            Name = "Pixel Pitch",
            NodeType = DeckNodeType.NodeType,
            Content = new DeckContent { Title = "Pixel Pitch", Slides = [s1, s2] }
        }).Should().Emit();

        // Slide 1: a gradient background — invisible to the document-model renderer.
        await CreateSlide(s1, "Intro", 1,
            "# ALPHAWIDGET\n\nThe opening slide.",
            background: "linear-gradient(135deg, #667eea 0%, #764ba2 100%)");
        // Slide 2: authored as raw HTML with a CSS transform — dropped entirely by the AST path.
        await CreateSlide(s2, "Design", 2,
            "<div style=\"transform: rotate(-3deg); font-weight: 700;\"><h1>BETAWIDGET</h1></div>",
            background: "#0b1d3a");

        var request = new ExportDocumentRequest(deck, new DocumentExportOptions
        {
            Format = ExportFormat.Pdf,
            Fidelity = ExportFidelity.Pixel,
            CoverPage = false,
            TableOfContents = false
        });

        var dispatch = await Mesh
            .Observe<ExportDocumentResponse>(request, o => o.WithTarget(new Address(deck)))
            .Should().Within(30.Seconds()).Emit();

        dispatch.Message.Error.Should().BeNullOrEmpty("the export should start successfully");
        dispatch.Message.ActivityPath.Should().NotBeNullOrEmpty();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var terminal = await workspace
            .GetMeshNodeStream(dispatch.Message.ActivityPath)
            .Select(node => node?.Content as ActivityLog)
            .Should().Within(2.Minutes())
            .Match(log => log is not null && log.Status != ActivityStatus.Running);

        // Ask the SAME probe the export asked, so the expectation can never disagree with what the
        // script actually found.
        var renderer = Mesh.ServiceProvider.GetRequiredService<IPixelPdfRenderer>();
        var executable = await renderer.Probe().FirstAsync().ToTask();
        Output.WriteLine($"Pixel renderer executable: {executable ?? "<none>"}");

        var messages = string.Join("\n  ", terminal!.Messages.Select(m => $"[{m.LogLevel}] {m.Message}"));

        if (executable is null)
        {
            terminal.Status.Should().Be(ActivityStatus.Failed,
                because: "pixel fidelity without a browser must refuse, never quietly downgrade to "
                         + $"content fidelity. Messages:\n  {messages}");
            messages.Should().Contain("headless Chromium",
                "the failure must tell the operator exactly what is missing");
            await NodeFactory.DeleteNode(space).Should().Emit();
            return;
        }

        terminal.Status.Should().Be(ActivityStatus.Succeeded,
            because: $"the deck should print in the browser. Messages:\n  {messages}");

        var rendered = terminal.ReturnValue!.Value.Deserialize<RenderedDocument>(Mesh.JsonSerializerOptions);
        rendered.Should().NotBeNull();
        rendered!.Format.Should().Be(ExportFormat.Pdf);
        rendered.MimeType.Should().Be("application/pdf");
        rendered.Content.Should().NotBeNull().And.NotBeEmpty();

        using var pdf = PdfDocument.Open(rendered.Content);
        var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        Output.WriteLine($"Pixel deck PDF pages={pdf.NumberOfPages}\n{text}");

        pdf.NumberOfPages.Should().Be(2, "the page box IS one 16:9 slide, so two slides are two pages");
        text.Should().Contain("ALPHAWIDGET");
        // The raw-HTML slide is exactly what the content-faithful export cannot carry.
        text.Should().Contain("BETAWIDGET", "a slide authored as raw HTML must survive the pixel export");

        var firstPage = pdf.GetPage(1);
        firstPage.Width.Should().BeGreaterThan(firstPage.Height, "16:9 slides print landscape");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    private async Task CreateSlide(string path, string name, int order, string body, string background)
        => await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = SlideNodeType.NodeType,
            Order = order,
            Content = new SlideContent { Content = body, Background = background }
        }).Should().Emit();
}
