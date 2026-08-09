using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

/// <summary>
/// The "no browser here" contract. Pixel fidelity is a capability a deployment may or may not have,
/// and the whole design rests on that being detectable — the dialog only offers the choice when the
/// probe finds a browser, and the export refuses loudly rather than quietly downgrading when it
/// doesn't. Both halves are pinned here, and neither needs a browser to test.
/// </summary>
public class PixelRendererAvailabilityTests : IDisposable
{
    private readonly IoPoolRegistry pools = new();

    [Fact]
    public async Task A_configured_executable_that_does_not_exist_reports_unavailable()
    {
        var renderer = new HeadlessChromiumPdfRenderer(
            new PixelRenderingOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chromium") },
            pools);

        var executable = await renderer.Probe().FirstAsync().ToTask();

        // Explicit configuration is never silently ignored in favour of auto-detection: an operator
        // who pointed at the wrong path must see "unavailable", not a different browser.
        executable.Should().BeNull();
    }

    [Fact]
    public async Task Rendering_without_a_browser_fails_with_an_actionable_message()
    {
        var renderer = new HeadlessChromiumPdfRenderer(
            new PixelRenderingOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chromium") },
            pools);

        var render = async () => await renderer.Render("<html></html>").FirstAsync().ToTask();

        // Loud, not silent: a user who asked for pixel fidelity must never receive a
        // content-faithful file believing it is pixel-faithful.
        (await render.Should().ThrowAsync<PixelRendererUnavailableException>())
            .WithMessage("*headless Chromium*");
    }

    [Fact]
    public async Task The_probe_is_promise_cached_so_the_file_system_is_hit_once()
    {
        var renderer = new HeadlessChromiumPdfRenderer(new PixelRenderingOptions(), pools);

        var first = await renderer.Probe().FirstAsync().ToTask();
        var second = await renderer.Probe().FirstAsync().ToTask();

        // Same cached answer replayed — and, crucially, the cache is an INSTANCE field, so a second
        // renderer (a second mesh) probes independently rather than inheriting this one's answer.
        second.Should().Be(first);
        (await new HeadlessChromiumPdfRenderer(
                new PixelRenderingOptions { ExecutablePath = "/definitely/not/here" }, pools)
            .Probe().FirstAsync().ToTask())
            .Should().BeNull();
    }

    // ── The print-completion signal ──────────────────────────────────────────────────────────
    // Chrome does not exit after --print-to-pdf (measured: still alive 25s after the PDF is
    // complete, under every headless mode and every --disable-*-service flag). Its stderr
    // announcement is therefore the completion signal the renderer waits on, which makes this
    // parser load-bearing: too loose and a truncated PDF ships, too tight and every export hangs
    // until the timeout.

    [Fact]
    public void The_completion_announcement_is_recognised_with_its_byte_count()
    {
        HeadlessChromiumPdfRenderer
            .TryReadPrintCompletion("22910 bytes written to file /tmp/x/deck.pdf", "/tmp/x/deck.pdf", out var bytes)
            .Should().BeTrue();

        bytes.Should().Be(22910, "the count is what lets the caller verify the file rather than trust the message");
    }

    [Theory]
    // Another file's announcement — a stale line must never complete OUR print.
    [InlineData("22910 bytes written to file /tmp/other/deck.pdf")]
    // Ordinary Chromium noise that happens to name our file.
    [InlineData("[1:2:0809/140439.484408:ERROR:something.cc:291] /tmp/x/deck.pdf failed")]
    // The phrase and the path, but no count to verify against.
    [InlineData("bytes written to file /tmp/x/deck.pdf")]
    [InlineData("")]
    public void Anything_else_is_not_a_completion(string line)
        => HeadlessChromiumPdfRenderer.TryReadPrintCompletion(line, "/tmp/x/deck.pdf", out _)
            .Should().BeFalse();

    public void Dispose() => pools.Dispose();
}
