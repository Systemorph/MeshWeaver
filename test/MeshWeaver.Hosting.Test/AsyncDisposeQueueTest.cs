using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Tests for <see cref="AsyncDisposeQueue"/> — the async half of mesh teardown.
/// Resources enqueue async cleanup from their synchronous <c>Dispose()</c>; a TPL
/// <c>ActionBlock</c> drains it in the background; the mesh teardown awaits
/// <see cref="AsyncDisposeQueue.DrainAsync"/> (the quiesce) before the scope closes.
/// Drainage is verified by the version hook: enqueue work, drain, assert the version
/// advanced by exactly the number of items (old + N).
/// </summary>
public class AsyncDisposeQueueTest
{
    private static readonly TimeSpan Quiesce = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DrainAsync_runs_every_enqueued_cleanup_and_advances_version_by_item_count()
    {
        var queue = new AsyncDisposeQueue();
        var ran = 0;
        const int items = 5;

        var before = queue.DrainedVersion;
        for (var i = 0; i < items; i++)
            queue.Enqueue(async _ => { await Task.Yield(); Interlocked.Increment(ref ran); });

        await queue.DrainAsync(Quiesce);

        // "putting versions in and checking against own version: old version + N"
        queue.DrainedVersion.Should().Be(before + items);
        ran.Should().Be(items);
    }

    // The teardown-SIGSEGV fix: a cleanup that overruns the quiesce budget must be CANCELLED
    // (so it unwinds) and JOINED — never abandoned mid-flight to run past teardown, where it
    // would touch a collectible node ALC's compiled types after the ALC unloads.
    [Fact]
    public async Task DrainAsync_cancels_a_wedged_cleanup_then_joins_within_budget()
    {
        var queue = new AsyncDisposeQueue();
        var before = queue.DrainedVersion;
        var cancelled = false;
        var entered = new TaskCompletionSource();

        // A cleanup that never completes on its own (like an in-flight stream / flush).
        queue.Enqueue(async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        });

        await entered.Task.WaitAsync(Quiesce, TestContext.Current.CancellationToken);

        // Budget expires with the cleanup still running → DrainAsync cancels it, then joins.
        await queue.DrainAsync(TimeSpan.FromMilliseconds(200));

        cancelled.Should().BeTrue("DrainAsync cancels a cleanup that overran the quiesce budget");
        queue.DrainedVersion.Should().Be(before + 1, "the cancelled cleanup is JOINED, not abandoned");
    }

    [Fact]
    public async Task Enqueue_from_sync_caller_drains_on_the_background_loop()
    {
        // Mirrors the real flow: a synchronous Dispose() enqueues async cleanup with no
        // await; the background ActionBlock runs it; DrainAsync awaits it (the quiesce).
        var queue = new AsyncDisposeQueue();
        var done = new TaskCompletionSource();
        queue.Enqueue(async _ => { await Task.Yield(); done.SetResult(); });

        await queue.DrainAsync(Quiesce);

        done.Task.IsCompletedSuccessfully.Should().BeTrue();
        queue.DrainedVersion.Should().Be(1);
    }

    [Fact]
    public async Task DrainAsync_swallows_a_faulting_cleanup_and_still_runs_the_rest()
    {
        var queue = new AsyncDisposeQueue();
        var ran = 0;
        queue.Enqueue(_ => throw new InvalidOperationException("boom"));
        queue.Enqueue(async _ => { await Task.Yield(); Interlocked.Increment(ref ran); });

        await queue.DrainAsync(Quiesce);

        ran.Should().Be(1);
        // Both items are accounted for in the version — the faulting one too.
        queue.DrainedVersion.Should().Be(2);
    }
}
