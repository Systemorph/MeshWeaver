using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Pixel;

/// <summary>
/// <see cref="IPixelPdfRenderer"/> backed by an <b>already-installed</b> headless Chromium / Chrome
/// / Edge, driven as a plain <c>Process</c> — no NuGet package, no bundled browser download, and
/// therefore no cost at all for the deployments that never turn it on.
///
/// <para>Both the probe (a file-system stat) and the print (a subprocess) are I/O leaves and run
/// through <see cref="IIoPool"/>'s <see cref="IoPoolNames.Process"/> pool: off the hub scheduler and
/// bounded, so a burst of exports can never spawn an unbounded pile of browsers. The probe is
/// promise-cached in an instance dictionary — resolved once per mesh, replayed to every later
/// subscriber, and dead when the mesh is.</para>
/// </summary>
public sealed class HeadlessChromiumPdfRenderer(
    PixelRenderingOptions options,
    IoPoolRegistry ioPools,
    ILogger<HeadlessChromiumPdfRenderer>? logger = null) : IPixelPdfRenderer
{
    // Instance, never static: its lifetime is the mesh's, so a test mesh's probe cannot bleed
    // into the next one. Single logical key — the shape is the documented promise-cache.
    private readonly ConcurrentDictionary<string, IObservable<string?>> probe = new();

    private IIoPool Pool => ioPools.Get(IoPoolNames.Process);

    /// <inheritdoc />
    public IObservable<string?> Probe() =>
        probe.GetOrAdd(string.Empty, _ =>
        {
            // The promise-cache shape, with InvokeBlocking because the probe is sync-blocking
            // (File.Exists): the first subscriber runs it on the pool, the ReplaySubject replays
            // the answer to everyone after.
            var subject = new ReplaySubject<string?>(1);
            Pool.InvokeBlocking(_ => ResolveExecutable()).Subscribe(subject);
            return subject.AsObservable();
        });

    /// <inheritdoc />
    public IObservable<byte[]> Render(string html) =>
        Pool.InvokeBlocking(ct => Print(html, ct));

    /// <summary>
    /// Resolves the browser: explicit configuration first, then the conventional environment
    /// variables (so an image that already carries a browser needs no MeshWeaver config), then the
    /// platform's usual install locations. Null means "not available here".
    /// </summary>
    private string? ResolveExecutable()
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            if (File.Exists(options.ExecutablePath))
                return options.ExecutablePath;

            // Configured but wrong is an operator mistake worth surfacing — it is the difference
            // between "we chose not to enable this" and "we enabled it and it silently does nothing".
            logger?.LogWarning(
                "Pixel-faithful export is configured with ExecutablePath '{Path}', but no such file exists. "
                + "Pixel fidelity will not be offered.", options.ExecutablePath);
            return null;
        }

        foreach (var variable in PixelRenderingOptions.EnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                return value;
        }

        foreach (var candidate in PixelRenderingOptions.WellKnownPaths)
            if (File.Exists(candidate))
                return candidate;

        return null;
    }

    private byte[] Print(string html, CancellationToken ct)
    {
        var executable = ResolveExecutable()
            ?? throw new PixelRendererUnavailableException(
                "Pixel-faithful export needs a headless Chromium on the server and none is configured. "
                + "Set MarkdownExport PixelRendering ExecutablePath (or the CHROME_BIN environment variable) "
                + "to a Chromium/Chrome/Edge executable, or export with content fidelity instead.");

        // Each print gets its own scratch dir: the HTML input, the PDF output, and a throwaway
        // browser profile. A shared profile would serialise concurrent prints (Chromium takes a
        // lock on it) and leak state between exports.
        var workDir = Directory.CreateTempSubdirectory("mw-pixel-pdf-").FullName;
        try
        {
            var htmlPath = Path.Combine(workDir, "deck.html");
            var pdfPath = Path.Combine(workDir, "deck.pdf");
            File.WriteAllText(htmlPath, html);

            var psi = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in BuildArguments(workDir, htmlPath, pdfPath))
                psi.ArgumentList.Add(argument);

            logger?.LogDebug("Pixel export: printing {Bytes} bytes of HTML with {Executable}",
                html.Length, executable);

            using var process = new Process { StartInfo = psi };
            process.Start();

            // 🚨 The completion signal is the FILE, not the process exit.
            //
            // `--print-to-pdf` contracts to write the PDF; it does NOT contract to exit afterwards,
            // and a real desktop Chrome reliably does not — it keeps running its background service
            // machinery (updater, GCM, sync) long after the print has finished. Measured on Chrome
            // 151: the PDF is complete within ~1s and the process is still alive 25s later, under
            // EVERY combination of --headless=new/old/plain and every --disable-*-service flag.
            // Waiting for exit would make every export hang until the timeout and then fail — with
            // a perfectly good PDF sitting on disk.
            //
            // So we wait for Chrome's own explicit announcement on stderr,
            //     "<N> bytes written to file <path>"
            // which is self-validating: it carries the byte count, and we accept the result only
            // when the file on disk is exactly that long. A containerised Chromium that DOES exit
            // cleanly ends the same loop at end-of-stream and takes the exit path. Either way we
            // then stop the browser — the print is done, and its teardown is not our business.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(options.Timeout);
            using var registration = deadline.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already gone */ }
            });

            // Chrome says little on stdout, but an unread pipe can still fill and block it; drain
            // it concurrently and observe the task at the end.
            var stdoutDrain = process.StandardOutput.ReadToEndAsync(CancellationToken.None);

            // Blocking line reads are exactly what InvokeBlocking's dedicated scheduler exists for.
            // The deadline registration above kills the browser, which closes this stream, which
            // ends the loop — so the read is bounded without any hand-woven signal.
            long announcedBytes = -1;
            var stderr = new StringBuilder();
            string? line;
            while ((line = process.StandardError.ReadLine()) is not null)
            {
                if (stderr.Length < StderrCaptureLimit)
                    stderr.AppendLine(line);
                if (TryReadPrintCompletion(line, pdfPath, out announcedBytes))
                    break;
            }

            var completed = announcedBytes >= 0;
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already gone */ }
            }

            _ = DrainOrEmpty(stdoutDrain);
            var diagnostics = Summarize(stderr.ToString());

            if (!completed)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);

                if (deadline.IsCancellationRequested)
                    throw new PixelRenderException(
                        $"The headless browser did not finish printing within {options.Timeout.TotalSeconds:N0}s "
                        + $"and was stopped. {diagnostics}");

                // Stream ended with no announcement — the browser exited by itself. That is the
                // well-behaved containerised case, and the file is then the proof of success.
                if (!File.Exists(pdfPath))
                    throw new PixelRenderException(
                        $"The headless browser produced no PDF. {diagnostics}");
            }

            if (!File.Exists(pdfPath))
                throw new PixelRenderException($"The headless browser produced no PDF. {diagnostics}");

            var bytes = File.ReadAllBytes(pdfPath);
            if (bytes.Length == 0)
                throw new PixelRenderException("The headless browser produced an empty PDF.");

            // The announcement carries the byte count, so a partial read cannot slip through.
            if (completed && bytes.Length != announcedBytes)
                throw new PixelRenderException(
                    $"The headless browser announced a {announcedBytes}-byte PDF but wrote {bytes.Length} bytes.");

            logger?.LogDebug("Pixel export: produced {Bytes} PDF bytes", bytes.Length);
            return bytes;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException ex) { logger?.LogDebug(ex, "Could not clean up {WorkDir}", workDir); }
            catch (UnauthorizedAccessException ex) { logger?.LogDebug(ex, "Could not clean up {WorkDir}", workDir); }
        }
    }

    private IEnumerable<string> BuildArguments(string workDir, string htmlPath, string pdfPath)
    {
        yield return "--headless=new";
        // Deterministic, server-safe rendering.
        yield return "--disable-gpu";
        yield return "--hide-scrollbars";
        yield return "--disable-extensions";
        yield return "--disable-background-networking";
        yield return "--disable-sync";
        yield return "--no-first-run";
        yield return "--no-default-browser-check";
        // /dev/shm is tiny in most containers; without this Chromium crashes on larger decks.
        yield return "--disable-dev-shm-usage";
        // Own profile per print — see the scratch-dir note above.
        yield return $"--user-data-dir={Path.Combine(workDir, "profile")}";
        yield return $"--crash-dumps-dir={Path.Combine(workDir, "crash")}";
        // Let fonts, images and layout settle, then draw every compositor stage before printing —
        // otherwise a web font or a decoded image can miss the capture.
        yield return "--run-all-compositor-stages-before-draw";
        yield return "--virtual-time-budget="
            + options.SettleBudget.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
        // No browser-added header/footer: the slide IS the page, edge to edge.
        yield return "--no-pdf-header-footer";
        yield return $"--print-to-pdf={pdfPath}";

        if (options.NoSandbox)
            yield return "--no-sandbox";

        foreach (var extra in options.AdditionalArguments)
            yield return extra;

        yield return new Uri(htmlPath).AbsoluteUri;
    }

    /// <summary>
    /// Recognises Chromium's print-completion announcement — <c>"&lt;N&gt; bytes written to file
    /// &lt;path&gt;"</c> — for OUR output file, and yields the byte count it reports.
    ///
    /// <para>Matching is deliberately narrow on three independent things at once: the phrase, our
    /// exact output path, and a parsable leading count. A line that satisfies all three is the
    /// browser telling us the print is finished, and the count then lets the caller verify the file
    /// rather than trust the message.</para>
    /// </summary>
    public static bool TryReadPrintCompletion(string line, string pdfPath, out long bytes)
    {
        bytes = -1;
        if (!line.Contains(PrintCompletionPhrase, StringComparison.Ordinal))
            return false;
        if (!line.Contains(pdfPath, StringComparison.Ordinal))
            return false;

        var digits = line.AsSpan().TrimStart();
        var end = 0;
        while (end < digits.Length && char.IsAsciiDigit(digits[end]))
            end++;

        return end > 0 && long.TryParse(digits[..end], out bytes);
    }

    private const string PrintCompletionPhrase = "bytes written to file";

    /// <summary>How much stderr to keep for diagnostics — Chromium can be extremely chatty.</summary>
    private const int StderrCaptureLimit = 8192;

    /// <summary>
    /// Observes a pipe-drain task. Cancellation (we killed the browser) and a broken pipe are
    /// expected shutdown noise, not the failure we report — everything else propagates.
    /// </summary>
    private static string DrainOrEmpty(Task<string> drain)
    {
        try
        {
            return drain.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string Summarize(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return string.Empty;
        var trimmed = stderr.Trim();
        return trimmed.Length <= 600 ? trimmed : trimmed[..600] + "…";
    }
}

/// <summary>
/// Thrown when pixel-faithful rendering was requested on a deployment that has no headless browser.
/// Distinct from <see cref="PixelRenderException"/> so callers can tell "not available here" from
/// "the browser tried and failed".
/// </summary>
public class PixelRendererUnavailableException(string message) : InvalidOperationException(message);

/// <summary>Thrown when the headless browser ran but did not produce a usable PDF.</summary>
public class PixelRenderException(string message) : InvalidOperationException(message);
