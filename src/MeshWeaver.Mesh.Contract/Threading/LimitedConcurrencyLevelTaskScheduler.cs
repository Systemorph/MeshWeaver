using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// A <see cref="TaskScheduler"/> that runs at most <c>maxDegreeOfParallelism</c>
/// tasks concurrently. Adapted from the Microsoft Docs sample "How to: Create a Task Scheduler That
/// Limits Concurrency"
/// (https://learn.microsoft.com/dotnet/standard/parallel-programming/how-to-create-a-task-scheduler-that-limits-concurrency).
///
/// <para>Used by <see cref="IoPool.InvokeBlocking{T}"/> for sync-blocking / CPU
/// leaves (e.g. <c>File.ReadAllBytes</c>, Roslyn compile, <c>Process</c>). It caps how many run at
/// once, so a burst of blocking work QUEUES behind the cap instead of fanning out.</para>
///
/// <para>🚨 <b>The drain loops run on threads this scheduler starts itself</b> — at most one per slot,
/// started on demand and then KEPT: an idle lane thread parks on the queue's monitor until the next
/// <see cref="QueueTask"/> wakes it, and exits only once <see cref="Complete"/> has been called (the
/// owning <see cref="IoPool"/> calls it when its disposal completes). The Docs sample this was adapted
/// from BORROWS ThreadPool workers, and for a blocking leaf that is the defect: the leaf holds its
/// worker for as long as it blocks, the pool's minimum is <c>ProcessorCount</c> (6 on a portal pod),
/// and most caps are far above that (<c>FileSystem</c> 256, <c>Http</c> 16). A burst of network-volume
/// reads, bundle reads or subprocess waits could therefore hold every worker the grain turns and
/// routing legs need, and the silo then waited on the ThreadPool's slow thread injection — Orleans'
/// ".NET Thread Pool execution stalled". The CPU lane went first (<c>IoPoolNames.CompileCpu</c>,
/// <c>Doc/Architecture/CompileOffTheThreadPool</c>); the same argument holds for anything that holds a
/// thread without yielding it (<c>Doc/Architecture/BlockingLeavesOffTheThreadPool</c>). A <c>null</c>
/// thread name keeps the borrowing behaviour; it exists so a test can show the difference.</para>
///
/// <para>🚨 <b>Why a lane thread is KEPT rather than started per burst (#4654).</b> This scheduler used
/// to start a fresh thread whenever a leaf reached an idle lane and let it EXIT the moment the queue
/// drained — one OS thread created and destroyed per burst, thousands an hour on a portal. A thread
/// EXIT is the trigger of a CoreCLR defect: a thread that once touched a collectible
/// <c>[ThreadStatic]</c> (any <c>ArrayPool&lt;T&gt;.Shared</c> over a NodeType-compiled <c>T</c>
/// creates one) frees, on exit, a loader handle in whatever LIVE collectible context has since been
/// handed that thread-static index — releasing an unrelated GC-statics box, whose static then dangles
/// (<c>Doc/Architecture/CollectibleThreadStaticHandleReuse</c>). The runtime fix is upstream; what this
/// repository controls is how often its own threads exit, and a lane thread now exits once per pool
/// lifetime instead of once per burst. The cost is that a lane keeps the threads its widest burst
/// needed (never more than the cap) parked until the pool is disposed.</para>
///
/// <para>The <c>_tasks</c> queue is an INSTANCE field guarded by <c>lock(_tasks)</c>
/// — not static, so it dies with the owning <see cref="IoPool"/> and never bleeds
/// across meshes/tests. A mutable <see cref="LinkedList{T}"/> is required because
/// the scheduler contract needs remove-by-reference (<see cref="TryDequeue"/>),
/// which immutable queues don't support; this is infrastructure plumbing, not
/// domain data.</para>
/// </summary>
internal sealed class LimitedConcurrencyLevelTaskScheduler : TaskScheduler
{
    // Pending tasks. Protected by lock(_tasks). Instance-scoped — dies with the pool.
    private readonly LinkedList<Task> _tasks = new();

    private readonly int _maxDegreeOfParallelism;

    // Number of drain loops currently dispatched (running, or started and about to run). In the
    // dedicated-thread mode this is the number of LIVE lane threads, parked ones included.
    private int _delegatesQueuedOrRunning;

    // Dedicated mode only: lane threads parked in the monitor wait and not yet woken. QueueTask wakes
    // one per queued task before it considers starting a new thread. Protected by lock(_tasks).
    private int _parked;

    // Dedicated mode only: set by Complete(); a lane thread that finds the queue empty then exits
    // instead of parking. Protected by lock(_tasks).
    private bool _completed;

    // Non-null: each drain loop runs on a thread started here, under this name, never a ThreadPool
    // worker. Null: the drain loops borrow ThreadPool workers (kept for the contrast tests only).
    private readonly string? _dedicatedThreadName;

    /// <summary>Name of the threads the CPU lane (<c>IoPoolNames.CompileCpu</c>) starts — visible in dumps.</summary>
    internal const string DedicatedThreadName = "mw-cpu-lane";

    /// <summary>Name of the threads every other pool's blocking leaves run on — visible in dumps.</summary>
    internal const string BlockingThreadName = "mw-io-lane";

