using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the root cause of the endemic teardown SIGSEGV (<c>MeshWeaver.FutuRe.Test</c> exit=139,
/// issue #613): a layout-area render subscribe hopped off the owning hub's action block via a BARE
/// <c>SubscribeOn(TaskPoolScheduler.Default)</c> — a ThreadPool schedule mesh teardown cannot see.
/// During teardown that render kept executing on a ThreadPool thread AFTER the hub's Autofac
/// <c>LifetimeScope</c> was disposed (→ <c>ObjectDisposedException</c> from a menu renderer's
/// <c>GetService</c> — captured verbatim by <c>TeardownStragglerCapturer</c>) and, for a node whose
/// render touched types compiled into a collectible <c>AssemblyLoadContext</c>, AFTER that ALC was
/// unloaded (→ native use-after-unload crash).
///
/// <para>The fix routes every layout render subscribe through <c>IPooledSubscribeScheduler</c> — the
/// mesh's teardown-drainable <see cref="IoPoolNames.Layout"/> pool — so
/// <see cref="IoPoolRegistry.DrainAll"/> cancel+<b>joins</b> the in-flight subscribe BEFORE the
/// service scope disposes / the ALC unloads. This test forces a render to block mid-subscribe and
/// asserts BOTH halves of that contract deterministically:</para>
/// <list type="number">
///   <item>the render runs as a TRACKED leaf on the Layout pool
///     (<see cref="IIoPool.CurrentInFlight"/> ≥ 1 while it executes) — pre-fix it ran on a bare
///     ThreadPool thread, so the Layout pool reported ZERO in-flight; and</item>
///   <item><see cref="IoPoolRegistry.DrainAll"/> BLOCKS (joins) until the in-flight render releases —
///     pre-fix it returned immediately because nothing tracked the render, which is precisely how
///     the scope could dispose / the ALC unload underneath a still-running render.</item>
/// </list>
/// Both assertions FAIL on the pre-fix code and PASS after. (We assert the drainable-pool contract
/// rather than actually unloading an ALC mid-render, because faithfully reproducing the crash would
/// SIGSEGV the test host — the tracking+join IS the property that prevents it.)
/// </summary>
public class LayoutRenderTeardownDrainTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string BlockingArea = nameof(BlockingArea);
    private static readonly Address HostAddress = new("layout-render-drain-host");

    // Signalled by the render body once it is executing on the pool thread (holding a Layout-pool
    // permit); the test proceeds only after the render is genuinely in-flight — no sleeps, no polling.
    private readonly ManualResetEventSlim renderInFlight = new(false);
    // Held by the render body until the test releases it, keeping the render subscribe in-flight for
    // the duration of the assertions.
    private readonly ManualResetEventSlim releaseRender = new(false);

    /// <summary>
    /// A view generator that mirrors the production render body shape (a synchronous render that
    /// resolves a service from the hub scope, exactly like <c>NodeMenuItemsExtensions.RenderMenus</c>):
    /// it signals that it is in-flight, blocks until released, then resolves a service and returns a
    /// control. Its func body runs synchronously during the top-level render subscribe, so while it
    /// blocks the Layout pool holds one in-flight permit for it.
    /// </summary>
    private IObservable<UiControl?> BlockingView(LayoutAreaHost host, RenderingContext ctx)
    {
        renderInFlight.Set();
        // Bounded so a wiring regression can never hang the suite; the test releases well within this.
        releaseRender.Wait(TimeSpan.FromSeconds(30));
        // Same service-resolution shape as the real menu renderer whose GetService threw the disposed
        // -scope ODE at teardown. With the drain joining this render first, the scope is still alive.
        _ = host.Hub.ServiceProvider.GetService<ILoggerFactory>();
        return Observable.Return<UiControl?>(Controls.Html("rendered"));
    }

    [Fact(Timeout = 60000)]
    public async Task LayoutRenderSubscribe_RunsAsTrackedDrainableLeaf_AndDrainJoinsIt()
    {
        // A hosted hub that serves our blocking layout area — same DI-scope inheritance prod uses.
        _ = Mesh.GetHostedHub(
            HostAddress,
            c => c.AddData().AddLayout(layout => layout.WithView<UiControl>(BlockingArea, BlockingView)));

        var ioPools = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        var layoutPool = ioPools.Get(IoPoolNames.Layout);

        // Subscribe the area from a client, exactly as the portal does — this drives the server-side
        // render pipeline. Fire-and-forget: the render blocks, so we must NOT await it here.
        var client = GetClient(c => c.AddData());
        var reference = new LayoutAreaReference(BlockingArea);
        var areaStream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(HostAddress, reference);
        using var areaSubscription = areaStream.GetControlStream(BlockingArea).Subscribe(_ => { });

        // Wait until the render body is genuinely executing on the pool (deterministic — no sleep).
        renderInFlight.Wait(TimeSpan.FromSeconds(30))
            .Should().BeTrue("the layout render must actually start executing");

        // (1) TRACKED: the in-flight render holds a Layout-pool permit. Pre-fix it ran on a bare
        //     TaskPoolScheduler thread the pool never saw → CurrentInFlight == 0.
        layoutPool.CurrentInFlight.Should().BeGreaterThanOrEqualTo(1,
            "the render subscribe must run as a TRACKED leaf on the drainable Layout pool — a bare " +
            "SubscribeOn(TaskPoolScheduler.Default) is invisible to the teardown drain (the SIGSEGV root cause)");

        // (2) JOINED: DrainAll must BLOCK until the in-flight render releases. Run it on a background
        //     thread; it must still be blocked while the render is held. Pre-fix DrainAll returned
        //     immediately (nothing tracked) — that is exactly how the scope disposed / the ALC
        //     unloaded underneath a still-running render.
        var drainReturned = Task.Run(() => ioPools.DrainAll());
        var returnedWhileBlocked = await Task.WhenAny(drainReturned, Task.Delay(TimeSpan.FromSeconds(1)));
        returnedWhileBlocked.Should().NotBeSameAs(drainReturned,
            "DrainAll must JOIN (block on) the in-flight render — returning while a render is still " +
            "executing is the teardown use-after-dispose/unload window this fix closes");

        // Release the render; DrainAll can now acquire the freed permit and return.
        releaseRender.Set();
        await drainReturned.WaitAsync(TimeSpan.FromSeconds(30));
        layoutPool.CurrentInFlight.Should().Be(0, "the render released its permit once it completed");
    }

    public override async ValueTask DisposeAsync()
    {
        // Guarantee the render is never left blocked if an assertion aborts early (the [Fact] timeout
        // would otherwise wait on the 30s render bound before teardown).
        releaseRender.Set();
        await base.DisposeAsync();
        renderInFlight.Dispose();
        releaseRender.Dispose();
    }
}
