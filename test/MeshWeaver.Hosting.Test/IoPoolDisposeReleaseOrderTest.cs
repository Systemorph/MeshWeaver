using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #2135: the LAST leaf to unwind during a dispose threw
/// <see cref="ObjectDisposedException"/> out of its own <c>finally</c>.
///
/// <para>Not a cross-thread race — self-inflicted and deterministic. The exit path read
/// <c>Decrement(_inFlight); TryFinishDisposal(); _gate.Release();</c>, and
/// <c>TryFinishDisposal</c> disposes the gate the instant that decrement takes the count to zero.
/// So the leaf disposed the semaphore and then released it. In production it surfaced as a failed
/// <c>Comments</c> area render on memex-cloud, reported by <c>LayoutAreaHost</c> — which is the
/// reporter, not the defect.</para>
///
/// <para>These tests need no timing: the leaf parks on a TCS the test controls, so "dispose while a
/// leaf is in flight" is a sequence, not a race. That matters because the bug is invisible to a
/// pool that is idle at dispose — which is every pool in a test that does not deliberately hold a
/// leaf open.</para>
/// </summary>
public class IoPoolDisposeReleaseOrderTest
{
    [Fact]
    public async Task Disposing_while_a_leaf_is_in_flight_does_not_fault_the_subscriber()
    {
        var pool = new IoPool(maxConcurrency: 1);
        var leafEntered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var run = pool.Invoke(async ct =>
        {
            leafEntered.TrySetResult();
            await release.Task;
            return 42;
        }).ToTask();

        await leafEntered.Task;      // the permit is taken and _inFlight is 1

        pool.Dispose();              // cancels; the gate must survive until the leaf is out
        release.TrySetResult();      // the leaf now runs its finally — the moment of the defect

        // The leaf was cancelled by Dispose, so a cancellation is a legitimate outcome. An
        // ObjectDisposedException is NOT: it means the pool tore its own gate out from under the
        // leaf that was still holding a permit.
        var fault = await Record.ExceptionAsync(() => run);
        fault.Should().NotBeOfType<ObjectDisposedException>(
            "the last leaf disposed the gate and then released it");
    }

    [Fact]
    public async Task Disposal_still_completes_after_the_last_leaf_leaves()
    {
        var pool = new IoPool(maxConcurrency: 1);
        var leafEntered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var disposed = pool.Disposed.FirstAsync().ToTask();
        var run = pool.Invoke(async ct =>
        {
            leafEntered.TrySetResult();
            await release.Task;
            return 1;
        }).ToTask();

        await leafEntered.Task;
        pool.Dispose();

        // Reordering the exit path must not cost the guarantee the ordering existed for: the
        // resources are released on the last leaf's way OUT, and Disposed reports it.
        release.TrySetResult();
        await Record.ExceptionAsync(() => run);

        var leaked = await disposed.WaitAsync(TimeSpan.FromSeconds(20));
        leaked.Should().Be(0, "the leaf unwound, so nothing survived teardown");
        pool.CurrentInFlight.Should().Be(0);
    }

    [Fact]
    public async Task An_idle_pool_still_disposes_immediately()
    {
        // The path that always worked, kept honest: with nothing in flight, Dispose completes on
        // the spot rather than waiting for a leaf that will never come.
        var pool = new IoPool(maxConcurrency: 2);
        var disposed = pool.Disposed.FirstAsync().ToTask();

        pool.Dispose();

        (await disposed.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(0);
    }
}
