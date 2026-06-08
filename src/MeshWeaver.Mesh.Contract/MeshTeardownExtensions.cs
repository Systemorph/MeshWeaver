using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// Deterministic teardown for a mesh root hub. Disposing a hub is reactive and
/// returns immediately (<see cref="IMessageHub.Dispose"/> kicks off the disposal
/// state machine); callers that go on to tear down the hub's service scope —
/// tests between <c>[Fact]</c>s, a silo on stop, a host on shutdown — must wait
/// for ALL of the hub's activity to finish first, or a late continuation resolves
/// a service from the already-disposed Autofac scope and throws
/// <see cref="ObjectDisposedException"/> ("LifetimeScope … has already been
/// disposed"). Unobserved, that surfaces as an xUnit "catastrophic failure" that
/// corrupts the rest of the run.
///
/// <para>"All of the hub's activity" is TWO things, and
/// <see cref="IMessageHub.DisposalCompleted"/> only covers the first:</para>
/// <list type="number">
/// <item>the action blocks + in-flight message round-trips (drained before
///   <see cref="IMessageHub.DisposalCompleted"/> fires), and</item>
/// <item>I/O offloaded onto the ThreadPool through <see cref="IIoPool"/> — which
///   runs independently of the action block and is NOT tracked by
///   <see cref="IMessageHub.DisposalCompleted"/>. <see cref="IoPoolRegistry.WhenDrained"/>
///   is the wait for that.</item>
/// </list>
/// </summary>
public static class MeshTeardownExtensions
{
    /// <summary>
    /// Disposes <paramref name="mesh"/> and awaits BOTH halves of its drain
    /// (<see cref="IMessageHub.DisposalCompleted"/> then the
    /// <see cref="IoPoolRegistry"/>), so the caller may safely dispose the
    /// service scope afterwards. Each wait is bounded by <paramref name="timeout"/>
    /// (a stuck action block or leaked I/O slot completes the wait rather than
    /// hanging teardown — the underlying bug surfaces elsewhere, e.g.
    /// <c>AnyHubQuiescingTimedOut</c> or a non-zero <see cref="IoPoolRegistry.TotalInFlight"/>).
    /// </summary>
    public static async Task TeardownAsync(this IMessageHub mesh, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        // Capture the registry while the scope is still ALIVE — never resolve DI
        // once disposal has begun (the scope may already be tearing down).
        var ioPools = mesh.ServiceProvider.GetService<IoPoolRegistry>();

        mesh.Dispose();

        await mesh.WaitForDisposalAndIoDrainAsync(ioPools, timeout);
    }

    /// <summary>
    /// The wait half of <see cref="TeardownAsync"/>, exposed for callers that
    /// already drive <see cref="IMessageHub.Dispose"/> themselves (and keep their
    /// own progress/diagnostic loop around <see cref="IMessageHub.DisposalCompleted"/>).
    /// Pass the <see cref="IoPoolRegistry"/> captured BEFORE disposal began.
    /// </summary>
    public static async Task WaitForDisposalAndIoDrainAsync(
        this IMessageHub mesh, IoPoolRegistry? ioPools, TimeSpan timeout)
    {
        // (1) Action blocks + message round-trips.
        await mesh.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .ToTask()
            .WaitAsync(timeout);

        // (2) Offloaded ThreadPool I/O — the half DisposalCompleted does not cover.
        if (ioPools is not null)
            await ioPools.WhenDrained(timeout).FirstAsync().ToTask();
    }
}
