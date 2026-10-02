// <meshweaver>
// Id: PrimeReportLayoutAreas
// DisplayName: Prime Report Layout Areas
// </meshweaver>

using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    /// <summary>The <c>/data</c> id the report feed writes to.</summary>
    public const string ReportDataId = "primeReport";

    /// <summary>
    /// The prime table computed by Python — a TEMPLATE (Doc/GUI/DataBinding → "Templates first,
    /// data later"): the markdown control is on screen at the first render, bound to
    /// <c>/data/primeReport</c>, and <see cref="ReportFeed"/> fills it once Python has answered.
    /// </summary>
    /// <param name="host">The area host; the feed reads its node.</param>
    /// <param name="_">The rendering context.</param>
    /// <returns>The report template.</returns>
    public static UiControl Report(LayoutAreaHost host, RenderingContext _)
        => ReportTemplate(ReportFeed(host));

    /// <summary>The report page: one markdown control, bound to what <paramref name="report"/> writes.</summary>
    /// <param name="report">The rendered report, as it changes.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl ReportTemplate(IObservable<string> report) =>
        report.Bind(markdown => Controls.Markdown(markdown), ReportDataId);

    /// <summary>
    /// The FEED half: reads the node reactively from the per-node hub's own stream, reruns the
    /// script whenever <see cref="PrimeReport.Count"/> changes, and degrades to an informative note
    /// when <c>python3</c> is not installed on the host. Builds no control. "Running Python…" is
    /// written first, so the slot says what it is waiting for; a failure is logged and shown —
    /// reported, never swallowed.
    /// </summary>
    /// <param name="host">The area host whose node is read.</param>
    /// <returns>The report markdown.</returns>
    public static IObservable<string> ReportFeed(LayoutAreaHost host)
    {
        var hub = host.Hub;
        return host.Workspace.GetMeshNodeStream()
            .Select(node => Math.Clamp(ExtractReport(hub, node)?.Count ?? 25, 1, 200))
            .DistinctUntilChanged()
            .Select(count => ProcessPool(hub)
                // InvokeBlocking = sync-blocking leaf on the pool's limited-concurrency
                // scheduler. The Process never starts on the hub's action block.
                .InvokeBlocking(ct => RunPython(BuildScript(count), ct)))
            .Switch()
            .StartWith("*Running Python…*")
            .Catch<string, Exception>(ex =>
            {
                hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(nameof(PrimeReportLayoutAreas))
                    .LogWarning(ex, "The prime report of {Path} could not be computed", hub.Address);
                return Observable.Return($"> **The report could not be computed:** {ex.Message}");
            });
    }

    private static IIoPool ProcessPool(IMessageHub hub) =>
        hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Process)
        ?? IoPool.Unbounded;

    /// <summary>
    /// The node's typed content. <c>ContentAs</c> — never <c>is</c> + a hand-rolled JsonElement
    /// branch: the accessor covers the already-typed value AND the degraded JsonElement/JsonNode
    /// (content arrives as JSON before the type is bound, and STAYS that way when it carries no
    /// resolvable <c>$type</c>) AND a same-short-named <c>PrimeReport</c> from another build — every
    /// recompile of this NodeType mints a new collectible assembly, so "the same" record has a
    /// different CLR identity per build, which the hand-rolled version had no round-trip to recover.
    /// It also reads with the HUB's options rather than a locally-invented
    /// <c>JsonSerializerOptions</c>, and logs instead of swallowing in a bare <c>catch</c>.
    /// </summary>
    private static PrimeReport? ExtractReport(IMessageHub hub, MeshNode? node) =>
        node.ContentAs<PrimeReport>(hub.JsonSerializerOptions);

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

        // Both pipes drain CONCURRENTLY, and without a Task bridge. The event-based reads are
        // pumped by the runtime while this thread sits in WaitForExit(), so neither pipe can fill
        // and block the child — the deadlock the previous ReadToEndAsync pair was guarding
        // against — and there is no observable-or-Task-to-blocking hop to bridge back.
        //
        // 🚨 The handlers must be attached BEFORE Start() and the Begin*ReadLine() calls made
        // after it; that ordering is the whole contract. WaitForExit() with no argument is also
        // deliberate: the parameterless overload is the one that waits for the asynchronous
        // readers to flush, so the builders below are complete when it returns. WaitForExit(ms)
        // does NOT make that guarantee, and swapping it in would silently truncate output.
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        // Cancellation kills the process tree so a pool slot is never leaked on unsubscribe.
        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        });
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        // 🚨 Cancellation must terminate the sequence, not render. The registration above only
        // KILLS the child; without this the kill's non-zero exit would fall through to the error
        // branch below and the area would publish "Python exited with code 137" as though the
        // script had failed. ReadToEndAsync(ct) used to throw here for free — the event-based
        // drain has no token, so the check is explicit and belongs AFTER WaitForExit, once the
        // readers have flushed and the process is genuinely done.
        ct.ThrowIfCancellationRequested();

        return process.ExitCode == 0
            ? stdout.ToString()
            : $"> **Python exited with code {process.ExitCode}.**\n>\n> {stderr.ToString().ReplaceLineEndings("\n> ")}";
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
