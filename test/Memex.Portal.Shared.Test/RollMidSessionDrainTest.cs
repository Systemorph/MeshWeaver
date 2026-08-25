using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.ServiceDefaults;
using MeshWeaver.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>THE ROLL-MID-SESSION ACCEPTANCE</b> — issue #1971 (split out of #1340), the one item that
/// nothing else covers: <i>roll the mesh while a session is open and assert the session survives.</i>
///
/// <para><b>Why this is not the guard we already have.</b>
/// <c>MeshWeaver.Documentation.Test.DrainDeadlineGuard</c> pins the preStop <b>arithmetic</b> — that
/// the loop's deadline is <c>drainSeconds − shutdownMarginSeconds</c>. That guard would still pass
/// if the drain were pinned by something else entirely: a probe tool missing from the image, a
/// counter that never decrements, an endpoint that answers 500. The chart carries a scar of exactly
/// that class — preStop once probed with <c>wget</c>, which is absent from the image, so the loop
/// could never succeed and EVERY termination hung to the grace ceiling, strictly worse than the
/// abrupt teardown it exists to prevent. Reading caught it; no test could. This is that test.</para>
///
/// <para><b>What "the session survives a roll" actually means.</b> A rollout is surge-first
/// (<c>maxUnavailable: 0</c>): the new pod serves before the old one is removed, so new sessions go
/// elsewhere the moment this pod leaves the Service. The OLD pod's job is then to keep serving the
/// circuits it already has — a circuit's hubs live on the pod serving it
/// (<c>MessageHubGrain</c> is <c>[PreferLocalPlacement]</c>) — until they close. That is entirely a
/// property of <c>preStop</c> + <c>/drain</c> + <c>ActiveCircuitTracker</c>, and all three run here,
/// for real, over a real socket.</para>
///
/// <para><b>What runs is the SHIPPED script.</b> The command is lifted verbatim out of
/// <c>deploy/helm/templates/memex-portal/deployment.yaml</c> — not retyped — with exactly four
/// declared substitutions, each of which asserts the token it replaces was there (so a chart edit
/// that renames one fails here rather than silently running a different script). Everything else,
/// including the <c>command -v curl</c> guard whose failure mode was the <c>wget</c> incident, is
/// the text Kubernetes executes.</para>
/// </summary>
public class RollMidSessionDrainTest
{
    private const string Deployment = "deploy/helm/templates/memex-portal/deployment.yaml";

    /// <summary>
    /// 🚨 <b>THE ACCEPTANCE.</b> A session that is open when the roll begins keeps being served, and
    /// SIGTERM is withheld until it closes — which is the whole of "non-disruptive rollout".
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ASessionOpenWhenTheRollBegins_KeepsBeingServedUntilItCloses()
    {
        await using var pod = await Pod.StartAsync();

        // A user is working on this pod when Kubernetes deletes it.
        pod.Circuits.Opened();

        // The kubelet runs preStop. The deadline is deliberately long here — this fact is about the
        // drain WAITING, and a deadline that fired would prove the opposite of what is claimed.
        using var preStop = pod.StartPreStop(deadlineSeconds: 60);

        // The pod reports itself busy over the very endpoint preStop probes.
        var probe = await pod.Client.GetAsync("/drain", TestContext.Current.CancellationToken);
        probe.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "/drain must answer 503 while a circuit is live — a 200 here is preStop's signal to "
            + "return, and the session would be cut off mid-sentence");
        (await probe.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("1 circuit(s) still open");

        // 🚨 THE SESSION SURVIVES: preStop is still polling, so SIGTERM has not been delivered and
        // the process is still serving. A fixed wait is the sanctioned kind — this is a negative
        // assertion, and there is no positive signal to filter for.
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);
        preStop.HasExited.Should().BeFalse(
            "preStop must keep waiting while a circuit is open. If it returns here the kubelet "
            + "SIGTERMs a pod that is still serving somebody, which is the 'the demo just crashed' "
            + "report #1342 closed and #1971 exists to keep closed");
        pod.Circuits.Count.Should().Be(1, "nothing has closed the session");

        // The user finishes and closes the tab.
        pod.Circuits.Closed();

        // Only now may the pod stop — and it must notice PROMPTLY, or a roll costs a poll interval
        // per pod for nothing.
        (await preStop.WaitForExitAsync(TimeSpan.FromSeconds(30)))
            .Should().BeTrue($"preStop must return once the last circuit closes. {preStop.Diagnostics}");
        preStop.ExitCode.Should().Be(0);

