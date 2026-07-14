using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 REGRESSION GUARD FOR THE QUERY-IN-RENDER DEADLOCK THAT TOOK DOWN PRODUCTION MESHES.
///
/// <para><b>The trap.</b> A layout area's view generator subscribes, IN-RENDER, to an observable
/// that performs a MESH round-trip — most importantly <c>IMeshService.Query(...)</c>, but equally
/// <c>hub.Observe(...)</c>, <c>GetRemoteStream</c>, or any workspace query. The framework's render
/// pipeline (<see cref="LayoutAreaHost"/>'s init subscription) SUBSCRIBES to whatever the generator
/// returns — and, before the fix, it did so ON the layout-area's own synchronization-stream hub
/// action block: a single-threaded actor (<c>MaxDegreeOfParallelism = 1</c>). For a node hosted as
/// an Orleans grain that hub is grain-affined. If the SUBSCRIBE occupies the hub turn until a mesh
/// round-trip completes — and that round-trip must be routed / answered BY THAT SAME HUB — the turn
/// is held inside the subscribe and the round-trip can never make progress → the hub DEADLOCKS. On
/// startup prerender many area hubs block at once → thread-pool starvation → the whole silo wedges
/// (even /healthz). Confirmed offenders: <c>Doc/DataMesh/SocialMedia/Post</c> (List area) and
/// <c>Doc/DataMesh/PythonPandasNode/PandasExplorer</c> — each queries in-render.</para>
///
/// <para><b>The framework-level fix</b> (not a per-node authoring workaround): the render
/// subscription in <see cref="LayoutAreaHost"/>.<c>BuildInitialization</c> is offloaded OFF the
/// owning hub's action block with <c>.SubscribeOn(TaskPoolScheduler.Default)</c> — the framework's
/// own designated reactive off-hub move (the same one <c>MeshQuery.Query</c> and
/// <c>IMeshNodeStreamCache.GetQuery</c> already make). The generator, and every observable it
/// subscribes in-render, now runs OFF the hub turn, which is immediately free to route and answer the
/// round-trip. This makes EVERY layout area query-in-render-safe at ONE seam, with no recompile of
/// any deployed node's source. See <c>Doc/Architecture/OrleansTaskScheduler</c> and
/// <c>Doc/Architecture/AsynchronousCalls</c>.</para>
///
/// <para><b>How this test reproduces the deadlock without a DB.</b> A layout area's generator returns
/// an observable that, ON SUBSCRIBE, does a mesh round-trip that the layout-area's OWN
/// synchronization-stream hub must answer, and does not return control from the subscribe until that
/// round-trip resolves — the shape a naive query-in-render takes when the layout-seam offload is
/// absent (the round-trip's completion is gated behind the very turn that is subscribing). If the
/// render pipeline subscribes this on the hub turn, the round-trip's response can never be dequeued
/// and the subscribe never returns → the area never renders. The layout-seam offload is the ONLY
/// thing that moves this subscribe off the hub turn; the observable itself carries no scheduler hop.
/// Before the fix the render subscribe wedges the stream-hub turn and the control never lands (the
/// <c>.Should().Within(20.Seconds())</c> deadline FAILS the test — a DEBUG <c>HubFact</c> applies no
/// timeout, so the deadline is the real guard). After the fix the subscribe is off the hub turn, the
/// round-trip is answered on the free turn, and the control lands within the deadline.</para>
/// </summary>
public class QueryInRenderDeadlockTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string QueryInRenderArea = nameof(QueryInRenderArea);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout.WithView(QueryInRenderArea, QueryInRender));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    /// <summary>
    /// The offending pattern, distilled: a view generator whose observable, ON SUBSCRIBE, performs a
    /// mesh round-trip the layout area's OWN (single-threaded) synchronization-stream hub must answer,
    /// and blocks the subscribing thread until it resolves. This is the deadlock topology of
    /// <c>meshService.Query(...)</c> reached in-render when the layout-seam offload is missing: the
    /// query routes to a hub that is, at that instant, the very hub blocked inside this subscribe. A
    /// self-directed <c>PingRequest</c> stands in for the query so the test needs no DB. The
    /// bounded-wait is the diagnostic knob: if the subscribe runs on the stream-hub turn, the ping's
    /// response can never be dequeued and the wait times out to a null control (the area never
    /// renders); if it runs OFF the turn (the fix), the ping is answered and the control emits.
    /// </summary>
    private static IObservable<UiControl?> QueryInRender(LayoutAreaHost host, RenderingContext _)
        => Observable.Create<UiControl?>(observer =>
        {
            // A round-trip the layout area's OWN stream hub must answer (every hub answers PingRequest
            // on its own action block). If THIS subscribe is running on that hub's turn, the ping sits
            // in the inbox behind us and cannot be dequeued until we return — but we are (below)
            // waiting for it: the deadlock. Off the turn (the fix), the hub is free to answer it.
            var pong = new ManualResetEventSlim(false);
            using var sub = host.Stream.Hub
                .Observe(new PingRequest(), o => o.WithTarget(host.Stream.Hub.Address))
                .Take(1)
                .Subscribe(_ => pong.Set());

            // Blocks the SUBSCRIBING thread until the round-trip resolves — the sync-over-mesh shape a
            // query-in-render collapses to when it is on the wrong scheduler. Bounded so a genuinely
            // wedged hub surfaces as "area never rendered" (null control → the .Within deadline fails
            // the test) instead of hanging the whole run.
            var answered = pong.Wait(TimeSpan.FromSeconds(15));

            observer.OnNext(answered
                ? (UiControl?)Controls.Markdown("QUERY_IN_RENDER_RESOLVED")
                : (UiControl?)Controls.Markdown("QUERY_IN_RENDER_WEDGED"));
            observer.OnCompleted();
            return System.Reactive.Disposables.Disposable.Empty;
        });

    /// <summary>
    /// Before the layout-seam fix, the render subscribe holds the layout-area stream hub's
    /// single-threaded action block while the generator waits on a round-trip that same hub must
    /// answer — the ping never resolves, the generator emits the WEDGED marker (or the whole render
    /// stalls), and the <c>QUERY_IN_RENDER_RESOLVED</c> control never lands → the
    /// <c>.Should().Within(20.Seconds())</c> deadline fails the test. After the fix the render
    /// pipeline subscribes OFF the hub turn, the hub answers the ping on its free turn, and the
    /// RESOLVED control lands within the deadline.
    /// </summary>
    [HubFact]
    public async Task QueryInRender_DoesNotDeadlockTheHub_AndRenders()
    {
        var reference = new LayoutAreaReference(QueryInRenderArea);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);

        var control = await stream.GetControlStream(reference.Area!)
            .Where(c => c is MarkdownControl m
                        && (m.Markdown?.ToString() ?? string.Empty).Contains("QUERY_IN_RENDER_RESOLVED"))
            .Should().Within(20.Seconds())
            .Match(c => c is MarkdownControl);

        control.Should().NotBeNull(
            "a layout area that does an in-render mesh round-trip must render — the render " +
            "subscription must be OFF the owning hub's action block, never blocking it");
    }
}