    /// <param name="maxDegreeOfParallelism">How many tasks may run at once; at least 1.</param>
    /// <param name="dedicatedThreadName">
    /// The name of the threads the drain loops run on; <c>null</c> borrows ThreadPool workers instead.
    /// </param>
    public LimitedConcurrencyLevelTaskScheduler(
        int maxDegreeOfParallelism, string? dedicatedThreadName = BlockingThreadName)
    {
        if (maxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
        _dedicatedThreadName = dedicatedThreadName;
    }

    protected override void QueueTask(Task task)
    {
        // Add the task to the queue; wake a parked lane thread if there is one, otherwise — below the
        // cap — dispatch a new drain loop.
        lock (_tasks)
        {
            var node = _tasks.AddLast(task);
            if (_parked > 0)
            {
                // Counted down HERE, by the waker, so two tasks queued back to back wake two parked
                // threads instead of pulsing the same one twice and running both serially.
                --_parked;
                Monitor.Pulse(_tasks);
                return;
            }
            if (_delegatesQueuedOrRunning < _maxDegreeOfParallelism)
            {
                ++_delegatesQueuedOrRunning;
                try
                {
                    NotifyThreadPoolOfPendingWork();
                }
                catch
                {
                    // Nothing will drain for this reservation (thread creation / start failed, e.g.
                    // out of memory for a stack): give the slot back and withdraw the task, or the
                    // count stays at the cap with no drain loop behind it and every later QueueTask
                    // queues behind a loop that does not exist. The fault propagates to the caller
                    // (TaskFactory.StartNew), which faults the leaf.
                    --_delegatesQueuedOrRunning;
                    _tasks.Remove(node);
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Ends the lane threads: each finishes whatever is still queued, then exits instead of parking.
    /// Returns at once — it joins nothing, so it is safe on <see cref="IoPool"/>'s non-blocking
    /// disposal path, including from a lane thread itself. Idempotent. A task queued AFTER this still
    /// runs (a drain loop is started for it, and exits when the queue drains), so nothing queued is
    /// ever stranded.
    /// </summary>
    public void Complete()
    {
        lock (_tasks)
        {
            _completed = true;
            _parked = 0;
            Monitor.PulseAll(_tasks);
        }
    }

    /// <summary>
    /// Live drain loops: in the dedicated mode the lane threads alive (parked ones included); in the
    /// borrowing mode the loops in flight.
    /// </summary>
    internal int LiveDrainLoops
    {
        get
        {
            lock (_tasks)
                return _delegatesQueuedOrRunning;
        }
    }

    /// <summary>Lane threads currently parked waiting for work (dedicated mode; always 0 otherwise).</summary>
    internal int ParkedLaneThreads
    {
        get
        {
            lock (_tasks)
                return _parked;
        }
    }

    private void NotifyThreadPoolOfPendingWork()
    {
        if (_dedicatedThreadName is { } name)
        {
            // A lane thread of its own, on a thread the pool never lends out. Background so a parked
            // lane can never hold the process open; UnsafeStart because each task carries its own
            // captured ExecutionContext into TryExecuteTask (and a kept thread must not pin whichever
            // caller's context happened to start it).
            new Thread(_ => LaneLoop(), maxStackSize: 0)
            {
                IsBackground = true,
                Name = name,
            }.UnsafeStart();
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(_ => DrainQueue(), null);
    }

    /// <summary>
    /// A dedicated lane thread's whole life: run queued tasks; when the queue is empty, PARK until
    /// <see cref="QueueTask"/> wakes it; exit only once <see cref="Complete"/> has been called. The
    /// park is a wait for WORK on a thread this scheduler owns — never a hub turn, a grain turn or a
    /// ThreadPool worker — so it holds up no scheduler the mesh runs on.
    /// </summary>
    private void LaneLoop()
    {
        while (true)
        {
            Task item;
            lock (_tasks)
            {
                while (_tasks.Count == 0)
                {
                    if (_completed)
                    {
                        --_delegatesQueuedOrRunning;
                        return;
                    }
                    ++_parked;
                    Monitor.Wait(_tasks);
                }

                item = _tasks.First!.Value;
                _tasks.RemoveFirst();
            }

            TryExecuteTask(item);
        }
    }

    /// <summary>
    /// The borrowing mode's drain loop (a ThreadPool worker must never be parked): run until the
    /// queue is empty, then give the worker back.
    /// </summary>
    private void DrainQueue()
    {
        while (true)
        {
            Task item;
            lock (_tasks)
            {
                if (_tasks.Count == 0)
                {
                    --_delegatesQueuedOrRunning;
                    break;
                }

                item = _tasks.First!.Value;
                _tasks.RemoveFirst();
            }

            TryExecuteTask(item);
        }
    }

    // Never inline. Inlining would run the (blocking) task on whatever thread
    // called Wait/Result — bypassing the concurrency cap and potentially
    // executing on a hub/Orleans scheduler thread. Declining inlining keeps
    // every task bounded by this scheduler.
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override bool TryDequeue(Task task)
    {
        lock (_tasks)
            return _tasks.Remove(task);
    }

    public override int MaximumConcurrencyLevel => _maxDegreeOfParallelism;

    protected override IEnumerable<Task> GetScheduledTasks()
    {
        var lockTaken = false;
        try
        {
            Monitor.TryEnter(_tasks, ref lockTaken);
            if (lockTaken)
                return _tasks.ToArray();
            throw new NotSupportedException();
        }
        finally
        {
            if (lockTaken)
                Monitor.Exit(_tasks);
        }
    }
}
