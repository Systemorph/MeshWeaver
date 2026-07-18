using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end test for the <b>Deck → PDF</b> export (issue #423, piece 1). Verifies that
/// posting an <see cref="ExportDocumentRequest"/> at a <c>Deck</c> node routes through the SAME
/// script-templated pipeline as the markdown export (<c>Templates/Export/Pdf</c>), but the
/// template's <c>NodeType == "Deck"</c> branch resolves the deck's ordered slides and renders
/// ONE page per slide — content-faithful (each slide's text present) and landscape (16:9).
/// <para>Mirrors <see cref="ExportDocumentScriptRelayTest"/> for the dispatch/terminal shape and
/// <c>DeckLayoutAreaTest</c> for the deck/slide seeding; re-reads the PDF bytes with PdfPig
/// (the trick <c>PdfXlsxConversionTest</c> uses) to assert the rendered content.</para>
/// </summary>
public class DeckExportScriptRelayTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMarkdownExport();

    [Fact(Timeout = 120000)]
    public async Task DeckExport_RendersOnePagePerSlide_ContentFaithful_Landscape()
    {
        // ── Seed a Space partition holding a Deck with three ordered Slide children. Each
        // slide carries a UNIQUE single-word token (contiguous glyphs survive PDF text
        // extraction intact) so we can prove every slide's content made it into the PDF. ──
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Deck Export Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();

        var deck = $"{space}/pitch";
        var s1 = $"{deck}/intro";
        var s2 = $"{deck}/metrics";
        var s3 = $"{deck}/summary";
        await NodeFactory.CreateNode(MeshNode.FromPath(deck) with
        {
            Name = "Pitch Deck",
            NodeType = DeckNodeType.NodeType,
            Content = new DeckContent { Title = "Pitch Deck", Slides = [s1, s2, s3] }
        }).Should().Emit();

        await CreateSlide(s1, "Intro", 1, "# Introduction\n\nThis opening slide is about ALPHAWIDGET.");
        // A markdown table exercises the structured-content path (tables must render, not dump).
        await CreateSlide(s2, "Metrics", 2,
            "## Metrics\n\n| Region | Value |\n|---|---|\n| NORTHREGION | 42 |\n| SOUTHREGION | 17 |\n");
        await CreateSlide(s3, "Summary", 3, "# Summary\n\nFinal thoughts on GAMMAWIDGET.");

        // ── Dispatch the deck export. Handler returns immediately with the activity path
        // (no wait-for-terminal in the hub); format stays PDF, the DECK branch is chosen by
        // the template from the node's NodeType, not by the request. ──
        var request = new ExportDocumentRequest(deck, new DocumentExportOptions
        {
            Format = ExportFormat.Pdf,
            CoverPage = false,      // deterministic page count — body pages only
            TableOfContents = false
        });

        var dispatch = await Mesh
            .Observe<ExportDocumentResponse>(request, o => o.WithTarget(new Address(deck)))
            .Should().Within(30.Seconds()).Emit();

        dispatch.Message.Error.Should().BeNullOrEmpty("the deck export should start successfully");
        dispatch.Message.ActivityPath.Should().NotBeNullOrEmpty(
            "the response should carry the running activity's path");

        // ── Wait for the script to reach terminal status on its own activity node stream. ──
        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var terminal = await workspace
            .GetMeshNodeStream(dispatch.Message.ActivityPath)
            .Select(node => node?.Content as ActivityLog)
            .Should().Within(2.Minutes())
            .Match(log => log is not null && log.Status != ActivityStatus.Running);

        terminal!.Status.Should().Be(ActivityStatus.Succeeded,
            because: "the deck should render to PDF without errors. Messages:\n  "
                     + string.Join("\n  ", terminal.Messages.Select(m => $"[{m.LogLevel}] {m.Message}")));

        terminal.ReturnValue.Should().NotBeNull("the script should record its rendered output");
        var rendered = terminal.ReturnValue!.Value.Deserialize<RenderedDocument>(Mesh.JsonSerializerOptions);
        rendered.Should().NotBeNull("ActivityLog.ReturnValue should deserialise to RenderedDocument");
        rendered!.Format.Should().Be(ExportFormat.Pdf);
        rendered.MimeType.Should().Be("application/pdf");
        rendered.FileName.Should().EndWith(".pdf");
        rendered.Content.Should().NotBeNull().And.NotBeEmpty();

        // ── Re-read the produced PDF and assert content faithfulness + layout. ──
        using var pdf = PdfDocument.Open(rendered.Content);
        var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        Output.WriteLine($"Deck PDF pages={pdf.NumberOfPages}\n{text}");

        // Every slide's unique token must appear — the deck exported ALL slides' content.
        text.Should().Contain("ALPHAWIDGET", "slide 1's paragraph text must be in the PDF");
        text.Should().Contain("NORTHREGION", "slide 2's TABLE cell must render into the PDF");
        text.Should().Contain("SOUTHREGION", "slide 2's second table row must render too");
        text.Should().Contain("GAMMAWIDGET", "slide 3's paragraph text must be in the PDF");

        // One page per slide (page break between slides).
        pdf.NumberOfPages.Should().BeGreaterThanOrEqualTo(3,
            "each of the three slides starts on its own page");

        // Landscape orientation (16:9): A4 landscape is wider than tall.
        var firstPage = pdf.GetPage(1);
        firstPage.Width.Should().BeGreaterThan(firstPage.Height,
            "the deck export defaults to landscape so 16:9 slides fill the page");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    private async Task CreateSlide(string path, string name, int order, string body)
        => await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = SlideNodeType.NodeType,
            Order = order,
            Content = new SlideContent { Content = body, Notes = $"Notes for {name}" }
        }).Should().Emit();
}
