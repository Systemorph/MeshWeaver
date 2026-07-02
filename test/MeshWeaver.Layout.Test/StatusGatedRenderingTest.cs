using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins STATUS-GATED EMERGENCY-MODE RENDERING in <see cref="LayoutAreaHost"/> — the root fix for
/// the "renders empty / secretly-errors-as-timeout" wedge class: a host whose content is in an
/// error state previously STILL invoked the typed-content readers (<c>ContentAs&lt;T&gt;</c> /
/// <c>Content is X</c> inside the view generators) → null content → areas rendered empty or a
/// reactive wait timed out. With the gate (<see cref="LayoutDefinition.WithRenderingGate"/>,
/// carrying the <see cref="ActivityStatusExtensions"/> law):
/// <list type="bullet">
///   <item>SUCCESS status → renderers run, content renders normally.</item>
///   <item>ERROR or CANCELLED status → EMERGENCY MODE: every requested area of the host renders
///         the error as a visible control — the error IS the rendered output.</item>
///   <item>Missing configuration → emergency mode too.</item>
///   <item>An error status NEVER triggers a typed-content read (the regression pin).</item>
/// </list>
/// </summary>
public class StatusGatedRenderingTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string TypedContentArea = nameof(TypedContentArea);
    private const string SecondArea = nameof(SecondArea);

    /// <summary>
    /// The per-test status source driving the host's rendering gate. Seeded Pending (nothing
    /// renders); each test pushes its scenario's status BEFORE subscribing the area.
    /// </summary>
    private readonly BehaviorSubject<RenderingGateState> gateStates = new(RenderingGateState.Pending());

    /// <summary>
    /// Counts invocations of the typed-content-reading view generator — the probe for the
    /// "an error status never triggers a typed-content read" regression.
    /// </summary>
    private int typedContentReads;

    /// <summary>
    /// Models a node-typed view: the generator body is where <c>ContentAs&lt;T&gt;</c> /
    /// <c>Content is X</c> reads happen in production layout areas. Under the gate this must run
    /// ONLY on a SUCCESS status.
    /// </summary>
    private IObservable<UiControl?> TypedContentView(LayoutAreaHost host, RenderingContext ctx)
    {
        Interlocked.Increment(ref typedContentReads);
        return Observable.Return<UiControl?>(Controls.Html("TYPED_CONTENT"));
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithRenderingGate(_ => gateStates)
                .WithView(TypedContentArea, TypedContentView)
                .WithView(SecondArea, Controls.Html("SECOND_CONTENT")));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private ISynchronizationStream<JsonElement> GetAreaStream(string area) =>
        GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(area));

    private async Task<string> GetEmergencyMarkdown(ISynchronizationStream<JsonElement> stream, string area)
    {
        var control = await stream.GetControlStream(area)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);
        return control.Should().BeOfType<MarkdownControl>().Subject.Markdown?.ToString() ?? string.Empty;
    }

    [HubFact]
    public async Task SuccessStatus_RendersContentNormally()
    {
        gateStates.OnNext(RenderingGateState.Success());

        var stream = GetAreaStream(TypedContentArea);
        var control = await stream.GetControlStream(TypedContentArea)
            .Should().Within(10.Seconds()).Match(x => x is HtmlControl);

        var html = control.Should().BeOfType<HtmlControl>().Subject.Data.ToString() ?? string.Empty;
        html.Should().Contain("TYPED_CONTENT");
        Volatile.Read(ref typedContentReads).Should().BeGreaterThan(0,
            "a SUCCESS status is exactly the state in which the typed-content readers must run");
    }

    /// <summary>
    /// EMERGENCY MODE: an error status renders the error into EVERY requested area of the host —
    /// not just one — so no area of a failed host can hang or render empty.
    /// </summary>
    [HubFact]
    public async Task ErrorStatus_RendersTheErrorInEveryRequestedArea()
    {
        gateStates.OnNext(RenderingGateState.Failed("BOOM_status_error"));

        foreach (var area in new[] { TypedContentArea, SecondArea })
        {
            var text = await GetEmergencyMarkdown(GetAreaStream(area), area);
            text.Should().Contain("cannot be rendered",
                $"area {area} of a host in an error state must render a visible emergency frame, " +
                "never hang and never render empty");
            text.Should().Contain("BOOM_status_error",
                "the emergency frame must carry the underlying error so the cause is visible");
        }
    }

    [HubFact]
    public async Task CancelledStatus_RendersTheEmergencyFrame()
    {
        gateStates.OnNext(RenderingGateState.Cancelled("BOOM_cancelled_by_user"));

        var text = await GetEmergencyMarkdown(GetAreaStream(TypedContentArea), TypedContentArea);
        text.Should().Contain("cancelled",
            "a cancelled status is an error-class terminal status and must render emergency mode");
        text.Should().Contain("BOOM_cancelled_by_user");
    }

    [HubFact]
    public async Task MissingConfig_RendersTheEmergencyFrame()
    {
        gateStates.OnNext(RenderingGateState.NoConfig("BOOM_expected_MyConfig_content_missing"));

        var text = await GetEmergencyMarkdown(GetAreaStream(TypedContentArea), TypedContentArea);
        text.Should().Contain("no configuration is available",
            "content missing/untyped where configuration is required must render emergency mode, " +
            "not attempt a typed render against nothing");
        text.Should().Contain("BOOM_expected_MyConfig_content_missing");
    }

    /// <summary>
    /// THE regression pin: an error status must NEVER trigger a typed-content read. Before the
    /// gate, the view generators ran regardless of status, typed the un-typeable content to null,
    /// and the area rendered empty (or a downstream reactive wait timed out).
    /// </summary>
    [HubFact]
    public async Task ErrorStatus_NeverTriggersATypedContentRead()
    {
        gateStates.OnNext(RenderingGateState.Failed("BOOM_no_typed_read"));

        // The emergency frame must arrive (visible error, not a hang) ...
        var text = await GetEmergencyMarkdown(GetAreaStream(TypedContentArea), TypedContentArea);
        text.Should().Contain("BOOM_no_typed_read");

        // ... and the typed-content-reading generator must never have been invoked.
        Volatile.Read(ref typedContentReads).Should().Be(0,
            "an error status must short-circuit to emergency mode BEFORE any typed-content read — " +
            "typing a failed node's content is exactly the null-content wedge the gate exists to kill");
    }

    /// <summary>
    /// The gate is live, not terminal: when the status recovers (error → success, e.g. a recompile
    /// fixed the content), the same host switches from the emergency frame to the normal render
    /// without a resubscribe.
    /// </summary>
    [HubFact]
    public async Task ErrorThenSuccess_RecoversToTheNormalRender()
    {
        gateStates.OnNext(RenderingGateState.Failed("BOOM_initial_error"));

        var stream = GetAreaStream(TypedContentArea);
        var text = await GetEmergencyMarkdown(stream, TypedContentArea);
        text.Should().Contain("BOOM_initial_error");
        Volatile.Read(ref typedContentReads).Should().Be(0);

        gateStates.OnNext(RenderingGateState.Success());

        var control = await stream.GetControlStream(TypedContentArea)
            .Should().Within(10.Seconds()).Match(x => x is HtmlControl);
        var html = control.Should().BeOfType<HtmlControl>().Subject.Data.ToString() ?? string.Empty;
        html.Should().Contain("TYPED_CONTENT");
        Volatile.Read(ref typedContentReads).Should().BeGreaterThan(0);
    }
}
