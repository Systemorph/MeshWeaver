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
///
/// <para><b>And what the pre-flight must not lose.</b> Answering the caller is only half of what
/// the dispatch it replaced did: #841's contract is that a run which ends without an Activity node
/// is visible to the OPERATOR too. That half is pinned by
/// <see cref="ExecuteScript_PreflightRefusal_IsVisibleToTheOperator_NotJustToTheCaller"/>, which
/// asserts on the mesh's own logger rather than on elapsed time.</para>
/// </summary>
public class ExecuteScriptPreflightTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Captures what the mesh's own <see cref="ILoggerFactory"/> emits, so "the refusal is visible
    /// at Warning+" is an assertion rather than a claim. Instance-owned — its lifetime is this
    /// test's mesh — never static state.
    /// </summary>
    private readonly CapturingLoggerProvider logs = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs));

    /// <summary>
    /// The dispatch budget handed to <c>ExecuteScript</c>. Large enough that "waited it out" and
    /// "answered from the pre-flight" cannot be confused for one another.
    /// </summary>
    private const int DispatchBudgetSeconds = 45;

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
    /// 🚨 #841's OTHER half, which the pre-flight silently dropped.
    ///
    /// <para>A dispatch that ends without an Activity node must reach the CALLER <b>and</b> the
    /// OPERATOR — that is why <c>CodeNodeType.HandleExecuteScript</c>'s refusal sink does both
    /// things in one place, and why every test in the engine's dispatch-diagnostics suite asserts
    /// the Warning+ trace as well as the verdict. The pre-flight above moved two of those three
    /// verdicts — "no readable node" and "not a runnable Code node" — from the owning hub to the
    /// caller, where the answer is cheaper; the log line did not come with them. From that change
    /// onward the commonest refusals reproduced #841's reported picture exactly: the caller is
    /// told, and the pod emits NOTHING at Warning or above.</para>
    ///
    /// <para>Both pre-flight branches are exercised, because they are two <c>return</c>s in
    /// <c>DescribeUnrunnableTarget</c> and a trace on one says nothing about the other. The
    /// assertion is on the mesh's own <see cref="ILoggerFactory"/>, not on test output, so it
    /// measures what an operator would actually see in Loki.</para>
    /// </summary>
    [Theory(Timeout = 180000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteScript_PreflightRefusal_IsVisibleToTheOperator_NotJustToTheCaller(
        bool absentPath)
    {
        var path = absentPath
            ? $"{TestPartition}/NoSuchScript-{Guid.NewGuid():N}"
            : TestPartition;

        var (result, _) = await Run(path);
        result.GetProperty("status").GetString().Should().Be("Error", result.ToString());

        var refusals = logs.Records
            .Where(r => r.Level >= LogLevel.Warning
                && r.Message.Contains("ExecuteScript refused", StringComparison.Ordinal))
            .ToArray();

        refusals.Should().NotBeEmpty(
            "a refused dispatch must leave a Warning+ trace naming the target — the caller's JSON "
            + "is not evidence anybody but the caller can see, and an operator with no trace is "
            + "the whole of #841. Captured at Warning+: [{0}]",
            string.Join(" | ", logs.Records
                .Where(r => r.Level >= LogLevel.Warning)
                .Select(r => $"{r.Level} {r.Category}: {r.Message}")));

        refusals.Should().Contain(r => r.Message.Contains(path, StringComparison.Ordinal),
            "the trace must name the PATH that was refused, or an operator reading it cannot tell "
            + "which run died");
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

    // ── log capture ──

    private sealed record CapturedLog(
        LogLevel Level, string Category, string Message, Exception? Exception);

    /// <summary>
    /// An <see cref="ILoggerProvider"/> that keeps every record the mesh emits. The backing queue
    /// is an INSTANCE field on an instance the test's mesh owns — the no-static-state rule applies
    /// to test infrastructure exactly as it does to <c>src/</c>.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
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
                => sink.Enqueue(new CapturedLog(
                    logLevel, category, formatter(state, exception), exception));
        }
    }
}
