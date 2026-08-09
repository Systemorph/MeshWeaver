using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh.Threading;
using UglyToad.PdfPig;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

/// <summary>
/// Drives the real browser leaf — when the machine has one.
///
/// <para><b>This test is never skipped.</b> Pixel fidelity is a capability a deployment either has
/// or hasn't, and BOTH worlds are a contract worth pinning: with a browser the export must produce
/// a real PDF with one page per slide; without one it must fail loudly rather than hand back a
/// content-faithful file pretending to be pixel-faithful. So the test asserts whichever contract
/// applies to the machine it runs on, and a missing browser can never turn into a silent gap in
/// coverage or a red build.</para>
/// </summary>
public class HeadlessChromiumRenderTests : IDisposable
{
    private readonly IoPoolRegistry pools = new();

    [Fact]
    public async Task A_gradient_deck_prints_to_a_real_pdf_or_fails_loudly()
    {
        var renderer = new HeadlessChromiumPdfRenderer(
            // Containers run as a non-root user without SYS_ADMIN; CI runners generally do not need
            // this, but asking for it here keeps the test working on both.
            new PixelRenderingOptions { NoSandbox = OperatingSystem.IsLinux() },
            pools);

        var executable = await renderer.Probe().FirstAsync().ToTask();

        var html = SlidePrintComposer.Compose("Gradient deck",
        [
            new PrintSlide(new SlideContent
            {
                Background = "linear-gradient(135deg, #667eea 0%, #764ba2 100%)",
                Content = "# ALPHAWIDGET"
            }),
            new PrintSlide(new SlideContent
            {
                Background = "#0b1d3a",
                Content = "<div style=\"transform: rotate(-3deg);\"><h1>BETAWIDGET</h1></div>"
            }),
        ]);

        if (executable is null)
        {
            var render = async () => await renderer.Render(html).FirstAsync().ToTask();
            (await render.Should().ThrowAsync<PixelRendererUnavailableException>())
                .WithMessage("*headless Chromium*");
            return;
        }

        var pdf = await renderer.Render(html).FirstAsync().ToTask();

        pdf.Should().NotBeEmpty();
        pdf.Take(4).Should().Equal((byte)'%', (byte)'P', (byte)'D', (byte)'F');

        using var document = PdfDocument.Open(pdf);
        document.NumberOfPages.Should().Be(2, "the page box is exactly one 16:9 slide, so two slides are two pages");

        var text = string.Join("\n", document.GetPages().Select(p => p.Text));
        text.Should().Contain("ALPHAWIDGET", "the markdown slide's heading must reach the page");
        // The raw-HTML slide is the whole point: the document-model renderer drops it entirely.
        text.Should().Contain("BETAWIDGET", "a slide authored as raw HTML must render, not vanish");

        var first = document.GetPage(1);
        first.Width.Should().BeGreaterThan(first.Height, "16:9 slides print landscape");
    }

    public void Dispose() => pools.Dispose();
}
