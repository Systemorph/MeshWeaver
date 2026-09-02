using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 <b>#2876 — a render that could not reach the DATA STORE is an availability report, not a
/// broken view.</b>
///
/// <para><b>What production reported.</b> The <c>Catalog</c> area, twice inside 21 seconds on one
/// pod:</para>
/// <code>
/// fail: MeshWeaver.Layout.Composition.LayoutAreaHost[0]
///       Rendering failed for area Catalog
///       Npgsql.NpgsqlException (0x80004005): The operation has timed out
///        ---&gt; System.TimeoutException: The operation has timed out.
///          at Npgsql.Internal.NpgsqlConnector.ConnectAsync(…)
///          at Npgsql.PoolingDataSource.OpenNewConnector(…)
///          at …PostgreSqlCrossSchemaQueryProvider.GetSchemasWithTableAsync(…)
/// </code>
///
/// <para><b>Why the existing retry did not cover it.</b> It DID run. The query fan-in wraps every
/// provider observable in <c>TransientStorageFaults.RetryTransientConnect</c> (#2521, merged
/// 2026-08-28 — three days BEFORE this capture): 250 → 500 → 1000 ms of backoff, then the last
/// error surfaces. A database that is unreachable for 21 s outlives 1.75 s of budget, so the fault
/// arrived at the render exactly as designed. #2876 is not a missing retry — it is the missing
/// answer to "what does the area SHOW when the bounded retry is honestly spent".</para>
///
/// <para><b>What it showed instead.</b> The generic panel: <c>⚠️ This area failed to render.</c>
/// plus <c>ex.Message</c>, i.e. the driver's own text and the database host it could not reach,
/// rendered to an end user — and a log line naming the AREA as the thing that failed, which sends
/// whoever reads it hunting for a bug in the Catalog view. Both halves are wrong about what
/// happened.</para>
///
/// <para><b>What the fix is NOT.</b> No retry on the render path (the fan-in's is spent; a second
/// one would be an unbounded resubscribe aimed at the resource that is already the bottleneck),
/// no swallow, and no log downgrade — an availability failure stays at Error so an operator sees
/// the outage, the same argument #974 makes. What changes is the CLASSIFICATION: a named frame
/// (<see cref="AreaFrameClassifier.StorageUnavailableId"/>) saying the store is temporarily
/// unavailable, and a log line that names the store rather than the area.</para>
/// </summary>
public class StorageUnavailableRenderTest : HubTestBase
{
    private const string StorageBackedView = nameof(StorageBackedView);
    private const string BrokenView = nameof(BrokenView);

    /// <summary>The database host in the production capture — it must never reach a viewer.</summary>
    private const string DatabaseHost = "10.42.18.4:5432";

    private const string EngineeringFault = "BOOM_an_ordinary_defect";

    private readonly RenderFailureCapture capture = new();

