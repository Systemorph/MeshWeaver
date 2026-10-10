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
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// #6394 - a render whose OWNER did not answer in time is a DEADLINE MISS, not a broken view.
///
/// <para><b>What production reported.</b> The <c>Overview</c> area, once, on one portal pod:</para>
/// <code>
/// fail: MeshWeaver.Layout.Composition.LayoutAreaHost[0]
///       Rendering failed for area Overview
///       MeshWeaver.Messaging.DeliveryFailureException: Delivery to 'TelefonicaSeguros' failed:
///       Grain placement operation timed out for grain messagehub/TelefonicaSeguros.
/// </code>
///
/// <para><b>Why that line was wrong.</b> <see cref="AreaErrorClassifier.IsDeadlineMiss"/> already
/// classifies exactly this text as a deadline miss - the owner was SLOW, not broken - but only the
/// client's view path consulted it. The server-side render error path fell through to its generic arm:
/// an Error naming the AREA as what failed (one auto-filed incident per slow owner), and the generic
/// panel carrying the raw Orleans banner to the viewer.</para>
///
/// <para><b>What the fix is NOT.</b> No retry on the render path (an unpaced resubscribe aimed at an
/// owner that is already slow is the storm shape), and no swallow: the area still resolves to a visible
/// frame and the fault is still logged, with its text, at Warning - the placement stall itself is
/// reported where it happens (#5037). Every OTHER delivery failure keeps the generic panel and its Error
/// line, which is what the second test pins.</para>
/// </summary>
public class DeadlineMissRenderTest : HubTestBase
{
    private const string SlowOwnerView = nameof(SlowOwnerView);
    private const string FailedDeliveryView = nameof(FailedDeliveryView);

    /// <summary>The production text, verbatim - it must never reach a viewer.</summary>
    private const string PlacementTimeout =
        "Delivery to 'TelefonicaSeguros' failed: Grain placement operation timed out for grain messagehub/TelefonicaSeguros.";

    /// <summary>A delivery failure that is NOT a deadline miss - an ordinary defect.</summary>
    private const string OrdinaryDeliveryFault = "Delivery to 'Store' failed: BOOM_an_ordinary_delivery_fault";

    private readonly RenderFailureCapture capture = new();

