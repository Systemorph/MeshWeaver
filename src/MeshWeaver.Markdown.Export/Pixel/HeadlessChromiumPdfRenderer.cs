using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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

            // Unsubscribe (or mesh teardown) kills the whole browser tree — a pool slot is never
            // held by an orphaned renderer child.
            using var registration = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            });

            // Drain both pipes before waiting: Chromium is chatty on stderr and a full pipe buffer
            // would deadlock the wait.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            var timedOut = !process.WaitForExit(ClampToInt(options.Timeout));
            if (timedOut)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            }

            // Observe BOTH drains on every path — a read that was cancelled by the kill above must
            // not surface later as an unobserved task exception. Their failure is never the error
            // we report; the exit code / missing output below is.
            var stderr = DrainOrEmpty(stderrTask);
            _ = DrainOrEmpty(stdoutTask);

            if (timedOut)
                throw new PixelRenderException(
                    $"The headless browser did not finish printing within {options.Timeout.TotalSeconds:N0}s and was stopped. "
                    + Summarize(stderr));

            if (process.ExitCode != 0)
                throw new PixelRenderException(
                    $"The headless browser exited with code {process.ExitCode}. {Summarize(stderr)}");

            if (!File.Exists(pdfPath))
                throw new PixelRenderException(
                    $"The headless browser produced no PDF. {Summarize(stderr)}");

            var bytes = File.ReadAllBytes(pdfPath);
            if (bytes.Length == 0)
                throw new PixelRenderException("The headless browser produced an empty PDF.");

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
    /// Milliseconds for <see cref="Process.WaitForExit(int)"/>, clamped so an absurdly large
    /// configured timeout cannot overflow into a negative (= immediate) wait.
    /// </summary>
    private static int ClampToInt(TimeSpan timeout) =>
        timeout.TotalMilliseconds is var ms && ms >= int.MaxValue ? int.MaxValue : (int)Math.Max(ms, 0);

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
