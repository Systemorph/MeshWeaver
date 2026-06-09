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
