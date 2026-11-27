using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace MeshWeaver.Messaging;

/// <summary>One thing the shared stale-callback ticker scans: a hub.</summary>
public interface IStaleCallbackScanTarget
{
    /// <summary>One scan pass; must not throw. Returns <c>false</c> when the thing scanned is gone
    /// (collected), which retires the registration.</summary>
    bool ScanStaleCallbacks();
}

/// <summary>
/// The ONE process-wide ticker behind every hub's stale-callback scan (Systemorph/MeshWeaver#5388).
///
/// <para><b>The shape this removes.</b> Each hub used to start its own <c>Observable.Interval(5 s)</c>.
/// That is one <c>System.Threading.Timer</c> per hub, and a timer that fires is a ThreadPool work item
/// per firing. On memex-cloud, which keeps tens of thousands of per-node hubs (a mailbox is one hub per
/// mail), the thread-pool queue census read 4,000-6,300 of 9,000-14,800 queued items as
/// <c>ConcurrencyAbstractionLayerImpl+PeriodicTimer</c> (2026-11-27, pod portal 4.0.14289), against
/// none on memex, which holds far fewer hubs. The timers were also phase-locked: hubs that activate
/// in one burst (a page opening a mailbox, a module landing) share the same first tick and the same
/// period forever, so every 5 s the whole wave enqueues at once, ahead of the socket events that
/// carry user traffic.</para>
///
/// <para><b>The fix is one timer.</b> Hubs register here (through an adapter that holds the hub weakly: the scanner is a diagnostic and must
/// never keep a hub alive) and ONE tick walks the registrations on ONE pool item. N hubs cost
/// one timer and one work item per interval instead of N of each. The scan itself is unchanged.</para>
/// </summary>
public sealed class StaleCallbackScanRegistry
{
    /// <summary>The process-wide registry every hub registers with.</summary>
    public static StaleCallbackScanRegistry Shared { get; } = new(TimeSpan.FromSeconds(5));

    private readonly TimeSpan interval;
    private readonly IScheduler? scheduler;
    private readonly ConcurrentDictionary<long, IStaleCallbackScanTarget> targets = new();
    private readonly object timerGate = new();
    private IDisposable? timer;
    private long nextId;
    private int timersStarted;

    /// <summary>Creates a registry that scans every <paramref name="interval"/>. Applications use
    /// <see cref="Shared"/>; the scheduler is for tests.</summary>
    public StaleCallbackScanRegistry(TimeSpan interval, IScheduler? scheduler = null)
    {
        this.interval = interval;
        this.scheduler = scheduler;
    }

    /// <summary>How many timers this registry has started: 1 once anything registered, however many
    /// did. The number the one-timer invariant is pinned on.</summary>
    public int TimersStarted => Volatile.Read(ref timersStarted);

    /// <summary>The live registrations.</summary>
    public int Count => targets.Count;

    /// <summary>Registers <paramref name="target"/>; dispose the result to stop scanning it. The
    /// registry holds the TARGET strongly, so the target must hold what it scans weakly (a hub
    /// adapter does) and report <c>false</c> once that is gone.</summary>
    public IDisposable Register(IStaleCallbackScanTarget target)
    {
        var id = Interlocked.Increment(ref nextId);
        targets[id] = target;
        EnsureTimer();
        return Disposable.Create(() => targets.TryRemove(id, out _));
    }

    private void EnsureTimer()
    {
        if (timer is not null) return;
        lock (timerGate)
        {
            if (timer is not null) return;
            Interlocked.Increment(ref timersStarted);
            timer = (scheduler is null ? Observable.Interval(interval) : Observable.Interval(interval, scheduler))
                .Subscribe(_ => Tick());
        }
    }

    private void Tick()
    {
        foreach (var (id, target) in targets)
        {
            bool alive;
            try { alive = target.ScanStaleCallbacks(); }
            catch { alive = true; /* a diagnostic: one hub's failed scan must not stop the others */ }
            if (!alive) targets.TryRemove(id, out _);
        }
    }
}