    public StorageUnavailableRenderTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(capture));
    }

    /// <summary>
    /// Stand-in for a driver exception — <c>NpgsqlException</c> derives from the BCL
    /// <see cref="DbException"/>, and core never references the driver, so neither does this test.
    /// </summary>
    private sealed class FakeDbException(string message, Exception? inner = null, string? sqlState = null)
        : DbException(message, inner)
    {
        public override string? SqlState { get; } = sqlState;
    }

    /// <summary>The #2876 shape: a connector open that timed out reaching the server.</summary>
    private static Exception ConnectTimeout() =>
        new FakeDbException($"Failed to connect to {DatabaseHost}",
            new TimeoutException("The operation has timed out."));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(StorageBackedView, (LayoutAreaHost _, RenderingContext _)
                    => Observable.Throw<UiControl?>(ConnectTimeout()))
                .WithView(BrokenView, (LayoutAreaHost _, RenderingContext _)
                    => Observable.Throw<UiControl?>(new InvalidOperationException(EngineeringFault))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient(d => d);

    /// <summary>
    /// The area serves the NAMED storage-unavailable frame, the driver's diagnostic never reaches
    /// the viewer, and the operator still gets an Error line — the three things #2876 asks for.
    ///
    /// <para>Waiting for the rendered control is the barrier, not a sleep: the host writes its log
    /// line BEFORE it renders the frame, so a frame that has reached the client proves the record
    /// has already been emitted.</para>
    /// </summary>
    [HubFact]
    public async Task AStoreThatCouldNotBeReached_ServesTheNamedFrame_AndStillPagesTheOperator()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(StorageBackedView));

        var control = await stream.GetControlStream(StorageBackedView)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);

        AreaFrameClassifier.IsStorageUnavailable(control as UiControl).Should().BeTrue(
            "the frame must carry the well-known id so a consumer can tell 'the store did not "
            + "answer' from the four states AreaFrameClassifier already distinguishes — the id "
            + "round-trips through the sync stream, the localized prose does not");
        AreaFrameClassifier.IsTransientFrame(control as UiControl).Should().BeFalse(
            "IsTransientFrame promises 'this WILL be replaced without anyone acting'. Nothing "
            + "fires when the database comes back, so a waiter that treated this as transient "
            + "would wait forever");
        AreaFrameClassifier.IsAreaNotFound(control as UiControl).Should().BeFalse(
            "the area exists and renders fine — it is the store that did not answer");
        AreaFrameClassifier.IsMissingReference(control as UiControl).Should().BeFalse(
            "nothing is wrong with the content this area points at either");

        var text = (control as MarkdownControl)?.Markdown?.ToString() ?? string.Empty;
        Output.WriteLine($"frame: {text}");
        text.Should().NotContain(DatabaseHost,
            "the generic panel embedded ex.Message verbatim, so the database host the pod could "
            + "not reach was rendered to an end user");
        text.Should().NotContain("Failed to connect",
            "the driver's own diagnostic is internal — it must not reach an end user");

        var record = Records().Should().ContainSingle().Subject;
        record.Area.Should().Be(StorageBackedView);
        record.Level.Should().Be(LogLevel.Error,
            "an availability failure must stay visible to operators (#974) — the fan-in's bounded "
            + "transient-connect retry is already spent by the time this line is written, so it "
            + "reports a database that stayed unreachable, not a blip");
    }

    /// <summary>
    /// The guard that keeps the fix honest: everything ELSE must still report as a failure, with
    /// its message intact. A classification that swallowed ordinary faults into "come back later"
    /// would be a far worse defect than the one being fixed — and it is what makes the assertions
    /// above non-vacuous.
    /// </summary>
    [HubFact]
    public async Task AnOrdinaryRenderFault_KeepsTheGenericPanel_AndItsMessage()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(BrokenView));

        var control = await stream.GetControlStream(BrokenView)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);

        AreaFrameClassifier.IsStorageUnavailable(control as UiControl).Should().BeFalse(
            "a defect in a view is not an outage — dressing it up as one hides it");
        ((control as MarkdownControl)?.Markdown?.ToString() ?? string.Empty)
            .Should().Contain(EngineeringFault,
                "the generic panel keeps the exception message so the cause stays visible");

        var record = Records().Should().ContainSingle().Subject;
        record.Area.Should().Be(BrokenView);
        record.Level.Should().Be(LogLevel.Error);
    }

    /// <summary>
    /// The classification itself, stated as an executable fact — the half that decides both
    /// behaviours above, and the half that must not widen.
    /// </summary>
    [Fact]
    public void TheClassifier_MatchesTheConnectClass_AndNothingElse()
    {
        AreaErrorClassifier.IsStorageUnavailable(ConnectTimeout()).Should().BeTrue(
            "the #2876 shape: a driver exception wrapping a connect timeout");
        AreaErrorClassifier.IsStorageUnavailable(
                new FakeDbException("connect", new SocketException()))
            .Should().BeTrue("…and its network-level variants");
        AreaErrorClassifier.IsStorageUnavailable(
                new InvalidOperationException("query failed", ConnectTimeout()))
            .Should().BeTrue("providers re-wrap driver faults, so the walk must go inner");
        AreaErrorClassifier.IsStorageUnavailable(
                new FakeDbException("the server is not accepting connections", sqlState: "57P03"))
            .Should().BeTrue("a server-side connection-class SQLSTATE is the same condition");

        AreaErrorClassifier.IsStorageUnavailable(
                new FakeDbException("undefined_table", sqlState: "42P01"))
            .Should().BeFalse(
                "a real schema error IS a defect — telling the viewer to come back later would "
                + "hide it, and it would never come back");
        AreaErrorClassifier.IsStorageUnavailable(new TimeoutException()).Should().BeFalse(
            "a timeout with no database exception in the chain is a HUB timeout, which has its "
            + "own policy (IsTransientHubFailure) and its own frame");
        AreaErrorClassifier.IsStorageUnavailable(new InvalidOperationException(EngineeringFault))
            .Should().BeFalse();
        AreaErrorClassifier.IsStorageUnavailable(null).Should().BeFalse();
    }

    private RenderFailureRecord[] Records()
    {
        var all = capture.Records;
        foreach (var record in all)
            Output.WriteLine($"LayoutAreaHost captured: {record}");
        return all;
    }

    private sealed record RenderFailureRecord(LogLevel Level, string? Area, Exception? Exception);

    /// <summary>
    /// Reads <c>LayoutAreaHost</c>'s render-failure report out of the logging pipeline, at Warning
    /// and above — i.e. exactly the levels that reach an error dashboard. Structured state, never
    /// the formatted prose.
    /// </summary>
    private sealed class RenderFailureCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<RenderFailureRecord> records = new();

        internal RenderFailureRecord[] Records => records.ToArray();

        public ILogger CreateLogger(string categoryName)
            => categoryName == typeof(LayoutAreaHost).FullName
                ? new CapturingLogger(records)
                : Silent.Instance;

        public void Dispose() { }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }

        private sealed class Silent : ILogger
        {
            internal static readonly Silent Instance = new();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => false;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) { }
        }

        private sealed class CapturingLogger(ConcurrentQueue<RenderFailureRecord> sink) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning)
                    return;
                if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                    return;
                if (exception is null)
                    return;
                var area = values.FirstOrDefault(v => v.Key == "Area");
                if (area.Key is null)
                    return;
                sink.Enqueue(new RenderFailureRecord(logLevel, area.Value?.ToString(), exception));
            }
        }
    }
}
