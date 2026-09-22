using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// A <see cref="TaskScheduler"/> that runs at most <c>maxDegreeOfParallelism</c>
/// tasks concurrently on top of the shared .NET <see cref="ThreadPool"/>.
/// Adapted from the Microsoft Docs sample "How to: Create a Task Scheduler That
/// Limits Concurrency"
/// (https://learn.microsoft.com/dotnet/standard/parallel-programming/how-to-create-a-task-scheduler-that-limits-concurrency).
///
/// <para>Used by <see cref="IoPool.InvokeBlocking{T}"/> for sync-blocking / CPU
/// leaves (e.g. <c>File.ReadAllBytes</c>, Roslyn compile, <c>Process</c>). It
/// borrows ThreadPool threads — it never spawns its own — but caps how many it
/// occupies at once, so a burst of blocking work cannot trigger runaway thread
/// injection and starve the ThreadPool that the Orleans grain schedulers rely
/// on. This is the "compatible with how Orleans wants us to pool" property:
/// reuse the framework's pool, just govern it.</para>
///
/// <para>🚨 <b>Except for CPU-bound work, where borrowing is the defect.</b> A capped borrower still
/// HOLDS up to <c>maxDegreeOfParallelism</c> pool workers for as long as each leaf computes, and the
/// pool's minimum is <c>ProcessorCount</c> — so a CPU pool capped at the processor count occupies
/// every worker the grain turns need. With <c>dedicatedThreads</c> the same cap is enforced over
/// threads this scheduler starts itself (one per concurrent drain loop, exiting when the queue is
/// empty): the bound is identical, and the ThreadPool is not touched at all
/// (<c>IoPoolNames.CompileCpu</c>, <c>Doc/Architecture/CompileOffTheThreadPool</c>).</para>
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

    // Number of ThreadPool work items currently dispatched (running or queued to run).
    private int _delegatesQueuedOrRunning;

    // True: each drain loop runs on a thread started here, never a ThreadPool worker.
    private readonly bool _dedicatedThreads;

    /// <summary>Name of the threads a dedicated-thread scheduler starts — visible in dumps.</summary>
    internal const string DedicatedThreadName = "mw-cpu-lane";

    public LimitedConcurrencyLevelTaskScheduler(int maxDegreeOfParallelism, bool dedicatedThreads = false)
    {
        if (maxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
        _dedicatedThreads = dedicatedThreads;
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
        if (_dedicatedThreads)
        {
            // A drain loop of its own: the same loop, on a thread the pool never lends out. Background
            // so an idle-but-draining loop can never hold the process open; UnsafeStart because each
            // task carries its own captured ExecutionContext into TryExecuteTask.
            new Thread(_ => DrainQueue(), maxStackSize: 0)
            {
                IsBackground = true,
                Name = DedicatedThreadName,
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
