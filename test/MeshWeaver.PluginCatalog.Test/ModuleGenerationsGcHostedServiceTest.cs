using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The #2684 contract of <see cref="ModuleGenerationsGcHostedService"/>: the generations GC is
/// housekeeping that runs AFTER <c>ApplicationStarted</c> — never in <c>StartAsync</c>, which the
/// host awaits before it can listen — and a pass stalled in slow IO (the CIFS <c>Dsl</c> park that
/// blew memex-cloud's startup probe) can neither delay the started callback nor survive teardown:
/// disposing the service cancels the pooled leaf's token and the collector unwinds.
/// </summary>
public class ModuleGenerationsGcHostedServiceTest : IDisposable
{
    private readonly IoPool pool = new(1);
    private readonly ApplicationLifetime lifetime = new(NullLogger<ApplicationLifetime>.Instance);

    public void Dispose() => pool.Dispose();

    [Fact]
    public async Task TheSweepRunsOnlyAfterApplicationStarted()
    {
        var invoked = 0;
        var svc = new ModuleGenerationsGcHostedService(
            "(unused)", lifetime, pool,
            collect: (_, _, _) =>
            {
                Interlocked.Increment(ref invoked);
                return 7;
            });

        await svc.StartAsync(CancellationToken.None);

        // FIFO probe through the same cap-1 pool: had StartAsync scheduled the sweep, it would
        // have run before this probe completed. A positive signal, not a sleep.
        await pool.InvokeBlocking(_ => 0).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref invoked));

        lifetime.NotifyStarted();
        var removed = await svc.Completed
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        Assert.Equal(7, removed);
        Assert.Equal(1, Volatile.Read(ref invoked));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AStalledSweepNeverBlocksTheStartedCallback_AndTeardownCancelsIt()
    {
        var entered = new AsyncSubject<Unit>();
        var unwound = new AsyncSubject<Unit>();
        var svc = new ModuleGenerationsGcHostedService(
            "(unused)", lifetime, pool,
            collect: (_, _, ct) =>
            {
                entered.OnNext(Unit.Default);
                entered.OnCompleted();
                // The CIFS stall, simulated on the pool thread: the collector makes no progress
                // until the pooled leaf's token cancels — the state memex-cloud's PID 1 sat in
                // (#2684). Deliberately a cooperative spin, NOT a kernel wait: a blocking bridge
                // in a test is the #2013 defect class (BlockingBridgeInTestRatchetGuard), and the
                // property under test is precisely that nothing on the host side ever waits on
                // this thread. The 30 s ceiling means even a broken cancellation path cannot park
                // the thread past this test's own Timeout assertions.
                var stall = System.Diagnostics.Stopwatch.StartNew();
                var spin = new SpinWait();
                while (!ct.IsCancellationRequested && stall.Elapsed < TimeSpan.FromSeconds(30))
                    spin.SpinOnce();
                if (ct.IsCancellationRequested)
                {
                    unwound.OnNext(Unit.Default);
                    unwound.OnCompleted();
                }
                return 0;
            });

        await svc.StartAsync(CancellationToken.None);

        // NotifyStarted runs registered callbacks INLINE — if the sweep ran inside the callback
        // rather than being scheduled on the pool, this line would park forever on the stall
        // (and the test's timeout would name it).
        lifetime.NotifyStarted();

        // The sweep IS running (on a pool thread), parked in its "IO".
        await entered.Timeout(TimeSpan.FromSeconds(10)).FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        // Teardown: disposing the service unsubscribes the pooled leaf, which cancels its linked
        // token — the stalled collector unwinds instead of parking the drain.
        await svc.StopAsync(CancellationToken.None);
        await unwound.Timeout(TimeSpan.FromSeconds(10)).FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
    }
}
