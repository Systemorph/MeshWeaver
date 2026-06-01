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

    public LimitedConcurrencyLevelTaskScheduler(int maxDegreeOfParallelism)
    {
        if (maxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    protected override void QueueTask(Task task)
    {
        // Add the task to the queue and, if we're below the cap, dispatch a
        // ThreadPool work item to drain it.
        lock (_tasks)
        {
            _tasks.AddLast(task);
            if (_delegatesQueuedOrRunning < _maxDegreeOfParallelism)
            {
                ++_delegatesQueuedOrRunning;
                NotifyThreadPoolOfPendingWork();
            }
        }
    }

    private void NotifyThreadPoolOfPendingWork()
    {
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
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
        }, null);
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