        // And what SIGTERM finds is written down — the line whose ABSENCE is the evidence of a hard
        // kill, and the reason #1971's first question was unanswerable from the logs in either
        // direction.
        await pod.StopAsync();
        pod.Log.Should().Contain(l => l.Contains("shutting down cleanly", StringComparison.Ordinal),
            "ApplicationStopping must report a clean departure — the silo leaves membership in "
            + "order, so no zombie entry is left for the cluster to place activations on. "
            + $"Got:\n{pod.LogDump}");
    }

    /// <summary>
    /// 🚨 The other half, and the defect #1971 was filed for: a session that outlives the drain must
    /// NOT ride to the grace ceiling. preStop returns at its own deadline, so SIGTERM lands with the
    /// shutdown margin intact and the silo departs; riding to the ceiling instead SIGKILLs the
    /// process with a LIVE silo, which is what left zombie membership entries the cluster kept
    /// placing activations on.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ASessionThatOutlivesTheDrain_ReturnsPreStopWithGraceStillLeft()
    {
        await using var pod = await Pod.StartAsync();

        // A forgotten tab: this circuit never closes.
        pod.Circuits.Opened();

        using var preStop = pod.StartPreStop(deadlineSeconds: 4);

        (await preStop.WaitForExitAsync(TimeSpan.FromSeconds(60)))
            .Should().BeTrue(
                "preStop must bound its OWN wait. Polling until terminationGracePeriodSeconds means "
                + "the kubelet SIGKILLs the process with a live Orleans silo — the host's 90 s "
                + $"ShutdownTimeout never runs and ApplicationStopping never fires. {preStop.Diagnostics}");
        preStop.ExitCode.Should().Be(0,
            "it must exit 0: a non-zero preStop is an event on the pod, not a faster shutdown");

        pod.Circuits.Count.Should().Be(1,
            "the straggler is still there — preStop returned because its deadline expired, not "
            + "because the session ended. That trade is the point: cut off the last stragglers "
            + "deliberately rather than keep everyone and then die abruptly");

        await pod.StopAsync();
        pod.Log.Should().Contain(l => l.Contains("GIVING UP", StringComparison.Ordinal)
                                      && l.Contains("STILL OPEN", StringComparison.Ordinal),
            "the deliberate cut-off must be LOUD and counted — silently dropping the last sessions "
            + $"is how a drain regression stays invisible. Got:\n{pod.LogDump}");
    }

    // ── the shipped preStop script ───────────────────────────────────────────

    /// <summary>
    /// The preStop command as the chart ships it, with the four declared substitutions applied.
    /// Each substitution asserts the token it replaces is present, so a chart edit that renames one
    /// fails HERE instead of quietly running a script this test made up.
    /// </summary>
    private static string PreStopScript(int port, int deadlineSeconds)
    {
        var script = ShippedPreStopCommand();

        // 1. The INGRESS drain. Its value is asserted, not re-timed: fifteen seconds of sleeping
        //    would be added to every run of this file for a constant DrainDeadlineGuard already
        //    reads. What is under test here is the SESSION drain that follows it.
        script = Substitute(script, "sleep 15;", "sleep 1;");
        // 2. The port. The container listens on 8080; this test's Kestrel takes an ephemeral one.
        script = Substitute(script, "http://127.0.0.1:8080/drain", $"http://127.0.0.1:{port}/drain");
        // 3. The deadline — the Helm expression, evaluated here instead of by `helm template`.
        script = Substitute(script,
            new Regex(@"DEADLINE=\{\{[^}]*\}\}\}?;", RegexOptions.None, TimeSpan.FromSeconds(5)),
            $"DEADLINE={deadlineSeconds};",
            "DEADLINE={{ sub (.Values.portal.drainSeconds …) (.Values.portal.shutdownMarginSeconds …) }};");
        // 4. The poll interval, for the same reason as (1).
        script = Substitute(script, "sleep 5;", "sleep 1;");

        return script;
    }

    /// <summary>Lifts <c>lifecycle.preStop.exec.command</c>'s folded scalar out of the chart.</summary>
    private static string ShippedPreStopCommand()
    {
        var chart = File.ReadAllText(Path.Combine(FindRepoRoot(), Deployment));
        var preStop = chart.IndexOf("preStop:", StringComparison.Ordinal);
        Assert.True(preStop > 0, $"{Deployment} no longer has a preStop hook — without it a roll "
                                 + "cuts every open circuit the moment the ingress window closes (#1342).");
        var folded = chart.IndexOf("- >-", preStop, StringComparison.Ordinal);
        Assert.True(folded > 0, $"{Deployment}'s preStop command is no longer a folded scalar — "
                                + "this test lifts the SHIPPED text rather than retyping it.");
        var end = chart.IndexOf("\n          envFrom:", folded, StringComparison.Ordinal);
        Assert.True(end > folded, $"{Deployment}: could not find the end of the preStop command block.");

        var lines = chart[(chart.IndexOf('\n', folded) + 1)..end]
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        return string.Join(" ", lines);
    }

    private static string Substitute(string script, string token, string replacement)
    {
        Assert.True(script.Contains(token, StringComparison.Ordinal),
            $"{Deployment}'s preStop no longer contains `{token}`. This test substitutes it to run "
            + "the shipped script against a test host; if the token changed, update the "
            + "substitution — do NOT let the test run a script that is not what ships.");
        return script.Replace(token, replacement, StringComparison.Ordinal);
    }

    private static string Substitute(string script, Regex token, string replacement, string described)
    {
        Assert.True(token.IsMatch(script),
            $"{Deployment}'s preStop no longer contains `{described}`. This test evaluates that "
            + "Helm expression itself; if its shape changed, update the substitution rather than "
            + "running a script that is not what ships.");
        return token.Replace(script, replacement);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── the pod under test ───────────────────────────────────────────────────

    /// <summary>
    /// A real Kestrel host serving the REAL <c>/drain</c> endpoint over a real socket — a socket and
    /// not a <c>TestServer</c> because the thing under test is the shipped shell script, and
    /// <c>curl</c> cannot call an in-memory pipeline.
    /// </summary>
    private sealed class Pod : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly LogSink sink;

        private Pod(WebApplication app, LogSink sink, string baseUrl, int port)
        {
            this.app = app;
            this.sink = sink;
            Port = port;
            Client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            Circuits = app.Services.GetRequiredService<ActiveCircuitTracker>();
        }

        public int Port { get; }

        public HttpClient Client { get; }

        public ActiveCircuitTracker Circuits { get; }

        public IReadOnlyCollection<string> Log => sink.Lines;

        public string LogDump => string.Join("\n", sink.Lines);

        public static async Task<Pod> StartAsync()
        {
            // 🚨 Fail RED, never skip. The `command -v curl || exit 0` guard in the shipped script
            // means a machine without curl would make every assertion below vacuously true — the
            // script would exit 0 immediately and this test would report a passing drain it never
            // exercised. That is the same "a gate that cannot fail" shape the drain itself had.
            Assert.True(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
                "this test executes the shipped POSIX preStop script; run it on Linux or macOS.");
            Assert.True(WhichExists("curl"),
                "`curl` is not on PATH. The shipped preStop skips the drain entirely when its probe "
                + "tool is missing (fail-open, deliberately), so without curl this test would pass "
                + "having verified nothing. Install curl rather than skipping.");

            var sink = new LogSink();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(sink);
            // 🚨 The test bin's appsettings.json pins `MeshWeaver: Warning`, which would swallow the
            // drain's Information lines — and the clean-shutdown line is Information while the
            // give-up line is Warning, so without this the test would assert one narrative and
            // silently skip the other. Raised for THIS host only; the shipped levels are untouched.
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:MeshWeaver"] = "Trace",
                [$"Logging:LogLevel:{typeof(DrainProgress).FullName}"] = "Trace",
            });
            builder.Services.AddSingleton<ActiveCircuitTracker>();

            var app = builder.Build();
            app.MapDrainEndpoint();
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Pod(app, sink, address, new Uri(address).Port);
        }

        public PreStopRun StartPreStop(int deadlineSeconds) =>
            PreStopRun.Start(PreStopScript(Port, deadlineSeconds));

        /// <summary>SIGTERM's arrival: runs <c>ApplicationStopping</c>, which is where the drain
        /// writes down what it found.</summary>
        public Task StopAsync() => app.StopAsync();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }

        private static bool WhichExists(string tool)
        {
            using var probe = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", $"command -v {tool}"])
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            probe.WaitForExit(10_000);
            return probe.ExitCode == 0;
        }
    }

    /// <summary>One execution of the shipped preStop command, as the kubelet runs it.</summary>
    private sealed class PreStopRun : IDisposable
    {
        private readonly Process process;
        private readonly ConcurrentQueue<string> output = new();

        private PreStopRun(Process process, string script)
        {
            this.process = process;
            Script = script;
        }

        public string Script { get; }

        public bool HasExited => process.HasExited;

        public int ExitCode => process.ExitCode;

        public string Diagnostics =>
            $"Script:\n  {Script}\nOutput:\n  {string.Join("\n  ", output)}";

        public static PreStopRun Start(string script)
        {
            var info = new ProcessStartInfo("/bin/sh", ["-c", script])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var process = Process.Start(info)!;
            var run = new PreStopRun(process, script);
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) run.output.Enqueue(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) run.output.Enqueue(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return run;
        }

        /// <summary>Bounded, and it reports rather than throws — the caller states what a false
        /// means, which is the whole assertion.</summary>
        public async Task<bool> WaitForExitAsync(TimeSpan budget)
        {
            using var cts = new CancellationTokenSource(budget);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { /* already gone */ }
            process.Dispose();
        }
    }

    /// <summary>Captures the drain's own narrative so the shutdown report can be asserted.</summary>
    private sealed class LogSink : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();

        public IReadOnlyCollection<string> Lines => lines;

        public ILogger CreateLogger(string categoryName) => new SinkLogger(lines);

        public void Dispose() { }

        private sealed class SinkLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}
