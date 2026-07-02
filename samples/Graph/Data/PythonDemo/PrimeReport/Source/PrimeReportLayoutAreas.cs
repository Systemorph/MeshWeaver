// <meshweaver>
// Id: PrimeReportLayoutAreas
// DisplayName: Prime Report Layout Areas
// </meshweaver>

using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Views for the PythonDemo/PrimeReport sample: a layout area that computes its content
/// by shelling out to <c>python3</c>. The external process is a sync-blocking I/O leaf,
/// so it runs through the bounded Process <see cref="IIoPool"/> (never on a hub thread),
/// and the area emits reactively — no async/await anywhere hub-reachable.
/// </summary>
public static class PrimeReportLayoutAreas
{
    public static LayoutDefinition AddPrimeReportLayoutAreas(this LayoutDefinition layout) =>
        layout.WithView("Report", Report);

    /// <summary>
    /// Renders the prime table computed by Python. Reads the node reactively from the
    /// per-node hub's MeshDataSource (<c>host.Workspace.GetStream&lt;MeshNode&gt;()</c> —
    /// the same read the framework's default node areas use), reruns the script whenever
    /// <see cref="PrimeReport.Count"/> changes, and degrades to an informative note when
    /// <c>python3</c> is not installed on the host.
    /// </summary>
    public static IObservable<UiControl?> Report(LayoutAreaHost host, RenderingContext _)
    {
        var hub = host.Hub;
        var hubPath = hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>();
        if (nodeStream is null)
            return Observable.Return(
                (UiControl?)Controls.Markdown("*Unable to load the prime report node.*"));

        return nodeStream
            .Select(nodes => nodes?.FirstOrDefault(n => n.Path == hubPath))
            .Select(node => Math.Clamp(ExtractReport(node)?.Count ?? 25, 1, 200))
            .DistinctUntilChanged()
            .Select(count => ProcessPool(hub)
                // InvokeBlocking = sync-blocking leaf on the pool's limited-concurrency
                // scheduler. The Process never starts on the hub's action block.
                .InvokeBlocking(ct => RunPython(BuildScript(count), ct))
                .Select(markdown => (UiControl?)Controls.Markdown(markdown)))
            .Switch()
            // Render immediately; the Python result replaces the placeholder when it lands.
            .StartWith((UiControl?)Controls.Markdown("*Running Python…*"));
    }

    private static IIoPool ProcessPool(IMessageHub hub) =>
        hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Process)
        ?? IoPool.Unbounded;

    /// <summary>
    /// Extracts the typed content from the MeshNode, handling both the typed record and
    /// the raw JsonElement shape (content arrives as JSON before the type is bound).
    /// </summary>
    private static PrimeReport? ExtractReport(MeshNode? node)
    {
        if (node?.Content is PrimeReport report)
            return report;

        if (node?.Content is JsonElement json)
        {
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<PrimeReport>(json.GetRawText(), options);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The Python program: computes the first <paramref name="count"/> primes and formats
    /// the whole report as markdown itself — MeshWeaver only displays what Python prints.
    /// </summary>
    private static string BuildScript(int count) => $$"""
        import sys

        n = {{count}}
        primes = []
        candidate = 2
        while len(primes) < n:
            if all(candidate % p for p in primes):
                primes.append(candidate)
            candidate += 1

        gaps = [b - a for a, b in zip(primes, primes[1:])]
        print(f"### First {n} primes — computed by Python {sys.version.split()[0]}")
        print()
        print("| # | Prime |")
        print("|---|---|")
        for i, p in enumerate(primes):
            print(f"| {i + 1} | {p} |")
        print()
        print(f"- **Sum:** {sum(primes)}")
        print(f"- **Mean:** {sum(primes) / n:.2f}")
        if gaps:
            print(f"- **Largest gap between consecutive primes:** {max(gaps)}")
        """;

    /// <summary>
    /// Runs <c>python3 -c &lt;script&gt;</c> and returns its stdout (markdown). Called ONLY from
    /// inside <see cref="IIoPool.InvokeBlocking{T}"/> — this method blocks by design and must
    /// never run on a hub thread. When no Python interpreter is on PATH it returns a notice
    /// instead of throwing, so the area renders meaningfully on hosts without Python
    /// (production containers ship none).
    /// </summary>
    private static string RunPython(string script, CancellationToken ct)
    {
        var python = FindPython();
        if (python is null)
            return """
                > **Python is not available on this host.**
                >
                > This layout area shells out to `python3`, which is not installed in the
                > portal's container image. Run the sample on a host with Python 3 on the
                > PATH to see the live report — the area degrades to this notice instead
                > of erroring.
                """;

        var psi = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = psi };
        process.Start();
        // Cancellation kills the process tree so a pool slot is never leaked on unsubscribe.
        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        });
        // Drain both streams concurrently BEFORE WaitForExit so a full buffer can't deadlock.
        var outTask = process.StandardOutput.ReadToEndAsync(ct);
        var errTask = process.StandardError.ReadToEndAsync(ct);
        process.WaitForExit();
        var stdout = outTask.GetAwaiter().GetResult();
        var stderr = errTask.GetAwaiter().GetResult();

        return process.ExitCode == 0
            ? stdout
            : $"> **Python exited with code {process.ExitCode}.**\n>\n> {stderr.ReplaceLineEndings("\n> ")}";
    }

    private static string? FindPython()
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "python3.exe", "python.exe" }
            : new[] { "python3" };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(name => Path.Combine(dir, name)))
            .FirstOrDefault(File.Exists);
    }
}
