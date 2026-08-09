using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh.Threading;
using UglyToad.PdfPig;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

/// <summary>
/// Proves the pixel export's two isolation layers actually bite, one at a time.
///
/// <para><b>Why this matters more here than in a normal page.</b> The headless browser runs inside
/// the SERVER's trust boundary: it can reach internal services, private subnets and cloud metadata
/// endpoints the deck's author could never reach from their own browser, and it can read local
/// files over <c>file://</c>. Slide bodies are user-authored and pass raw HTML through verbatim, so
/// "untrusted input rendered by a privileged browser" is exactly the shape — the classic
/// server-side-rendering SSRF/exfiltration hazard.</para>
///
/// <para><b>Two layers, tested separately, each with a live control.</b> Asserting "no request
/// arrived" proves nothing on its own — it passes just as happily when the browser never had a
/// route, the listener was misconfigured, or the markup was wrong. Worse, testing both layers at
/// once hides a hole in either: the first draft of this test asserted the CSP while the renderer's
/// own network-denial flags were doing the blocking, so it would have passed with NO CSP at all.
/// So each test neutralises the OTHER layer, leaving one variable, and demands that the control
/// leaks before believing the protected run.</para>
///
/// <para>Never skipped: with no browser on the machine the isolation contract cannot be exercised,
/// so the test asserts the other half of the contract — that the export refuses rather than
/// silently downgrading — exactly as the sibling browser tests do.</para>
/// </summary>
public class PixelRenderIsolationTests : IDisposable
{
    private readonly IoPoolRegistry pools = new();
    private readonly string scratch =
        Directory.CreateTempSubdirectory("mw-pixel-isolation-").FullName;

    /// <summary>The renderer exactly as production configures it.</summary>
    private HeadlessChromiumPdfRenderer Production() => new(
        new PixelRenderingOptions { NoSandbox = OperatingSystem.IsLinux() },
        pools);

    /// <summary>
    /// The renderer with its process-level network denial neutralised, so the CSP is the only
    /// thing left that could block a request. Chromium takes the LAST occurrence of a switch and
    /// <see cref="PixelRenderingOptions.AdditionalArguments"/> are appended after the defaults,
    /// which is what makes this override possible — and is itself worth pinning, since it is the
    /// escape hatch an operator would reach for.
    /// </summary>
    private HeadlessChromiumPdfRenderer WithoutNetworkDenial(params string[] extra) => new(
        new PixelRenderingOptions
        {
            NoSandbox = OperatingSystem.IsLinux(),
            AdditionalArguments =
                ImmutableList.Create("--proxy-server=direct://", "--host-resolver-rules=").AddRange(extra),
        },
        pools);

