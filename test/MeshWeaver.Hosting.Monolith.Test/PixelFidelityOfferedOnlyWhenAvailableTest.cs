#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Layout;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The export dialog must OFFER pixel fidelity only where it can be honoured — issue #990. The
/// capability is resolved server-side (a Deck, and a browser this deployment can actually resolve)
/// and travels on <see cref="ExportDocumentControl.PixelFidelityAvailable"/>, so a portal without a
/// browser renders no fidelity picker at all rather than a choice that would fail.
///
/// <para>This pins the wiring between the renderer's probe and the control, which no other test
/// covers: the layout area resolves <see cref="IPixelPdfRenderer"/> from the per-node hub's
/// provider, and the flag arrives on a LATER emission than the seed control because the probe is
/// asynchronous.</para>
/// </summary>
public class PixelFidelityOfferedOnlyWhenAvailableTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMarkdownExport();

    [Fact(Timeout = 120000)]
    public async Task DeckExportDialog_OffersPixelFidelity_ExactlyWhenABrowserResolves()
    {
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Pixel Offer Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();

        var deck = $"{space}/pitch";
        await NodeFactory.CreateNode(MeshNode.FromPath(deck) with
        {
            Name = "Pitch",
            NodeType = DeckNodeType.NodeType,
            Content = new DeckContent { Title = "Pitch", Slides = [] }
        }).Should().Emit();

        // The expectation comes from the SAME probe the layout area consults, so the test can
        // never disagree with what the deployment actually has.
        var expected = await Mesh.ServiceProvider.GetRequiredService<IPixelPdfRenderer>()
            .Probe().FirstAsync().ToTask() is not null;
        Output.WriteLine($"Browser available: {expected}");

        var workspace = GetClient(client => client.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference(ExportDocumentLayoutArea.PdfArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(deck), reference);

        // The seed control carries no capability; the probe's answer arrives on a later emission,
        // so wait for the control whose flag matches — never .Take(1) on the first thing seen.
        var control = await stream.GetControlStream(reference.Area!)
            .Should().Within(60.Seconds())
            .Match(c => c is ExportDocumentControl e && e.PixelFidelityAvailable == expected);

        ((ExportDocumentControl)control!).PixelFidelityAvailable.Should().Be(expected,
            expected
                ? "a Deck on a deployment WITH a browser must offer the pixel choice"
                : "a deployment without a browser must not offer a choice that would fail");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    [Fact(Timeout = 120000)]
    public async Task MarkdownExportDialog_NeverOffersPixelFidelity()
    {
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Pixel Offer Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();

        var page = $"{space}/notes";
        await NodeFactory.CreateNode(MeshNode.FromPath(page) with
        {
            Name = "Notes",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# Notes\n\nSome prose." }
        }).Should().Emit();

        var workspace = GetClient(client => client.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference(ExportDocumentLayoutArea.PdfArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(page), reference);

        // Wait for the ENRICHED control (NodeName set) so this cannot pass merely by catching the
        // seed emission before any capability could have been added.
        var control = await stream.GetControlStream(reference.Area!)
            .Should().Within(60.Seconds())
            .Match(c => c is ExportDocumentControl e && e.NodeName == "Notes");

        ((ExportDocumentControl)control!).PixelFidelityAvailable.Should().BeFalse(
            "pixel fidelity applies to Deck → PDF only, however capable the deployment is");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }
}
