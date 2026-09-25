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
/// <para>🚨 <b>The drain loops run on threads this scheduler starts itself</b> — one per concurrent
/// drain loop, each exiting when the queue is empty, so an idle pool holds no thread. The Docs sample
/// this was adapted from BORROWS ThreadPool workers, and for a blocking leaf that is the defect: the
/// leaf holds its worker for as long as it blocks, the pool's minimum is <c>ProcessorCount</c> (6 on a
/// portal pod), and most caps are far above that (<c>FileSystem</c> 256, <c>Http</c> 16). A burst of
/// network-volume reads, bundle reads or <c>git</c> waits could therefore hold every worker the grain
/// turns and routing legs need, and the silo then waited on the ThreadPool's slow thread injection —
/// Orleans' ".NET Thread Pool execution stalled". The CPU lane went first
/// (<c>IoPoolNames.CompileCpu</c>, <c>Doc/Architecture/CompileOffTheThreadPool</c>); the same argument
/// holds for anything that holds a thread without yielding it
/// (<c>Doc/Architecture/BlockingLeavesOffTheThreadPool</c>). A <c>null</c> thread name keeps the
/// borrowing behaviour; it exists so a test can show the difference.</para>
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

    // Number of drain loops currently dispatched (running, or started and about to run).
    private int _delegatesQueuedOrRunning;

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
        // Add the task to the queue and, if we're below the cap, dispatch a
        // ThreadPool work item to drain it.
        lock (_tasks)
        {
            var node = _tasks.AddLast(task);
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

    private void NotifyThreadPoolOfPendingWork()
    {
        if (_dedicatedThreadName is { } name)
        {
            // A drain loop of its own: the same loop, on a thread the pool never lends out. Background
            // so an idle-but-draining loop can never hold the process open; UnsafeStart because each
            // task carries its own captured ExecutionContext into TryExecuteTask.
            new Thread(_ => DrainQueue(), maxStackSize: 0)
            {
                IsBackground = true,
                Name = name,
            }.UnsafeStart();
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(_ => DrainQueue(), null);
    }

    private void DrainQueue()
    {
        {
            // Process tasks until the queue drains, then relinquish this slot.
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
