using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// A fed stream that FAULTS behind <see cref="Template.Bind{T,TView}(IObservable{T}, System.Linq.Expressions.Expression{Func{T,TView}}?, string?)"/>
/// or <see cref="Template.BindMany{T,TView}(IObservable{IEnumerable{T}}, string, System.Linq.Expressions.Expression{Func{T,TView}})"/>.
///
/// <para>Both used to subscribe with <c>stream.Subscribe(onNext)</c> and NO error arm, so a fault
/// reached Rx's default <c>OnError</c>, which RETHROWS on the thread that delivered it. Off the
/// thread pool that is an unhandled exception — the shape that took memex-cloud replicas down in
/// #5650 (a <c>QueryProviderStalledException</c>). Every view converted in the data-binding wave
/// ("Templates first, data later") puts its query behind exactly this subscription.</para>
///
/// <para>The producer's <c>OnError</c> call is where the rethrow lands, so "the process survives" is
/// measured directly: the call must RETURN. Before the fix it threw the fault back at the producer
/// (the negative control below was run against the unfixed <c>Template.cs</c> and failed there).</para>
/// </summary>
public class BindFeedFaultTest : HubTestBase
{
    private const string LateFaultBind = nameof(LateFaultBind);
    private const string LateFaultBindMany = nameof(LateFaultBindMany);
    private const string ImmediateFaultBind = nameof(ImmediateFaultBind);
    private const string HealthyBind = nameof(HealthyBind);
    private const string HostFeedView = nameof(HostFeedView);
    private const string HostFeedData = "hostFeedData";
    private const string ImmediateHostFeedView = nameof(ImmediateHostFeedView);
    private const string ImmediateHostFeedData = "immediateHostFeedData";
    private const string Bound = nameof(Bound);
    private const string Sibling = nameof(Sibling);

    private const string LateFaultData = "lateFaultData";
    private const string LateFaultManyData = "lateFaultManyData";
    private const string ImmediateFaultData = "immediateFaultData";
    private const string HealthyData = "healthyData";

    private const string StallMessage = "query provider stalled — QueryProviderStalledException shape";
    private const string ImmediateMessage = "the feed faulted while it was being subscribed";

    /// <summary>A record bound by property, as every converted view binds its row.</summary>
    public record FeedRow(string Name);

    private readonly BehaviorSubject<FeedRow> lateFeed = new(new FeedRow("first"));
    private readonly BehaviorSubject<IEnumerable<FeedRow>> lateManyFeed =
        new([new FeedRow("a"), new FeedRow("b")]);
    private readonly BehaviorSubject<FeedRow> hostFeed = new(new FeedRow("host"));
    private readonly FeedFaultCapture capture = new();

