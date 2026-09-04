using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #3249, the remainder: a last-execution stamp that does not land must SAY SO.
///
/// <para><see cref="CodeCellCurrencyThroughTheMeshTest"/> pins what a cell may claim once the stamp
/// has (or has not) landed. This class pins the write itself — that it terminates with a verdict
/// instead of a silent no-op, and that the verdict reaches an operator as one alertable line which
/// does not misreport the run as failed.</para>
///
/// <para>🚨 <b>Why the seam and not the dispatch.</b> A stamp failure cannot be provoked through
/// <c>ExecuteScriptRequest</c>: the handler refuses to dispatch at all unless the node already
/// reads as an executable <see cref="CodeConfiguration"/>, so by the time the stamp runs the only
/// remaining failure modes are infrastructural ones a test cannot induce from outside. The seam is
/// therefore the only place the contract is assertable — and the happy-path wiring (handler →
/// seam) is already covered end to end by
/// <c>CodeCellCurrencyThroughTheMeshTest.ARunTheNodeDidRecord_ReadsAsCurrent_AndGoesStaleWhenTheCodeMoves</c>,
/// which would fail if the dispatch stopped calling it.</para>
/// </summary>
public class CodeNodeStampFailureTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static TimeSpan Bound => TestTimeouts.Convergence;

    private const string Source = "1 + 1";

    /// <summary>
    /// The defect the issue's fourth failure path describes, and the only one that produced NO
    /// signal whatsoever: the stamp's update lambda tested <c>curr.Content is CodeConfiguration</c>,
    /// so a node whose content it cannot read simply did not match — <c>Update</c> short-circuited
    /// the no-op, the observable COMPLETED as though the write had happened, and the stamp was gone
    /// with no exception and no log. Nothing downstream could distinguish that from success.
    ///
    /// <para>Content that is absent is the deterministic instance of that class (no serializer, no
    /// type registry, no timing involved); untyped JSON and a same-named record from another
    /// collectible assembly are the others, and the typed overload refuses all three the same way.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AStampThatCannotLand_Faults_InsteadOfCompletingAsThoughItHad()
    {
        // A node the stamp CANNOT write: there is no CodeConfiguration on it to carry the fields.
        var path = await CreateNode(content: null);

        var outcome = await CodeNodeType
            .LastExecutionStamp(
                Mesh.GetWorkspace().GetMeshNodeStream(path),
                $"{TestPartition}/_Activity/{Guid.NewGuid():N}",
                executedBy: "rbuergi",
                code: Source,
                language: "csharp")
            // Materialize so BOTH terminals are observable: the old shape completed with a value,
            // the fixed one faults, and asserting on the exception alone could not tell the
            // difference between "it faulted" and "the test never got that far".
            .Materialize()
            .FirstAsync()
            .Timeout(Bound)
            .Await();

        outcome.Kind.Should().Be(System.Reactive.NotificationKind.OnError,
            "a stamp that cannot land must terminate with a VERDICT — completing as though it had "
            + "written is what made this failure path invisible to every caller and every log "
            + "(#3249). Observed: {0}", Describe(outcome));

        outcome.Exception!.Message.Should().Contain(nameof(CodeConfiguration),
            "the verdict has to name what could not be read, or an operator holding it still "
            + "cannot tell a stamp failure from any other write failure");
        outcome.Exception!.Message.Should().Contain("NOT applied",
            "and it has to say the write did not happen — a diagnosis that leaves that open "
            + "invites exactly the 'maybe it partially landed' guessing the currency rule exists "
            + "to remove");
    }

    /// <summary>
    /// The control arm. Without it, "a stamp that cannot land faults" would be satisfied by a seam
    /// that faults unconditionally — a guard that cannot pass proves as little as one that cannot
    /// fail. A cell the stamp CAN write must come back carrying all four fields, including the
    /// fingerprint of what was submitted.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AStampThatCanLand_WritesAllFourFields()
    {
        var path = await CreateNode(new CodeConfiguration { Code = Source, IsExecutable = true });
        var activityPath = $"{TestPartition}/_Activity/{Guid.NewGuid():N}";

        await CodeNodeType
            .LastExecutionStamp(
                Mesh.GetWorkspace().GetMeshNodeStream(path),
                activityPath,
                executedBy: "rbuergi",
                code: Source,
                language: "csharp")
            .FirstAsync()
            .Timeout(Bound)
            .Await();

        var stamped = await ReadCell(path, c => c is { LastExecutedCodeHash: not null and not "" });

        stamped.LastActivityPath.Should().Be(activityPath,
            "the cell's pointer to its run is the whole reason the stamp exists");
        stamped.LastExecutedBy.Should().Be("rbuergi");
        stamped.LastExecutedAt.Should().NotBeNull();
        stamped.LastExecutedCodeHash.Should().Be(CodeFingerprint.Of(Source, "csharp"),
            "the fingerprint records what the run SUBMITTED, which is what lets the cell prove "
            + "its output belongs to the code on screen");
        stamped.OutputCurrency().Should().Be(CodeOutputCurrency.Current,
            "a stamp that landed whole leaves the one state a cell may render as up to date");
    }

    /// <summary>
    /// The report itself. A swallowed stamp is a PERMANENT loss that nothing retries, and its
    /// visible consequence is a cell telling a human it has never run — so it is an Error, not the
    /// Warning it used to be (both ship to Loki, so the level buys alerting, not volume).
    ///
    /// <para>And it must not read as a failed RUN. The run succeeded; only its record did not land,
    /// and the transcript is still at the activity path. A line that blurs the two sends an
    /// operator hunting a script failure that never happened.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public void TheReport_IsOneErrorThatDoesNotBlameTheRun()
    {
        var recorder = new RecordingLoggerProvider();
        var cell = new Address("rbuergi", "daily-rollup");
        const string ActivityPath = "rbuergi/_Activity/deadbeef";

        CodeNodeType.ReportStampNotRecorded(
            recorder.CreateLogger("MeshWeaver.Graph.CodeNodeType"),
            cell,
            ActivityPath,
            new InvalidOperationException("the partition write was refused"));

        var record = recorder.Records.Should().ContainSingle(
            "one swallowed stamp must produce exactly ONE alertable line — the shape this replaced "
            + "had a copy of the log call per failure path, which is how the third path came to "
            + "have none").Subject;

        record.Level.Should().Be(LogLevel.Error,
            "nothing retries this write, the loss is permanent, and the user-visible consequence "
            + "is a cell that reports itself as never run — none of which is the 'degraded but "
            + "self-correcting' that Warning claims");
        record.EventId.Id.Should().Be(3249,
            "a stable event id is what an operator alerts on; the message text is free to change");
        record.Error.Should().BeOfType<InvalidOperationException>(
            "the cause travels with type and stack, never reduced to its .Message");
        record.Message.Should().Contain(cell.ToString()).And.Contain(ActivityPath,
            "the line has to name the cell whose record was lost AND where the run's transcript "
            + "actually is, or it cannot be acted on");
        record.Message.Should().Contain("run itself is unaffected",
            "the run SUCCEEDED — a line that reads as a failed run sends an operator hunting a "
            + "script failure that never happened (#3249)");
    }

    // ── helpers ──

    private async Task<string> CreateNode(object? content)
    {
        var id = $"cell{Guid.NewGuid():N}"[..12];
        var path = $"{TestPartition}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = CodeNodeType.NodeType,
                Name = id,
                State = MeshNodeState.Active,
                Content = content,
            }))
            .FirstAsync()
            .Timeout(Bound)
            .Await();
        return path;
    }

    /// <summary>
    /// Reads the cell off the authoritative single-node stream, waiting on the CONDITION rather
    /// than the clock. 🚨 <c>ContentAs</c>, never <c>is CodeConfiguration</c>.
    /// </summary>
    private async Task<CodeConfiguration> ReadCell(string path, Func<CodeConfiguration?, bool> until)
    {
        var options = Mesh.JsonSerializerOptions;
        return (await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(node => node is not null)
            .Select(node => node.ContentAs<CodeConfiguration>(options))
            .Where(until)
            .FirstAsync()
            .Timeout(Bound)
            .Await())!;
    }

    private static string Describe(System.Reactive.Notification<MeshNode> outcome)
        => outcome.Kind switch
        {
            System.Reactive.NotificationKind.OnNext =>
                $"OnNext({outcome.Value.Path}) — the write reported success",
            System.Reactive.NotificationKind.OnCompleted => "OnCompleted with no value",
            _ => $"OnError({outcome.Exception?.GetType().Name})",
        };

    /// <summary>
    /// Instance-scoped log sink — no static state, per the house rule. Records the structured
    /// facts an assertion needs (level, event id, exception) rather than only the formatted prose.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogRecord> records = new();

        public IReadOnlyList<LogRecord> Records => records.ToArray();

        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, records);

        public void Dispose() { }

        internal sealed record LogRecord(
            LogLevel Level, EventId EventId, string Category, string Message, Exception? Error);

        private sealed class Recorder(string category, ConcurrentQueue<LogRecord> sink) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull
                => Disposable.Empty;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                sink.Enqueue(new LogRecord(
                    logLevel, eventId, category, formatter(state, exception), exception));
            }
        }
    }
}
