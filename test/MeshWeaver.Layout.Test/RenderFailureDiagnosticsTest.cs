using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

namespace MeshWeaver.Layout.Test;

/// <summary>
/// What a failed layout-area render REPORTS about itself — issue #1182.
///
/// <para>Production filed an incident reading, four times over twelve hours:</para>
/// <code>
/// fail: MeshWeaver.Layout.Composition.LayoutAreaHost[0]
///       Rendering failed for area (null)
///       System.UnauthorizedAccessException: User 'carson' lacks Read permission on 'Profiles/RolandLinkedIn'
/// </code>
/// <para>Two separate defects, neither of them the access decision — the denial is CORRECT and this
/// fixture deliberately keeps it: the user genuinely lacks Read, and the gate is doing its job.</para>
/// <list type="number">
///   <item><b>The area had no name.</b> <c>LayoutAreaHost</c> logged <c>Reference.Area</c>, which is
///   <c>null</c> for every subscriber that asked for a node's DEFAULT area
///   (<c>new LayoutAreaReference(null)</c> — the shape the portal's thread / side-panel path builds).
///   The one field that says WHICH area was denied was the one field the line could not carry.</item>
///   <item><b>The severity was wrong.</b> The Blazor client already classifies this exact failure as
///   a user-action outcome and logs it at Warning (<c>NamedAreaView</c> →
///   <see cref="AreaErrorClassifier.IsExpectedUserActionFailure"/>). The server logged it at Error,
///   and Error is what the red-log filer turns into a production incident. A correct denial opened
///   a ticket.</item>
/// </list>
/// <para>Both halves are pinned below, together with the guard that matters more than either: an
/// ordinary engineering fault on the same path must STILL land at Error.</para>
/// </summary>
public class RenderFailureDiagnosticsTest : HubTestBase
{
    private const string DeniedDefaultView = nameof(DeniedDefaultView);
    private const string BrokenNamedView = nameof(BrokenNamedView);

    /// <summary>The verbatim production denial (<c>MeshNodeStreamCache.GateOnRead</c>'s banner).</summary>
    private const string DenialMessage =
        "User 'carson' lacks Read permission on 'Profiles/RolandLinkedIn'";

    private const string EngineeringFault = "BOOM_not_a_user_action";

    private readonly RenderFailureCapture capture = new();

    public RenderFailureDiagnosticsTest(ITestOutputHelper output) : base(output)
    {
        // Registered AFTER TestBase's ctor has run its ClearProviders(), so this survives alongside
        // the xUnit sink. The log line IS the artifact under test, so reading the record the host
        // emitted is the only way to assert on it without re-implementing the decision here.
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(capture));
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                // The production shape: the area that fails is the layout's DEFAULT area, so the
                // subscriber's reference carries no area name at all.
                .WithDefaultArea(DeniedDefaultView)
                .WithView(DeniedDefaultView, (LayoutAreaHost _, RenderingContext _)
                    => Observable.Throw<UiControl?>(new UnauthorizedAccessException(DenialMessage)))
                .WithView(BrokenNamedView, (LayoutAreaHost _, RenderingContext _)
                    => Observable.Throw<UiControl?>(new InvalidOperationException(EngineeringFault))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient(d => d);

    /// <summary>
    /// 🚨 The regression pin for both halves of #1182, driven through the exact production shape:
    /// a subscriber that named no area, and a render denied by access control.
    ///
    /// <para>Waiting for the rendered error control first is the barrier, not a sleep — the host
    /// writes its log line BEFORE it renders the placeholder, so a placeholder that has reached the
    /// client proves the record was already emitted.</para>
    /// </summary>
    [HubFact]
    public async Task ADeniedDefaultAreaRender_NamesTheAreaAndReportsItAsADenial()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(null));

        // The area the null reference resolves to is where the visible error must land.
        var control = await stream.GetControlStream(DeniedDefaultView)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);
        control.Should().BeOfType<MarkdownControl>()
            .Which.Markdown!.ToString().Should().Contain(DenialMessage,
                "the denial is correct and must still be surfaced to the viewer verbatim");

        var record = Records().Should().ContainSingle(
            "one denied render must produce exactly one report").Subject;

        record.Area.Should().Be(DeniedDefaultView,
            "the line must name the area that was denied. Reference.Area is null for a "
            + "default-area subscription, which is what printed 'Rendering failed for area (null)' "
            + "in production and left the denied resource untraceable");
        record.Area.Should().NotBeNull("'(null)' is what the production line actually rendered");

        record.Level.Should().Be(LogLevel.Warning,
            "an access denial is the user asking for something they may not have — the same "
            + "classifier the Blazor client uses (AreaErrorClassifier.IsExpectedUserActionFailure) "
            + "puts it at Warning. At Error it auto-files a production incident for access control "
            + "working correctly");
        record.Exception.Should().BeOfType<UnauthorizedAccessException>(
            "downgrading the level must not drop the exception — the cause and its stack stay");
    }

    /// <summary>
    /// The guard that keeps the fix honest. Downgrading denials is only safe while everything ELSE
    /// still lands at Error — otherwise #1182's fix would blind the dashboards it was meant to
    /// unclutter. A plain <see cref="InvalidOperationException"/> from a view generator is an
    /// engineering fault and must be reported as one.
    /// </summary>
    [HubFact]
    public async Task AnEngineeringFaultOnTheSamePath_IsStillReportedAtError()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(BrokenNamedView));

        await stream.GetControlStream(BrokenNamedView)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);

        var record = Records().Should().ContainSingle().Subject;
        record.Area.Should().Be(BrokenNamedView);
        record.Level.Should().Be(LogLevel.Error,
            "only user-action outcomes are downgraded; a view generator that threw is a defect and "
            + "must keep paging");
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
    /// Reads <c>LayoutAreaHost</c>'s render-failure report out of the logging pipeline. Structured
    /// state, not the formatted string: the assertions pin the VALUES the host chose (the area it
    /// named, the level it picked, the exception it kept) rather than the prose around them, which
    /// is free to be reworded.
    /// </summary>
    private sealed class RenderFailureCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<RenderFailureRecord> records = new();

        internal RenderFailureRecord[] Records => records.ToArray();

        public ILogger CreateLogger(string categoryName)
            => categoryName == typeof(LayoutAreaHost).FullName
                ? new CapturingLogger(records)
                : NullLogger.Instance;

        public void Dispose() { }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }

        private sealed class NullLogger : ILogger
        {
            internal static readonly NullLogger Instance = new();
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
                if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                    return;
                // Only the render-failure report carries an {Area} placeholder alongside an
                // exception; the host's other Warning lines (progress / apply-render) do not fault.
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