    public BindFeedFaultTest(ITestOutputHelper output) : base(output)
    {
        // After TestBase's ClearProviders(): the report IS part of what is asserted.
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(capture));
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(LateFaultBind, Controls.Stack
                    .WithView(Controls.Html("still here"), Sibling)
                    .WithView(lateFeed.Bind(r => Controls.Text(r.Name), LateFaultData), Bound))
                .WithView(LateFaultBindMany, Controls.Stack
                    .WithView(lateManyFeed.BindMany(LateFaultManyData, r => Controls.Text(r.Name)), Bound))
                .WithView(ImmediateFaultBind, Controls.Stack
                    .WithView(Observable.Throw<FeedRow>(new InvalidOperationException(ImmediateMessage))
                        .Bind(r => Controls.Text(r.Name), ImmediateFaultData), Bound))
                .WithView(HostFeedView, (LayoutAreaHost host, RenderingContext _) =>
                {
                    host.SubscribeToDataStream(HostFeedData, hostFeed);
                    return Observable.Return<UiControl?>(Controls.Html("fed by the host"));
                })
                .WithView(ImmediateHostFeedView, (LayoutAreaHost host, RenderingContext _) =>
                {
                    // The shape EditLayoutArea relies on: a projection that throws as soon as the
                    // feed is subscribed, from inside a view builder.
                    host.SubscribeToDataStream(ImmediateHostFeedData,
                        Observable.Return(new FeedRow("x"))
                            .Select<FeedRow, FeedRow>(_ => throw new InvalidOperationException(ImmediateMessage)));
                    return Observable.Return<UiControl?>(Controls.Html("never shown"));
                })
                .WithView(HealthyBind, Controls.Stack
                    .WithView(Observable.Return(new FeedRow("healthy"))
                        .Bind(r => Controls.Text(r.Name), HealthyData), Bound)));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    /// <summary>
    /// 🚨 The production shape: the feed is live and bound, then faults LATER, from the producer's
    /// own thread. The producer's OnError must return (no rethrow), the bound control must turn
    /// into the localized error frame carrying the cause, a sibling must be untouched, and the
    /// fault must be reported at Error naming the area and the data id.
    /// </summary>
    [HubFact]
    public async Task ALateFaultInABoundFeed_DoesNotEscape_AndTheBoundAreaShowsTheError()
    {
        var stream = Subscribe(LateFaultBind);
        var boundArea = $"{LateFaultBind}/{Bound}";

        await stream.GetControlStream(boundArea)
            .Should().Within(10.Seconds()).Match(x => x is not null and not MarkdownControl);

        var fault = new InvalidOperationException(StallMessage);
        var produce = () => lateFeed.OnError(fault);
        produce.Should().NotThrow(
            "a faulting fed stream must never rethrow at its producer — off the pool that is an "
            + "unhandled exception and the process dies (#5650)");

        var error = await stream.GetControlStream(boundArea)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);
        Text(error).Should().Contain(StallMessage, "the viewer sees the cause in the area's error frame");

        var sibling = await stream.GetControlStream($"{LateFaultBind}/{Sibling}")
            .Should().Within(10.Seconds()).Match(x => x is not null);
        sibling.Should().BeOfType<HtmlControl>("only the bound control fails; its sibling keeps rendering");

        var record = capture.Records.Should().ContainSingle(r => r.DataId == LateFaultData).Subject;
        record.Level.Should().Be(LogLevel.Error, "a stalled query is an engineering fault and must page");
        record.Area.Should().Be(boundArea);
        record.Exception.Should().BeSameAs(fault);
    }

    /// <summary>The same contract for the collection overload, <c>BindMany</c>.</summary>
    [HubFact]
    public async Task ALateFaultInABindManyFeed_DoesNotEscape_AndTheBoundAreaShowsTheError()
    {
        var stream = Subscribe(LateFaultBindMany);
        var boundArea = $"{LateFaultBindMany}/{Bound}";

        await stream.GetControlStream(boundArea)
            .Should().Within(10.Seconds()).Match(x => x is ItemTemplateControl);

        var fault = new InvalidOperationException(StallMessage);
        var produce = () => lateManyFeed.OnError(fault);
        produce.Should().NotThrow("BindMany had the same missing error arm as Bind");

        var error = await stream.GetControlStream(boundArea)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);
        Text(error).Should().Contain(StallMessage);
        capture.Records.Should().ContainSingle(r => r.DataId == LateFaultManyData)
            .Which.Level.Should().Be(LogLevel.Error);
    }

    /// <summary>
    /// <see cref="LayoutAreaHost.SubscribeToDataStream{T}"/> — the public feed API views use
    /// (Northwind's year toolbar, the NodeType pages) — had the same bare Subscribe. Its fault is
    /// shown on the host's own area.
    /// </summary>
    [HubFact]
    public async Task ALateFaultInAHostDataStream_DoesNotEscape_AndTheHostAreaShowsTheError()
    {
        var stream = Subscribe(HostFeedView);
        await stream.GetControlStream(HostFeedView)
            .Should().Within(10.Seconds()).Match(x => x is HtmlControl);

        var fault = new InvalidOperationException(StallMessage);
        var produce = () => hostFeed.OnError(fault);
        produce.Should().NotThrow("SubscribeToDataStream had the same missing error arm");

        var error = await stream.GetControlStream(HostFeedView)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);
        Text(error).Should().Contain(StallMessage);
        capture.Records.Should().ContainSingle(r => r.DataId == HostFeedData)
            .Which.Level.Should().Be(LogLevel.Error);
    }

    /// <summary>
    /// <c>FeedData</c>'s other arm: a feed that faults while a VIEW BUILDER subscribes it is
    /// rethrown into that builder, so the existing area-error path renders it — the view's own
    /// control never replaces the error.
    /// </summary>
    [HubFact]
    public async Task AHostFeedThatFaultsWhileBeingSubscribed_FailsTheViewThroughTheAreaErrorPath()
    {
        var stream = Subscribe(ImmediateHostFeedView);

        var error = await stream.GetControlStream(ImmediateHostFeedView)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);
        Text(error).Should().Contain(ImmediateMessage);
    }

    /// <summary>
    /// A feed that faults DURING Subscribe (a cold stream that throws at once) is rendered into the
    /// store the buildup is building, so the render's own result carries the error frame instead of
    /// overwriting it.
    /// </summary>
    [HubFact]
    public async Task AFeedThatFaultsWhileBeingSubscribed_RendersTheErrorInsteadOfTheTemplate()
    {
        var stream = Subscribe(ImmediateFaultBind);

        var error = await stream.GetControlStream($"{ImmediateFaultBind}/{Bound}")
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);
        Text(error).Should().Contain(ImmediateMessage);
        capture.Records.Should().ContainSingle(r => r.DataId == ImmediateFaultData)
            .Which.Level.Should().Be(LogLevel.Error);
    }

    /// <summary>
    /// Negative control: a feed that does NOT fault renders its template and binds its value, and
    /// reports nothing. Without it, a fix that showed the error frame for every bound feed would
    /// pass the three tests above.
    /// </summary>
    [HubFact]
    public async Task AHealthyFeed_RendersTheTemplate_AndReportsNothing()
    {
        var stream = Subscribe(HealthyBind);

        var control = await stream.GetControlStream($"{HealthyBind}/{Bound}")
            .Should().Within(10.Seconds()).Match(x => x is not null);
        control.Should().NotBeOfType<MarkdownControl>("a healthy feed shows its template, not an error");

        var value = await stream
            .GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(HealthyData)))
            .Should().Within(10.Seconds()).Match(x => x.ValueKind == JsonValueKind.Object);
        value.GetProperty("name").GetString().Should().Be("healthy");

        capture.Records.Should().NotContain(r => r.DataId == HealthyData);
    }

    private ISynchronizationStream<JsonElement> Subscribe(string area)
        => GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(area));

    private static string Text(UiControl? control)
        => control.Should().BeOfType<MarkdownControl>().Subject.Markdown?.ToString() ?? string.Empty;

    private sealed record FeedFaultRecord(LogLevel Level, string? Area, string? DataId, Exception? Exception);

    /// <summary>Reads <c>LayoutAreaHost</c>'s data-feed fault report as structured state.</summary>
    private sealed class FeedFaultCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<FeedFaultRecord> records = new();

        internal FeedFaultRecord[] Records => records.ToArray();

        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(categoryName == typeof(LayoutAreaHost).FullName ? records : null);

        public void Dispose() { }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }

        private sealed class CapturingLogger(ConcurrentQueue<FeedFaultRecord>? sink) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => sink is not null && logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (sink is null || exception is null
                    || state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                    return;
                var dataId = values.FirstOrDefault(v => v.Key == "DataId");
                if (dataId.Key is null)
                    return;
                var area = values.FirstOrDefault(v => v.Key == "Area");
                sink.Enqueue(new FeedFaultRecord(logLevel, area.Value?.ToString(), dataId.Value?.ToString(), exception));
            }
        }
    }
}