    public DeadlineMissRenderTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(capture));
    }

    /// <summary>The shape the router hands back: a DeliveryFailure whose text is Orleans'.</summary>
    private static DeliveryFailureException FailedDelivery(string message)
        => new DeliveryFailureException(new DeliveryFailure((IMessageDelivery)null!, message));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(SlowOwnerView, (LayoutAreaHost _, RenderingContext _)
                    => Observable.Throw<UiControl?>(FailedDelivery(PlacementTimeout)))
                .WithView(FailedDeliveryView, (LayoutAreaHost _, RenderingContext _)
                    => Observable.Throw<UiControl?>(FailedDelivery(OrdinaryDeliveryFault))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient(d => d);

    /// <summary>
    /// The area serves the NAMED deadline-miss frame, the Orleans banner never reaches the viewer, and
    /// the fault is reported once, at Warning, as a deadline miss - not as "Rendering failed" at Error.
    /// On main this fails three ways: the generic panel, the banner in it, and the Error line.
    ///
    /// <para>Waiting for the rendered control is the barrier, not a sleep: the host writes its log
    /// line BEFORE it renders the frame, so a frame that has reached the client proves the record has
    /// already been emitted.</para>
    /// </summary>
    [HubFact]
    public async Task APlacementTimeout_ServesTheNamedDeadlineMissFrame_WithoutTheOrleansBanner()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(SlowOwnerView));

        var control = await stream.GetControlStream(SlowOwnerView)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);

        AreaDeadlineMissFrame.Is(control as UiControl).Should().BeTrue(
            "the frame must carry the well-known id so a consumer can tell 'the owner did not answer in "
            + "time' from every other state - the id round-trips through the sync stream, the localized "
            + "prose does not");
        AreaFrameClassifier.IsTransientFrame(control as UiControl).Should().BeFalse(
            "nothing pushes a replacement when a slow owner catches up, so a waiter that treated this "
            + "as transient would wait forever");
        AreaFrameClassifier.IsStorageUnavailable(control as UiControl).Should().BeFalse(
            "the data store answered - it is the owner that did not");
        AreaFrameClassifier.IsAreaNotFound(control as UiControl).Should().BeFalse(
            "the area exists");
        AreaFrameClassifier.IsMissingReference(control as UiControl).Should().BeFalse(
            "nothing is wrong with the content this area points at");

        var text = (control as MarkdownControl)?.Markdown?.ToString() ?? string.Empty;
        Output.WriteLine($"frame: {text}");
        text.Should().NotContain("Grain placement operation timed out",
            "the generic panel embedded ex.Message verbatim, so Orleans' own diagnostic reached the viewer");
        text.Should().NotContain("messagehub/TelefonicaSeguros",
            "the grain identity is framework-internal and must not reach an end user");
        text.Should().NotContain("Delivery to",
            "the routed NACK's wording is framework-internal and must not reach an end user");

        var record = Records().Should().ContainSingle().Subject;
        record.Area.Should().Be(SlowOwnerView);
        record.Level.Should().Be(LogLevel.Warning,
            "a slow owner is not a fault in this view: at Error it filed one incident per occurrence "
            + "naming the area, while the stall itself is reported where it happens (#5037)");
        (record.Exception is DeliveryFailureException).Should().BeTrue(
            "the fault is still logged with the exception - classified, never swallowed");
    }

    /// <summary>
    /// The guard that keeps the fix honest: a delivery failure that is NOT a deadline miss still
    /// reports as a failure, at Error, with its message on the generic panel. A classification that
    /// swallowed ordinary delivery faults into "re-open the view" would hide real defects - and this is
    /// what makes the assertions above non-vacuous.
    /// </summary>
    [HubFact]
    public async Task AnOrdinaryDeliveryFailure_KeepsTheGenericPanel_AndItsMessage()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(FailedDeliveryView));

        var control = await stream.GetControlStream(FailedDeliveryView)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);

        AreaDeadlineMissFrame.Is(control as UiControl).Should().BeFalse(
            "an ordinary delivery failure is not a deadline miss - dressing it up as one hides it");
        ((control as MarkdownControl)?.Markdown?.ToString() ?? string.Empty)
            .Should().Contain("BOOM_an_ordinary_delivery_fault",
                "the generic panel keeps the exception message so the cause stays visible");

        var record = Records().Should().ContainSingle().Subject;
        record.Area.Should().Be(FailedDeliveryView);
        record.Level.Should().Be(LogLevel.Error);
    }

    /// <summary>
    /// The classification that decides both behaviours above, stated as an executable fact - the half
    /// that must not widen.
    /// </summary>
    [Fact]
    public void TheDeadlineMissClassification_MatchesThePlacementTimeout_AndNotAnOrdinaryDeliveryFault()
    {
        AreaErrorClassifier.IsDeadlineMiss(FailedDelivery(PlacementTimeout)).Should().BeTrue(
            "the #6394 shape: a routed placement timeout is the owner missing its deadline");
        AreaErrorClassifier.IsDeadlineMiss(FailedDelivery(OrdinaryDeliveryFault)).Should().BeFalse(
            "a delivery failure without a timeout is not a deadline miss");
        AreaErrorClassifier.IsStorageUnavailable(FailedDelivery(PlacementTimeout)).Should().BeFalse(
            "a placement timeout is the owner's, not the data store's");
        AreaDeadlineMissFrame.Is(null).Should().BeFalse();
        AreaDeadlineMissFrame.Is(new MarkdownControl("not the frame")).Should().BeFalse();
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
    /// Reads <c>LayoutAreaHost</c>'s render-failure report out of the logging pipeline, at Warning and
    /// above - exactly the levels that reach an error dashboard. Structured state, never the formatted
    /// prose.
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
