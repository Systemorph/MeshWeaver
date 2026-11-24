namespace MeshWeaver.Messaging;

/// <summary>
/// The scheduler a hub's turns run on from the moment its <c>Dispose()</c> begins — a small set of
/// dedicated threads the ThreadPool never lends out and never queues behind (#5799).
///
/// <para><b>Why teardown cannot ride the pool.</b> A hosted hub has no scheduler of its own, so its
/// pump drains on <see cref="TaskScheduler.Default"/>. That is right for ordinary traffic and wrong
/// for teardown: a drain handed to the pool waits behind EVERY item queued ahead of it, so when the
/// pool is backlogged (a 2026-11-24 heartbeat on memex-cloud read <c>poolThreads=34
/// poolPending=2865</c>, and earlier captures 69,012 and 113,021) a hub's
/// <c>ShutdownRequest</c> sat unstarted for the whole 8 s stall budget — the
/// <c>THE PUMP IS NOT TURNING</c> verdict, 1,224 times. Seven earlier changes made the pool drain
/// FASTER (fairness flag, fewer lock queues, fewer parked threads, an instrument naming the flooder);
/// none could make teardown independent of it, because the drain was still a pool work item. A lane
/// of its own removes the dependency: the shutdown turn needs a thread, not the pool's queue.</para>
///
/// <para><b>Safe for the actor model.</b> The hub's serialisation comes from its drain latch, not from
/// any thread; a drain may run on any scheduler. Orleans grain hubs keep their activation scheduler
/// (this lane is used only when the hub's scheduler is <see cref="TaskScheduler.Default"/>).</para>
///
/// <para>Threads are started on demand up to <see cref="MaxThreads"/>, KEPT parked until the lane is
/// disposed (a thread exit is the trigger of the collectible <c>[ThreadStatic]</c> defect, #4654), and
/// are background threads. One lane per mesh, registered on the root hub like <c>ReleaseLane</c>;
/// never a static.</para>
/// </summary>
public sealed class DisposalDrainLane : TaskScheduler, IDisposable
{
    /// <summary>The most dedicated threads the lane starts.</summary>
    public const int MaxThreads = 16;

    private readonly LinkedList<Task> tasks = new();
    private int liveThreads;
    // Threads currently INSIDE Monitor.Wait (bracketed: ++ before, -- after, so a spurious wakeup
    // cannot overcount), and wake-ups already sent that the woken thread has not yet consumed.
    private int parked;
    private int pendingWakes;
    private bool completed;

    /// <summary>Gets the thread count the lane has started so far (parked threads included).</summary>
    public int LiveThreads
    {
        get { lock (tasks) return liveThreads; }
    }

    /// <inheritdoc />
    public override int MaximumConcurrencyLevel => MaxThreads;

    /// <inheritdoc />
    protected override void QueueTask(Task task)
    {
        lock (tasks)
        {
            var node = tasks.AddLast(task);
            if (parked - pendingWakes > 0)
            {
                // One wake per waiting thread, so two tasks queued back to back wake two threads.
                ++pendingWakes;
                Monitor.Pulse(tasks);
                return;
            }
            if (liveThreads >= MaxThreads)
                return;
            ++liveThreads;
            try
            {
                new Thread(LaneLoop) { IsBackground = true, Name = "mw-disposal-lane" }.UnsafeStart();
            }
            catch
            {
                --liveThreads;
                tasks.Remove(node);
                throw;
            }
        }
    }

    private void LaneLoop()
    {
        while (RunOne())
        {
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool RunOne()
    {
        Task item;
        lock (tasks)
        {
            while (tasks.Count == 0)
            {
                if (completed)
                {
                    --liveThreads;
                    return false;
                }
                ++parked;
                Monitor.Wait(tasks);
                --parked;
                if (pendingWakes > 0)
                    --pendingWakes;
            }
            item = tasks.First!.Value;
            tasks.RemoveFirst();
        }
        TryExecuteTask(item);
        return true;
    }

    /// <inheritdoc />
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    /// <inheritdoc />
    protected override bool TryDequeue(Task task)
    {
        lock (tasks)
            return tasks.Remove(task);
    }

    /// <inheritdoc />
    protected override IEnumerable<Task> GetScheduledTasks()
    {
        lock (tasks)
            return tasks.ToArray();
    }

    /// <summary>Ends the lane threads once the queue is empty. A task queued afterwards still runs.</summary>
    public void Dispose()
    {
        lock (tasks)
        {
            completed = true;
            Monitor.PulseAll(tasks);
        }
    }
}
