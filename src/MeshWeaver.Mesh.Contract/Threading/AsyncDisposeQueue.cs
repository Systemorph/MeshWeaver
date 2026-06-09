using System.Threading.Tasks.Dataflow;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The async half of mesh teardown. Disposal in MeshWeaver is reactive and
/// <b>synchronous</b> — <see cref="System.IDisposable.Dispose"/> fires and returns;
/// it must never block (an <c>await</c> on a hub turn deadlocks the very turn that is
/// tearing down). But some resources have genuinely-async cleanup — draining an
/// in-flight <see cref="IIoPool"/> operation, flushing a write queue, awaiting a stream
/// to finish. They cannot do it inside their sync <c>Dispose()</c>.
///
/// <para>The pattern: in their sync <c>Dispose()</c>, resources
/// <see cref="Enqueue(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task})"/>
/// their async cleanup onto this mesh-scoped queue instead of running or leaking it. A
/// single-consumer TPL <see cref="ActionBlock{T}"/> keeps draining the queue in the
/// background (serially, so teardown order is deterministic). The mesh's own async
/// teardown (<c>DisposeAsync</c>) calls <see cref="DrainAsync"/> AFTER the reactive
/// disposal has completed (so every resource has enqueued) and BEFORE the DI scope is
/// torn down — giving the queue a bounded quiesce budget to finish. No async
/// continuation then runs against a disposed scope (the "catastrophic
/// ObjectDisposedException" class). Mesh-scoped singleton; no static state.</para>
///
/// <para>Drainage is observable for tests: <see cref="DrainedVersion"/> bumps once per
/// item run, so a test can enqueue work, drain, and assert the version advanced by the
/// number of items (old + N) — the hook behind the queue's drain tests.</para>
/// </summary>
public sealed class AsyncDisposeQueue
{
    private readonly ActionBlock<Func<CancellationToken, Task>> _block;
    private long _drained;

    public AsyncDisposeQueue()
    {
        // MaxDegreeOfParallelism = 1: cleanup runs serially in enqueue order, so
        // teardown is deterministic (a write-queue flush completes before the pool
        // that fed it is torn down, etc.).
        _block = new ActionBlock<Func<CancellationToken, Task>>(
            async work =>
            {
                try { await work(CancellationToken.None).ConfigureAwait(false); }
                catch { /* teardown best-effort — one bad cleanup must not strand the rest */ }
                finally { Interlocked.Increment(ref _drained); }
            },
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1 });
    }

    /// <summary>Number of cleanup items run so far. Advances by exactly one per drained item.</summary>
    public long DrainedVersion => Interlocked.Read(ref _drained);

    /// <summary>
    /// Enqueue an async cleanup unit. Safe to call from a synchronous
    /// <see cref="System.IDisposable.Dispose"/>; the work runs on the background drain
    /// loop. If the queue has already been completed by <see cref="DrainAsync"/> (a late
    /// straggler after teardown began), the unit is run inline best-effort so it is never
    /// silently dropped.
    /// </summary>
    public void Enqueue(Func<CancellationToken, Task> asyncCleanup)
    {
        ArgumentNullException.ThrowIfNull(asyncCleanup);
        if (!_block.Post(asyncCleanup))
            _ = RunOrphanAsync(asyncCleanup);
    }

    /// <summary>Cancellation-free convenience overload.</summary>
    public void Enqueue(Func<Task> asyncCleanup)
    {
        ArgumentNullException.ThrowIfNull(asyncCleanup);
        Enqueue(_ => asyncCleanup());
    }

    private async Task RunOrphanAsync(Func<CancellationToken, Task> work)
    {
        try { await work(CancellationToken.None).ConfigureAwait(false); }
        catch { /* best-effort */ }
        finally { Interlocked.Increment(ref _drained); }
    }

    /// <summary>
    /// Terminal drain: stop accepting new items and await every posted item, bounded by
    /// <paramref name="quiesce"/>. Idempotent. A timeout means a cleanup is genuinely
    /// wedged (a separate bug to surface from a stuck resource) — teardown proceeds
    /// rather than hanging.
    /// </summary>
    public async Task DrainAsync(TimeSpan quiesce)
    {
        _block.Complete();
        try { await _block.Completion.WaitAsync(quiesce).ConfigureAwait(false); }
        catch (TimeoutException) { /* wedged cleanup — don't hang teardown */ }
    }
}
