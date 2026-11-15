using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Issue #7992 — after the mesh-lifetime readers were owned until the IoPool drain, silo stops on the
/// production portals still logged <c>IoPoolSiloTeardown: pool 'Layout' was cancelled with 2
/// subscription(s) still attached</c> (a <c>LayoutAreaHost.BuildInitialization</c> leg and a
/// <c>RenderArea</c> leg) and, at the same stop, <c>pool 'Query' was cancelled with 1 subscription(s)
/// still attached</c> — a <c>MeshQuery</c> fan-in opened at the same instant as the area's init leg
/// (both 321.0 s old), i.e. the query the area's view generator subscribed under its render.
///
/// <para><b>The defect.</b> A layout area's render subscriptions have an owner, the
/// <see cref="LayoutAreaHost"/>, but the host is disposed with its hub, and on a silo every hub lives on
/// past <c>IoPoolSiloTeardown</c>'s cancel of the pools. So every area open at a stop was reported as an
/// unowned leak on BOTH pools. This test reproduces that order on a real mesh: render an area whose view
/// is a live mesh query, dispose the registry while the area is still subscribed, and read the
/// registry's own leak report.</para>
///
/// <para><b>Not vacuous:</b> an UNOWNED control read, opened through the same mesh query, must be named by
/// the same report — so the instrument demonstrably sees pooled query legs here. <b>Negative control,
/// measured:</b> with <c>LayoutAreaHost.ScheduleRenderSubscribe</c> calling the one-argument
/// <c>SubscribeThroughPool</c> again, the report names the area's <c>BuildInitialization</c> leg —
/// <c>Defer&lt;EntityStoreAndUpdates&gt;</c>, the production line's shape — and this test fails. The
/// view's own query was NOT named in that run. The registry cancels its pools in no contracted order
/// (it enumerates a concurrent dictionary), so without the fix whether that leg is caught depends on
/// which pool goes first; in production it was caught mid-teardown, its <c>Switch</c> already disposed.
/// With the fix the render, and the query under it, is released before ANY pool is cancelled, so the
/// last assertion does not depend on that order — but it was not red in the measured negative run.</para>
/// </summary>
public class LayoutRenderReleasedBeforeThePoolCancelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PageType = "PoolCancelLayoutProbeType";
    private const string Page = "PoolCancelLayoutProbe";
    private const string Area = "Live";
    private const string Query = $"namespace:{TestPartition} scope:children";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // Named selectors, so the leak line can be asserted to name (or not name) each read.
    private static QueryResultChange<MeshNode> AreaViewRead(QueryResultChange<MeshNode> change) => change;
    private static QueryResultChange<MeshNode> UnownedControlRead(QueryResultChange<MeshNode> change) => change;

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMeshNodes(
            new MeshNode(PageType)
            {
                HubConfiguration = config => config.AddLayout(layout => layout.WithView<MarkdownControl>(Area,
                    (host, _) => host.Hub.ServiceProvider.GetRequiredService<IMeshService>()
                        .Query<MeshNode>(MeshQueryRequest.FromQuery(Query))
                        .Select(AreaViewRead)
                        .Select(change => Controls.Markdown($"live {change.Items.Count}"))))
            },
            new MeshNode(Page) { NodeType = PageType });

    [Fact]
    public async Task ASiloStop_FindsNoLiveAreaRenderStillAttached_ButStillNamesAnUnownedRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // A live area: its view is a standing mesh query, so the area's render legs stay attached.
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(Page), new LayoutAreaReference(Area));
        await stream.GetControlStream(Area).Where(c => c is MarkdownControl).Should().Within(Budget)
            .Emit("precondition: the area rendered its live view", cancellationToken: ct);

        // The control: the same kind of standing mesh query, owned by nobody.
        IDisposable control;
        using (access.ImpersonateAsSystem())
            control = meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(Query))
                .Select(UnownedControlRead)
                .Subscribe(_ => { }, _ => { });
        try
        {
            await meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(Query))
                .Take(1).Should().Within(Budget).Emit("precondition: the query answers in this mesh", cancellationToken: ct);

            // The silo's stop: IoPoolSiloTeardown disposes the registry while the mesh — and the area's
            // hub, which owns the area — lives on.
            registry.Dispose();
            await registry.Disposed.Should().Within(Budget)
                .Emit("every pool reported once its cancel returned", cancellationToken: ct);

            var leaks = registry.UnownedLeaks().SelectMany(r => r.Sites).ToArray();
            Output.WriteLine($"unowned at the cancel ({leaks.Length}):\n  " + string.Join("\n  ", leaks));

            leaks.Should().Contain(s => s.Contains(nameof(UnownedControlRead), StringComparison.Ordinal),
                "precondition: the report sees a pooled query leg nobody owns — otherwise its silence about "
                + "the area below would prove nothing");
            leaks.Should().NotContain(s => s.Contains(nameof(LayoutAreaHost), StringComparison.Ordinal),
                "a layout area's render legs are owned until the IoPool drain, so the registry released them "
                + "before cancelling the Layout pool, although the area's hub outlives it (#7992)");
            leaks.Should().NotContain(s => s.Contains(nameof(AreaViewRead), StringComparison.Ordinal),
                "the query the area's view opened under its render is torn down with that render, before the "
                + "Query pool is cancelled (#7992)");
        }
        finally
        {
            control.Dispose();
        }
    }
}
