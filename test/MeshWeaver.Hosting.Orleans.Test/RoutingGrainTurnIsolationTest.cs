using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Real Orleans regression for issue #1028 — "one non-reentrant RouteMessage can wedge a whole
/// silo's routing".
///
/// <para><b>Prod evidence (atioz, 2026-08-07).</b> <c>RoutingGrain</c> is
/// <c>[StatelessWorker(1)]</c> and NON-reentrant, so a silo has exactly ONE routing turn — and
/// Orleans' request timeout does not apply INSIDE a turn. One <c>RouteMessage</c> sat executing for
/// <c>06:00:22</c> with <c>NonReentrancyQueueSize=541</c>; Orleans' work-item diagnostics showed the
/// work item still <c>Running</c> with <c>Total processed</c> frozen, i.e. <c>RouteMessage</c> had
/// never RETURNED — it was blocked in its own synchronous body, which is exactly why nothing timed
/// it out. Every message the silo needed to route queued behind it, and
/// <c>Admin/UpdatePolicy</c> was unreachable for 37 h.</para>
///
/// <para><b>Invariant.</b> The turn captures its activation-bound handles and returns; the route
/// itself (path resolution, the memory-stream post, the per-node grain hand-off, the
/// <see cref="DeliveryFailure"/> NACK) runs OFF the turn on the <c>Routing</c>
/// <see cref="MeshWeaver.Mesh.Threading.IIoPool"/>. So a leg that blocks forever costs ONE pool slot
/// and the silo keeps routing.</para>
///
/// <para><b>Shape of the test.</b> The silo's <see cref="IPathResolver"/> is decorated with one whose
/// <c>Subscribe</c> BLOCKS THE CALLING THREAD for a path under a magic partition — the prod failure
/// mode, reproduced deterministically. We fire one delivery into that partition, wait (observing the
/// silo-side decorator directly, no sleep-and-hope) until the routing grain is provably inside the
/// stall, then ping an ordinary partition root. Pre-fix the ping cannot be served at all — the turn
/// is blocked — and the probe times out. Post-fix it answers in milliseconds.</para>
/// </summary>
public class RoutingGrainTurnIsolationTest(ITestOutputHelper output)
    : OrleansTestBase<StallingResolverSiloConfigurator>(output)
{
    /// <summary>
    /// Budget for the probe ping — comfortably above a healthy grain activation (&lt; 1 s). The stall
    /// only ends when the test calls <see cref="StallingPathResolver.Release"/> in its finally, so
    /// the probe can never simply wait it out; the
    /// <see cref="StallingPathResolver.StallsCompleted"/> assertion after the probe is what proves
    /// the stall was still live when the ping was answered.
    /// </summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StalledRouteLeg_DoesNotBlockTheRestOfTheSilosRouting()
    {
        // The decorator must really be the silo's resolver — otherwise the test would pass
        // vacuously (nothing stalls, everything routes). Assert the wiring, never assume it.
        var resolver = Cluster.SiloServices().GetRequiredService<IPathResolver>() as StallingPathResolver;
        resolver.Should().NotBeNull(
            "the stalling decorator must be the silo's IPathResolver — without it nothing stalls "
            + "and this test would pass without exercising #1028 at all");

        var client = GetClient($"wedge{Guid.NewGuid():N}"[..16]);

        try
        {
            // 1. A delivery whose PATH RESOLUTION blocks the subscribing thread. Pre-fix that thread
            //    IS the routing grain's activation thread, so this single message wedges the silo.
            client.Post(new PingRequest(),
                o => o.WithTarget(new Address(StallingPathResolver.StallPartition, "wedged")));

            // 2. Wait until the stall is PROVABLY in flight (read the silo-side decorator; the
            //    standard interval-poll for a source that is not itself observable). No ordering
            //    assumption, no sleep — without this the probe could overtake the stall and the
            //    test would false-pass.
            await Observable.Interval(TimeSpan.FromMilliseconds(25))
                .StartWith(0L)
                .Where(_ => resolver!.StallsEntered > 0)
                .FirstAsync()
                .ToTask(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

            Output.WriteLine("stall is in flight — probing the silo's routing");

            // 3. Probe: an ordinary bare partition root, served by the default node hub. It needs
            //    the routing grain, and nothing else about it is slow.
            var partitionRoot = $"probe-{Guid.NewGuid():N}";
            var sw = Stopwatch.StartNew();

            var response = await client
                .Observe(new PingRequest(), o => o.WithTarget(new Address(partitionRoot)))
                .FirstAsync()
                .ToTask(new CancellationTokenSource(ProbeBudget).Token);

            sw.Stop();

            response.Should().NotBeNull(
                "a route whose leg never terminates must not stop the silo routing anything else — "
                + "RoutingGrain is [StatelessWorker(1)] and non-reentrant, so any work the turn performs "
                + "inline is work every other delivery waits on, with no bound of any kind (issue #1028)");

            sw.Elapsed.Should().BeLessThan(ProbeBudget,
                $"the probe must be served while the stalled route is still stalled. Actual: {sw.Elapsed.TotalSeconds:0.0}s.");

            // The stall must STILL be running — otherwise the probe was served because the stall
            // ended, not because routing is isolated from it, and the test proved nothing.
            resolver!.StallsCompleted.Should().Be(0,
                "the probe must be answered WHILE a route leg is stuck, not after it finally unblocked");

            Output.WriteLine(
                $"PASSED — probe answered in {sw.Elapsed.TotalMilliseconds:0}ms with {resolver.StallsEntered} route leg(s) still stalled");
        }
        finally
        {
            // Unblock the stalled leg so no thread and no routing-pool permit survives into teardown.
            resolver!.Release();
        }
    }
}

/// <summary>
/// <see cref="IPathResolver"/> decorator that BLOCKS THE SUBSCRIBING THREAD for any path under
/// <see cref="StallPartition"/>, and delegates everything else to the real resolver. This is the
/// prod failure mode of issue #1028 reproduced deterministically: a route leg whose synchronous
/// prologue never returns. Self-releasing after <see cref="StallDuration"/> so the test can never
/// leak a blocked thread past the run.
/// </summary>
internal sealed class StallingPathResolver(PathResolutionService inner) : IPathResolver, IDisposable
{
    /// <summary>First path segment whose resolution blocks. Nothing else in the mesh uses it.</summary>
    internal const string StallPartition = "stalledroute";

    /// <summary>
    /// Safety net only: the longest a stalled subscribe can block if the test never calls
    /// <see cref="Release"/> (an aborted run). The test releases it explicitly, so a healthy run
    /// never waits this out and never leaves a pool permit held into teardown.
    /// </summary>
    private static readonly TimeSpan MaxStall = TimeSpan.FromSeconds(60);

    private readonly ManualResetEventSlim released = new(false);
    private int stallsEntered;
    private int stallsCompleted;

    /// <summary>Route legs that have entered the stall — the test's "the wedge is live" signal.</summary>
    internal int StallsEntered => Volatile.Read(ref stallsEntered);

    /// <summary>Route legs whose stall has ended; must stay 0 while the probe runs.</summary>
    internal int StallsCompleted => Volatile.Read(ref stallsCompleted);

    /// <summary>Unblocks every stalled leg so nothing holds a thread or a pool permit into teardown.</summary>
    internal void Release() => released.Set();

    public IObservable<AddressResolution?> ResolvePath(string path) => Stall(path) ?? inner.ResolvePath(path);

    public IObservable<AddressResolution?> ResolveNavigationPath(string path) =>
        Stall(path) ?? inner.ResolveNavigationPath(path);

    private IObservable<AddressResolution?>? Stall(string path)
    {
        if (!path.StartsWith(StallPartition, StringComparison.Ordinal))
            return null;
        return Observable.Create<AddressResolution?>(observer =>
        {
            Interlocked.Increment(ref stallsEntered);
            // 🚨 Deliberate fault INJECTION, not a wait-for-propagation sleep: this models a leaf
            // whose synchronous prologue never returns (the prod wedge). Pre-fix this runs on the
            // routing grain's activation thread and the whole silo stops routing.
            released.Wait(MaxStall);
            Interlocked.Increment(ref stallsCompleted);
            // Unwind as an ordinary NotFound so the release path is clean.
            observer.OnNext(null);
            observer.OnCompleted();
            return System.Reactive.Disposables.Disposable.Empty;
        });
    }

    public void Dispose() => released.Set();
}

/// <summary>
/// Canonical minimal silo (mirrors <c>PartitionRootSiloConfigurator</c>) plus the
/// <see cref="StallingPathResolver"/> decorator. The decorator is registered LAST so it wins over
/// the framework's <c>TryAddSingleton&lt;IPathResolver&gt;</c>; the test asserts the wiring took.
/// </summary>
public class StallingResolverSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddPartitionedInMemoryPersistence()
            .ConfigurePortalMesh();

        hostBuilder.ConfigureServices(services =>
            services.AddSingleton<IPathResolver>(sp =>
                new StallingPathResolver(sp.GetRequiredService<PathResolutionService>())));
    }
}
