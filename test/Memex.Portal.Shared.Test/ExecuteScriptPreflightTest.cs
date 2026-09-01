#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #2918 — <c>ExecuteScript</c> against a target that CANNOT answer used to sit out the whole
/// dispatch budget and then report a <see cref="TimeoutException"/>.
///
/// <para><b>Why the wait was total.</b> The <c>ExecuteScriptRequest</c> handler is registered by
/// the <b>Code</b> NodeType's <c>HubConfiguration</c> and by nothing else. A target that is not a
/// Code node — a Markdown page, or a path with no node at all — therefore routes to a hub that
/// carries no such handler: nothing refuses, nothing NACKs, the request is simply never answered.
/// The caller's own <c>.Timeout(...)</c> is the only thing that ever ends the wait, so a call that
/// could never have succeeded costs the full 60–120 s and one server-side error log — multiplied
/// by the retry count when an agent loops (production fingerprint <c>6094e6b21967f47b</c>,
/// memex-cloud, 2026-08-31).</para>
///
/// <para><b>What these tests pin is the ELAPSED TIME, not merely "an error came back".</b> The
/// pre-fix code already returned a structured error — it just took the entire budget to say it, so
/// an assertion on the payload alone passes on the defect. Each test therefore runs with a
/// generous dispatch budget and asserts the answer arrives in a small fraction of it: on the old
/// code the observable emits only when the budget elapses, so the stopwatch is the discriminator.
/// </para>
/// </summary>
public class ExecuteScriptPreflightTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The dispatch budget handed to <c>ExecuteScript</c>. Large enough that "waited it out" and
    /// "answered from the pre-flight" cannot be confused for one another.
    /// </summary>
    private const int DispatchBudgetSeconds = 45;

    /// <summary>
    /// Captures what the mesh's own <see cref="ILoggerFactory"/> emits, so "the refusal is visible
    /// at Warning+" is an assertion rather than a claim. Instance-owned — its lifetime is this
    /// test's mesh — never static.
    /// </summary>
    private readonly CapturingLoggerProvider logs = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs));

    /// <summary>
    /// The bar the answer must beat. The pre-flight's own read is bounded at 10 s (and only
    /// reaches that bound when it gets NO verdict, in which case it deliberately falls through to
    /// the dispatch rather than refusing), so a real pre-flight refusal lands far below this —
    /// while the old wait-it-out path cannot come in under <see cref="DispatchBudgetSeconds"/>.
    /// </summary>
    private static readonly TimeSpan FailFastBar = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The overall await bound. Comfortably above <see cref="DispatchBudgetSeconds"/> so the OLD
    /// behaviour is measured rather than cut short — a test that times out where the defect merely
    /// waits proves nothing about how long it waited.
    /// </summary>
    private static readonly TimeSpan AwaitBound = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The target does not exist at all. There is nothing to execute and no amount of waiting will
    /// change that, so the answer must be immediate and must NAME the condition.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task ExecuteScript_OnAnAbsentPath_AnswersFromThePreflight_WithoutBurningTheDispatchBudget()
    {
        var absent = $"{TestPartition}/NoSuchScript-{Guid.NewGuid():N}";

        var (result, elapsed) = await Run(absent);

        elapsed.Should().BeLessThan(FailFastBar,
            $"a target that does not exist is knowable up front — the pre-flight must answer it "
            + $"instead of sitting out the {DispatchBudgetSeconds}s dispatch budget (#2918). "
            + $"Took {elapsed.TotalSeconds:F1}s.");

        result.GetProperty("status").GetString().Should().Be("Error",
            "a target that cannot run must be refused, never reported as Dispatched");
        result.GetProperty("errorType").GetString().Should().Be("NodeNotFound",
            "the caller needs the CONDITION, not an exception type name — the old answer was "
            + $"System.TimeoutException, which says only that we gave up. Got: {result}");
    }

    /// <summary>
    /// The target EXISTS but is not a Code node — the test mesh's <c>TestData</c> partition root is
    /// a Markdown node. Its hub activates perfectly happily; it just has no
    /// <c>ExecuteScriptRequest</c> handler, so it answers nothing. This is the case that proves the
    /// wait is about the HANDLER, not about routing failing to find a hub.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task ExecuteScript_OnANodeThatIsNotCode_AnswersFromThePreflight_WithoutBurningTheDispatchBudget()
    {
        var (result, elapsed) = await Run(TestPartition);

        elapsed.Should().BeLessThan(FailFastBar,
            $"the node was readable and demonstrably not executable — deciding that needs one "
            + $"bounded read, not the whole {DispatchBudgetSeconds}s dispatch budget (#2918). "
            + $"Took {elapsed.TotalSeconds:F1}s.");

        result.GetProperty("status").GetString().Should().Be("Error",
            "a Markdown node carries no script — this can only ever be refused");
        result.GetProperty("errorType").GetString().Should().Be("NotExecutable",
            $"the refusal must say WHAT is wrong with the target. Got: {result}");
    }

    /// <summary>
    /// 🚨 THE PRE-FLIGHT MUST NOT MAKE THE POD GO QUIET. #841's guarantee is not "the caller is
    /// told" — it is "the caller is told AND a Warning+ trace is left", because a dispatch that
    /// creates no Activity node otherwise produces no evidence anywhere, which is what made that
    /// issue undiagnosable in production. Both branches this pre-flight now answers BEFORE carry
    /// that trace (<c>CodeNodeType.RefuseDispatch</c>'s <c>ExecuteScript refused on {Hub}</c>, and
    /// the dispatch's own <c>ExecuteScript dispatch refused for {Path}</c>) — so making the
    /// guaranteed failure FAST must not also make it silent.
    ///
    /// <para>Measured rather than assumed: without the log line in <c>PreflightError</c> this
    /// verdict comes back correctly and the pod emits nothing at Warning or above, which is
    /// exactly the pre-#841 condition. MeshWeaver.Plugins'
    /// <c>ExecuteScriptDispatchDiagnosticsTest</c> caught it from the satellite side; this pins it
    /// where the code lives.</para>
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task APreflightRefusal_LeavesAWarningTrace_notOnlyACallerVerdict()
    {
        var absent = $"{TestPartition}/NoSuchScript-{Guid.NewGuid():N}";

        var (result, _) = await Run(absent);

        result.GetProperty("status").GetString().Should().Be("Error");

        logs.Records
            .FirstOrDefault(r => r.Level >= LogLevel.Warning
                                 && r.Message.Contains("ExecuteScript refused", StringComparison.Ordinal))
            .Should().NotBeNull(
                "a dispatch that creates no Activity node must leave a Warning+ trace containing "
                + "'ExecuteScript refused' — answering from the pre-flight skips the two sinks "
                + "that carried that guarantee (#841), so it has to carry it itself. Captured: ["
                + string.Join(" | ", logs.Records.Select(r => $"{r.Level} {r.Category}: {r.Message}"))
                + "]");
    }

    // ── helpers ──

    /// <summary>
    /// Runs the tool exactly as the MCP surface does and returns its parsed JSON plus the wall
    /// clock the caller actually paid. The stopwatch starts at SUBSCRIBE — <c>ExecuteScript</c>
    /// returns a cold observable, so timing the call itself would measure nothing.
    /// </summary>
    private async Task<(JsonElement Result, TimeSpan Elapsed)> Run(string path)
    {
        var operations = new MeshOperations(Mesh);
        var stopwatch = Stopwatch.StartNew();
        var json = await Observable.Defer(() =>
                operations.ExecuteScript(path, DispatchBudgetSeconds))
            .FirstAsync()
            .Timeout(AwaitBound)
            .Await(TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Output.WriteLine($"ExecuteScript('{path}') answered in {stopwatch.Elapsed.TotalSeconds:F2}s: {json}");
        return (JsonDocument.Parse(json).RootElement.Clone(), stopwatch.Elapsed);
    }

    private sealed record CapturedLog(LogLevel Level, string Category, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        // Instance field on an instance owned by the test's mesh — never static state.
        private readonly ConcurrentQueue<CapturedLog> records = new();

        public IReadOnlyList<CapturedLog> Records => records.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, records);

        public void Dispose() { }

        private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => sink.Enqueue(new CapturedLog(logLevel, category, formatter(state, exception)));
        }
    }
}