    /// <summary>
    /// Removes the policy to produce the negative control. Throws if the meta tag is ever renamed,
    /// rather than silently producing an identical document — which would make the control agree
    /// with the protected run and quietly turn these tests green-and-meaningless.
    /// </summary>
    private static string WithoutPolicy(string html)
    {
        var start = html.IndexOf("<meta http-equiv=\"Content-Security-Policy\"", StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException(
                "The composed document carries no CSP meta tag — the control cannot be built, and "
                + "the isolation assertions would be vacuous.");
        var end = html.IndexOf("/>", start, StringComparison.Ordinal) + 2;
        return html.Remove(start, end - start);
    }

    private static string NetworkSlideHtml(int port) =>
        SlidePrintComposer.Compose("Exfiltration",
        [
            new PrintSlide(new SlideContent
            {
                Content = $"""
                    <img src="http://127.0.0.1:{port}/leak.png" alt="x" />
                    <p>VISIBLEMARKER</p>
                    """,
                // A CSS url() is the second half of the same vector — background images go through
                // the same loader and are easy to overlook.
                Background = $"#101820 url(http://127.0.0.1:{port}/bg.png)"
            })
        ]);

    [Fact]
    public async Task The_policy_alone_stops_a_slide_from_opening_a_connection()
    {
        // Process-level denial off: whatever blocks the request here can only be the CSP.
        var renderer = WithoutNetworkDenial();
        var executable = await renderer.Probe().FirstAsync().ToTask();

        using var probe = new LoopbackProbe();
        var html = NetworkSlideHtml(probe.Port);

        if (executable is null)
        {
            var render = async () => await renderer.Render(html).FirstAsync().ToTask();
            await render.Should().ThrowAsync<PixelRendererUnavailableException>();
            return;
        }

        // ── Control: without the policy the slide MUST reach the listener. ──
        probe.Reset();
        await renderer.Render(WithoutPolicy(html)).FirstAsync().ToTask();
        var leaked = probe.Connections;
        leaked.Should().BeGreaterThan(0,
            "the control must demonstrate the vector is real — a slide CAN otherwise make the "
            + "server's browser open a connection of its choosing. At 0 the assertion below would "
            + "prove nothing, which is exactly how the first draft of this test fooled itself.");

        // ── Protected: the composed document must reach nothing. ──
        probe.Reset();
        var pdf = await renderer.Render(html).FirstAsync().ToTask();
        probe.Connections.Should().Be(0,
            "the Content-Security-Policy must stop a slide from making the server fetch a URL — "
            + $"this is the SSRF surface (the control opened {leaked} connection(s))");

        // …and the slide still prints. A policy that also broke rendering would be no fix at all.
        TextOf(pdf).Should().Contain("VISIBLEMARKER");
    }

    [Fact]
    public async Task The_process_flags_stop_it_too_even_with_no_policy_at_all()
    {
        // Defence in depth: strip the CSP entirely and show the browser still cannot reach out.
        // This is the layer that survives a future markup change dropping the meta tag.
        var renderer = Production();
        var executable = await renderer.Probe().FirstAsync().ToTask();

        using var probe = new LoopbackProbe();
        var html = WithoutPolicy(NetworkSlideHtml(probe.Port));

        if (executable is null)
        {
            var render = async () => await renderer.Render(html).FirstAsync().ToTask();
            await render.Should().ThrowAsync<PixelRendererUnavailableException>();
            return;
        }

        var pdf = await renderer.Render(html).FirstAsync().ToTask();

        probe.Connections.Should().Be(0,
            "--host-resolver-rules and the dead --proxy-server must deny the network at the process "
            + "level, so a slide cannot reach out even if the document's policy is ever lost");
        TextOf(pdf).Should().Contain("VISIBLEMARKER");
    }

    [Fact]
    public async Task The_policy_stops_a_slide_pulling_a_local_file_into_the_pdf()
    {
        // Stands in for anything readable by the portal process — a config file, a mounted secret.
        var secret = Path.Combine(scratch, "secret.html");
        await File.WriteAllTextAsync(secret,
            // Modest type on purpose: at display size the heading overran the frame and PDF text
            // extraction returned a CLIPPED marker, which would have made the control look like a
            // non-leak. The leak is what the glyphs say, not how big they are.
            "<html><body><p style=\"font-size:28px\">SECRETLEAK</p></body></html>");

        // Chromium ALSO refuses most file:// subresource loads on its own. That is a second lock on
        // the same door, not a reason to skip the test: --allow-file-access-from-files unlocks it,
        // so the control can show the vector is genuinely open and the protected run can show the
        // policy holds even when the browser's own guard is off — i.e. the CSP would still protect
        // a deployment that ever added that flag.
        var renderer = WithoutNetworkDenial("--allow-file-access-from-files");
        var executable = await renderer.Probe().FirstAsync().ToTask();

        var html = SlidePrintComposer.Compose("Local file",
        [
            new PrintSlide(new SlideContent
            {
                Content = $"""
                    <iframe src="{new Uri(secret).AbsoluteUri}" style="width:900px;height:400px;"></iframe>
                    <p>VISIBLEMARKER</p>
                    """
            })
        ]);

        if (executable is null)
        {
            var render = async () => await renderer.Render(html).FirstAsync().ToTask();
            await render.Should().ThrowAsync<PixelRendererUnavailableException>();
            return;
        }

        // Text extraction is the crisp signal: a loaded frame puts its heading in the PDF as real
        // text; a blocked one leaves nothing to extract.
        var controlText = TextOf(await renderer.Render(WithoutPolicy(html)).FirstAsync().ToTask());
        controlText.Should().Contain("SECRETLEAK",
            "the control must demonstrate the vector is real — with file access permitted and no "
            + "policy, a slide CAN otherwise pull a local file into the exported PDF");

        var protectedText = TextOf(await renderer.Render(html).FirstAsync().ToTask());
        protectedText.Should().NotContain("SECRETLEAK",
            "the policy must stop a slide from pulling a local file into the exported PDF");
        protectedText.Should().Contain("VISIBLEMARKER",
            "the slide itself must still print — a policy that broke rendering would be no fix");
    }

    private static string TextOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(p => p.Text));
    }

    public void Dispose()
    {
        pools.Dispose();
        try { Directory.Delete(scratch, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    /// <summary>
    /// A loopback socket that counts inbound connections. Any fetch of its URL lands here, so the
    /// count is direct evidence of an attempted request — no HTTP server, no response contract.
    /// </summary>
    private sealed class LoopbackProbe : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cancellation = new();
        private int connections;

        public LoopbackProbe()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptLoop();
        }

        public int Port { get; }

        public int Connections => Volatile.Read(ref connections);

        public void Reset() => Volatile.Write(ref connections, 0);

        private async Task AcceptLoop()
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    Interlocked.Increment(ref connections);
                    // Close at once: the connection attempt is the evidence, and leaving the
                    // browser waiting would only slow the print.
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }
            }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            listener.Stop();
            cancellation.Dispose();
        }
    }
}
