using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// 🚨 <b>A cancellation is not a fault</b> — issues #2152 and #2182.
///
/// <para><b>What production reported.</b> <c>MeshWeaver.Mesh.CreateNode</c> logged
/// <c>fail: Unexpected error during node creation at …</c> for a bare
/// <c>TaskCanceledException</c> — 491 times in three days, across ten pods and several deployment
/// revisions, on paths as mundane as <c>Home</c> and <c>Admin</c>. <c>MeshWeaver.Mesh.MeshNode</c>
/// logged <c>fail: [DeleteNode] unexpected path=… partial-deleted=0</c> for the same shape: that
/// counter says the node was never touched, so there was no "unexpected" state to report at all.
/// Both are cooperative cancellation — the caller went away, a partition cleanup cascaded into its
/// <c>_Access</c> satellites, the hub tore down. Nothing failed.</para>
///
/// <para><b>Why it mattered.</b> Not volume: <i>signal</i>. A genuine storage failure on the very
/// same path printed identically to a client disconnect, so the one red line worth reading was
/// indistinguishable from 491 that were not — and both sites automatically opened tickets.</para>
///
/// <para><b>The bound these tests hold.</b> The rule is NOT "catch OperationCanceledException" —
/// that would swallow the one impostor that matters. A timeout raised on a token is the same CLR
/// type and IS a fault; .NET marks it by hanging a <see cref="TimeoutException"/> off the
/// cancellation. <see cref="Timeout_dressed_as_a_cancellation_stays_a_fault"/> is what makes the
/// distinction load-bearing rather than decorative, and
/// <see cref="A_genuine_fault_still_logs_Error_with_its_exception"/> pins that the fault path keeps
/// its level, its exception, and now also names the fault in its message (#2153).</para>
/// </summary>
public class CancellationIsNotAFaultTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CreateCategory = "MeshWeaver.Mesh.CreateNode";
    private const string DeleteCategory = "MeshWeaver.Mesh.MeshNode";

    /// <summary>Node ids the scripted validator reacts to. Everything else it passes.</summary>
    private const string CancelOnCreate = "cancelled-create";
    private const string TimeoutOnCreate = "timeout-create";
    private const string FaultOnCreate = "faulted-create";
    private const string CancelOnDelete = "cancelled-delete";

    private readonly CapturingLoggerProvider logs = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton<INodeValidator, ScriptedOutcomeValidator>()
                .AddSingleton<ILoggerProvider>(logs)
                // Debug for the two categories under test ONLY, and only on THIS provider — the
                // benign branch logs at Debug, and a test that cannot see it could only assert an
                // absence. Nothing in src/ or its appsettings is touched (AGENTS.md: log levels in
                // code are a production cost decision, not a debugging knob).
                .AddLogging(b => b
                    .AddFilter<CapturingLoggerProvider>(CreateCategory, LogLevel.Debug)
                    .AddFilter<CapturingLoggerProvider>(DeleteCategory, LogLevel.Debug)));

    /// <summary>
    /// #2152. A cancelled create logs at Debug — never <c>fail:</c> — and still answers the caller,
    /// with the reason that is actually true of it: <c>Unavailable</c> ("not evaluated; retrying is
    /// meaningful"), never <c>Unknown</c>.
    /// </summary>
    [Fact]
    public async Task A_cancelled_create_is_not_logged_as_a_fault()
    {
        var response = await CreateAt(CancelOnCreate);

        response.Success.Should().BeFalse("the node was not created — the caller must know");
        response.Error.Should().Contain("cancelled",
            "the caller reads this string, and 'Unexpected error' says the opposite of what happened");
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unavailable,
            "a cancelled create DECIDED nothing about the node — Unknown implies it was judged");

        logs.Red(CreateCategory, CancelOnCreate).Should().BeEmpty(
            "a cancellation is routine teardown; at fail: level it is indistinguishable from the "
            + "storage outage on the same path that on-call actually needs to see");
        var benign = logs.Entries(CreateCategory, CancelOnCreate)
            .Should().ContainSingle(e => e.Message.Contains("[CreateNode] cancelled path=")).Which;
        benign.Level.Should().Be(LogLevel.Debug);
        benign.Exception.Should().BeOfType<TaskCanceledException>(
            "downgrading the level must not throw the evidence away — the exception still rides along");
    }

    /// <summary>
    /// The impostor. <c>HttpClient</c> (and friends) express a TIMEOUT as a
    /// <see cref="TaskCanceledException"/> carrying a <see cref="TimeoutException"/> cause. That is
    /// an availability fault someone must look at, and it must survive the #2152 fix with its Error
    /// level and its exception intact — otherwise "cancellation is benign" would quietly have become
    /// "storage timeouts are benign".
    /// </summary>
    [Fact]
    public async Task Timeout_dressed_as_a_cancellation_stays_a_fault()
    {
        var response = await CreateAt(TimeoutOnCreate);

        response.Success.Should().BeFalse();
        var red = logs.Red(CreateCategory, TimeoutOnCreate).Should().ContainSingle(
            "a timeout is a fault however it is spelled").Which;
        red.Exception.Should().BeOfType<TaskCanceledException>();
        red.Exception!.InnerException.Should().BeOfType<TimeoutException>(
            "that inner cause is the exact signal separating the two, so the test must exercise it");
    }

    /// <summary>
    /// The fault direction, and #2153's ask: an unexpected failure keeps <c>Error</c>, keeps its
    /// exception, and now also NAMES the fault inside the message — so an incident whose capture
    /// lost the trailing exception line still says what went wrong.
    /// </summary>
    [Fact]
    public async Task A_genuine_fault_still_logs_Error_with_its_exception()
    {
        var response = await CreateAt(FaultOnCreate);

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unknown);

        var red = logs.Red(CreateCategory, FaultOnCreate).Should().ContainSingle().Which;
        red.Exception.Should().BeOfType<NotSupportedException>(
            "the catch-all must forward the exception it caught — that half of #2153 was already true");
        red.Message.Should().Contain(nameof(NotSupportedException),
            "and the type must ALSO be in the message: a burst that loses its trailing exception "
            + "line still has to name the fault (#2153)");
        red.Message.Should().Contain("the fixture's genuine fault",
            "the exception's own words belong in the message for the same reason");
    }

    /// <summary>
    /// #2182. A delete cancelled before it removed anything logs at Debug and says so —
    /// <c>partial-deleted=0</c> means there is no torn subtree to report. "unexpected" is reserved
    /// for the case that IS one.
    /// </summary>
    [Fact]
    public async Task A_delete_cancelled_before_it_touched_anything_is_not_logged_as_a_fault()
    {
        // Create the victim first — the validator only cancels this path's DELETE.
        var node = new MeshNode(CancelOnDelete, TestPartition)
        {
            Name = "Doomed", NodeType = "Markdown", Content = "victim",
        };
        var created = await AwaitResponseAsync<CreateNodeResponse>(
            new CreateNodeRequest(node), o => o.WithTarget(RequestHub.NodeOperationTarget()));
        created.Message.Success.Should().BeTrue("the fixture needs a node to delete");

        var deleted = await AwaitResponseAsync<DeleteNodeResponse>(
            new DeleteNodeRequest(node.Path), o => o.WithTarget(RequestHub.NodeOperationTarget()));

        deleted.Message.Success.Should().BeFalse("the delete did not happen");
        deleted.Message.Error.Should().Contain("cancelled",
            "'unexpected' described a state the node was never in");
        deleted.Message.RejectionReason.Should().Be(NodeDeletionRejectionReason.Unavailable,
            "a cancelled delete decided nothing — retrying it is meaningful");

        logs.Red(DeleteCategory, CancelOnDelete).Should().BeEmpty(
            "nothing was removed, so there is no inconsistency for a fail: line to be about");
        logs.Entries(DeleteCategory, CancelOnDelete)
            .Should().Contain(
                e => e.Level == LogLevel.Debug && e.Message.Contains("partial-deleted=0"),
                "the benign line still records what happened, including the counter that makes it benign");
    }

    /// <summary>
    /// The classifier itself, on the exact shapes production produces. It is the ONE place the
    /// benign/fault judgement is made, so it is worth pinning directly and not only through the
    /// handlers.
    /// </summary>
    [Fact]
    public void The_classifier_separates_cancellation_from_every_impostor()
    {
        CancellationClassifier.IsCooperativeCancellation(Cancellation()).Should().BeTrue(
            "the production shape: TaskCanceledException, 'A task was canceled.', no inner");
        CancellationClassifier.IsCooperativeCancellation(new OperationCanceledException()).Should().BeTrue();
        CancellationClassifier.IsCooperativeCancellation(new AggregateException(Cancellation()))
            .Should().BeTrue("a Task bridge can hand over a single-inner aggregate");

        CancellationClassifier.IsCooperativeCancellation(TimeoutDressedAsCancellation()).Should().BeFalse(
            "an HttpClient/Npgsql timeout wears this exact costume and IS a fault");
        CancellationClassifier.IsCooperativeCancellation(new TimeoutException()).Should().BeFalse();
        CancellationClassifier.IsCooperativeCancellation(new InvalidOperationException()).Should().BeFalse();
        CancellationClassifier.IsCooperativeCancellation(null).Should().BeFalse();
        CancellationClassifier.IsCooperativeCancellation(
                new AggregateException(Cancellation(), new InvalidOperationException("also this")))
            .Should().BeFalse("a multi-inner aggregate is genuinely several faults");

        CancellationClassifier.Describe(Cancellation()).Should()
            .Contain("TaskCanceledException").And.Contain("token cancelled: true",
                "the log line must say WHY it judged the outcome benign, not merely assert it");
    }

    private async Task<CreateNodeResponse> CreateAt(string id)
    {
        var node = new MeshNode(id, TestPartition)
        {
            Name = id, NodeType = "Markdown", Content = "fixture",
        };
        var response = await AwaitResponseAsync<CreateNodeResponse>(
            new CreateNodeRequest(node), o => o.WithTarget(RequestHub.NodeOperationTarget()));
        return response.Message;
    }

    /// <summary>The production exception verbatim: a token someone cancelled, nothing else.</summary>
    private static TaskCanceledException Cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        return new TaskCanceledException("A task was canceled.", null, cts.Token);
    }

    /// <summary>A TIMEOUT wearing the cancellation costume — how .NET reports one raised on a token.</summary>
    private static TaskCanceledException TimeoutDressedAsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        return new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            new TimeoutException("A connection could not be established within the time limit."),
            cts.Token);
    }

    /// <summary>
    /// A real <see cref="INodeValidator"/> — the framework's own extension point, not a stand-in for
    /// one — that ends the operation in a chosen way for a chosen node id and passes everything else
    /// untouched. This is how the handler's error branch is reached without reaching into it.
    /// </summary>
    private sealed class ScriptedOutcomeValidator : INodeValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } =
            [NodeOperation.Create, NodeOperation.Delete];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        {
            var id = context.Node.Path.Split('/')[^1];
            return (context.Operation, id) switch
            {
                (NodeOperation.Create, CancelOnCreate) =>
                    Observable.Throw<NodeValidationResult>(Cancellation()),
                (NodeOperation.Create, TimeoutOnCreate) =>
                    Observable.Throw<NodeValidationResult>(TimeoutDressedAsCancellation()),
                (NodeOperation.Create, FaultOnCreate) =>
                    Observable.Throw<NodeValidationResult>(
                        new NotSupportedException("the fixture's genuine fault")),
                (NodeOperation.Delete, CancelOnDelete) =>
                    Observable.Throw<NodeValidationResult>(Cancellation()),
                _ => Observable.Return(NodeValidationResult.Valid()),
            };
        }
    }

    /// <summary>One captured log entry.</summary>
    private sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception);

    /// <summary>
    /// Captures what the handlers actually logged. An INSTANCE owned by the test class (AGENTS.md:
    /// no static state) — it dies with the mesh, and every read is filtered by the node id under
    /// test, so no per-test reset is needed or wanted.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<Entry> entries = new();

        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, entries);

        /// <summary>What this category logged about one node.</summary>
        public IReadOnlyList<Entry> Entries(string category, string nodeId) =>
            entries
                .Where(e => e.Category == category && e.Message.Contains(nodeId, StringComparison.Ordinal))
                .ToImmutableList();

        /// <summary>The fail:/crit: lines — what the red-log watcher turns into tickets.</summary>
        public IReadOnlyList<Entry> Red(string category, string nodeId) =>
            Entries(category, nodeId).Where(e => e.Level >= LogLevel.Error).ToImmutableList();

        public void Dispose() { }

        private sealed class Capturing(string category, ConcurrentQueue<Entry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception));
        }
    }
}
