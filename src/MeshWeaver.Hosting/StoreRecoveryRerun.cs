using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

/// <summary>
/// How a FINITE store-reading job — the boot warm-up sweep — is re-run after a transient store
/// fault: how many re-runs a timer may trigger, and how long each waits. Registered as a mesh
/// singleton by <see cref="PreWarmServiceCollectionExtensions.AddDynamicTypePreWarming"/>; a host
/// that registers none gets <see cref="Default"/>.
/// </summary>
public sealed class StoreRecoveryRerunPolicy
{
    /// <summary>
    /// The production policy: up to <see cref="StandingWatchRecovery.DefaultMaxConsecutive"/> timed
    /// re-runs on <see cref="StandingWatchRecovery.DefaultBackoff"/> (2 s, 5 s, 15 s, 30 s, 60 s —
    /// about two minutes in all), the same schedule a standing watch re-opens on.
    /// </summary>
    public static StoreRecoveryRerunPolicy Default { get; } = new();

    /// <summary>
    /// Re-runs a TIMER may trigger. After that the job is re-run only on the store's own recovery
    /// signal (a change-feed gap closing), never on a clock again.
    /// </summary>
    public int MaxTimedReruns { get; init; } = StandingWatchRecovery.DefaultMaxConsecutive;

    /// <summary>The delay before timed re-run number n (1-based).</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = StandingWatchRecovery.DefaultBackoff;
}

/// <summary>One scheduled re-run, as told to the caller BEFORE it waits, so the gap is never silent.</summary>
/// <param name="Fault">The transient fault that ended the previous attempt.</param>
/// <param name="Rerun">Which re-run this will be (1-based).</param>
/// <param name="MaxTimedReruns">How many re-runs a timer may trigger in all.</param>
/// <param name="Backoff">The timer's delay, or <see langword="null"/> once the timed re-runs are
/// spent and only the store's recovery signal can trigger it.</param>
public sealed record StoreRecoveryRerunScheduled(
    Exception Fault, int Rerun, int MaxTimedReruns, TimeSpan? Backoff)
{
    /// <summary>What will trigger the re-run, in one clause — for the log line and the health payload.</summary>
    public string Trigger => Backoff is { } wait
        ? $"when the change feed declares the store reachable again, or in {wait.TotalSeconds:0.#}s "
          + $"(re-run {Rerun} of at most {MaxTimedReruns} timed)"
        : $"when the change feed declares the store reachable again (re-run {Rerun}; the "
          + $"{MaxTimedReruns} timed re-runs are spent, so no clock will trigger it)";
}

/// <summary>
/// 🚨 <b>A finite job that faulted on a TRANSIENT store fault is re-run once the store is reachable
/// again — on the same process</b> (MeshWeaver#7066).
///
/// <para><b>The defect this closes.</b> The boot warm-up sweep that feeds the <c>nodetype_bake</c>
/// readiness gate ran ONCE. On 2026-10-22 a managed-database restart refused connections for about
/// 40 seconds (#7068); one pod's sweep met that refusal on <c>Admin/Build</c> at 21:22:55Z, recorded
/// <see cref="BakePhase.Faulted"/>, and nothing ever ran it again. The database was back at about
/// 21:23:34Z; the pod refused readiness until it was replaced at 22:37Z, and the roll could not
/// converge in between. Under the readiness-only policy nothing restarts a refused pod, so "a
/// restart re-runs the sweep" was not a recovery path at all.</para>
///
/// <para><b>What triggers the re-run — the store's own recovery signal first.</b> A backend whose
/// cross-process feed can drop declares a <see cref="ChangeFeedGap"/> when it re-opens its session
/// (the PostgreSQL listener after a lost connection): that is the platform saying, with a
/// timestamp, that the store accepts connections again. The re-run subscribes to it after the fault
/// and runs on the first gap. It is not sufficient alone — a gap is declared only after the feed had
/// come up once, may already have been declared before the fault surfaced (a slow read failing after
/// the store returned), and a backend with no cross-process feed declares none — so a bounded timer
/// (<see cref="StoreRecoveryRerunPolicy"/>) races it for the first few re-runs, and after those only
/// the signal can trigger one. Whichever fires, the re-run is logged and named in the health
/// payload before it waits.</para>
///
/// <para><b>What it deliberately does NOT re-run.</b> Only <see cref="StandingWatchRecovery.IsTransient"/>
/// faults: a database connect/IO fault, the fan-in's stall terminal, or a store fault an owner
/// classified before answering across the hub boundary (<c>MeshNodeError.Transient</c>). A compile
/// error, a query or schema error, a deserialization defect — anything else — surfaces unchanged on
/// its first occurrence and stays a named failure; re-running a defect only repeats it.</para>
///
/// <para>Nothing polls: between attempts the only scheduled work is the one timer, and once the
/// timed re-runs are spent there is none. Nothing parks a thread, and no state is static.</para>
/// </summary>
public static class StoreRecoveryRerun
{
    /// <summary>
    /// The store's recovery signal for <paramref name="mesh"/>: each <see cref="ChangeFeedGap"/> its
    /// invalidation feed declares (delivery resumed, so the store accepts connections). Never emits
    /// on a mesh whose feed has no cross-process source.
    /// </summary>
    /// <param name="mesh">The mesh hub.</param>
    public static IObservable<ChangeFeedGap> RecoverySignal(IMessageHub mesh)
        => mesh.ServiceProvider.GetService<IMeshInvalidationFeed>()?.Gaps
           ?? Observable.Never<ChangeFeedGap>();

    /// <summary>
    /// Runs <paramref name="attempt"/>, and after a <see cref="StandingWatchRecovery.IsTransient"/>
    /// fault runs it again — on the first <paramref name="storeRecovered"/> signal after the fault,
    /// or (for the first <see cref="StoreRecoveryRerunPolicy.MaxTimedReruns"/> re-runs) when the
    /// policy's backoff elapses, whichever comes first. Any other fault surfaces unchanged.
    /// </summary>
    /// <typeparam name="T">The job's emission.</typeparam>
    /// <param name="attempt">Cold factory for one attempt, given its re-run number (0 = the first
    /// run). Re-invoked per re-run, so it must build a FRESH upstream.</param>
    /// <param name="storeRecovered">The store's recovery signal (hot); subscribed only while a
    /// re-run is pending, so a recovery declared before the fault does not count.</param>
    /// <param name="policy">How many timed re-runs, and their backoff.</param>
    /// <param name="scheduler">Scheduler for the backoff timer.</param>
    /// <param name="onScheduled">Told each re-run BEFORE it waits — the caller records the fault and
    /// the pending re-run, so the gap between attempts is visible.</param>
    /// <param name="isTransient">The classification; <see cref="StandingWatchRecovery.IsTransient"/>
    /// when null.</param>
    /// <returns>Every attempt's emissions, completing when an attempt completes.</returns>
    public static IObservable<T> RerunOnStoreRecovery<T>(
        Func<int, IObservable<T>> attempt,
        IObservable<ChangeFeedGap> storeRecovered,
        StoreRecoveryRerunPolicy policy,
        IScheduler scheduler,
        Action<StoreRecoveryRerunScheduled> onScheduled,
        Func<Exception, bool>? isTransient = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(storeRecovered);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(onScheduled);
        var classify = isTransient ?? StandingWatchRecovery.IsTransient;
        var timedBudget = Math.Max(0, policy.MaxTimedReruns);

        // ONE flat pipeline re-subscribed in place by RetryWhen (StandingWatchRecovery's shape, for
        // the same reason: no Catch nesting one layer deeper per fault survived).
        return Observable.Defer(() =>
        {
            // Per outer subscription: the number of the attempt the next subscription starts.
            var next = 0;
            return Observable.Defer(() => attempt(next++))
                .RetryWhen(faults => faults.SelectMany(fault =>
                {
                    if (!classify(fault))
                        return Observable.Throw<Unit>(fault);
                    var rerun = next;
                    TimeSpan? wait = rerun <= timedBudget ? policy.Backoff(rerun) : null;
                    var recovered = storeRecovered.Take(1).Select(_ => Unit.Default);
                    // LISTEN FIRST, RECORD SECOND, RE-RUN THIRD. The trigger is subscribed before the
                    // caller is told, so a recovery declared while the caller records the fault is
                    // not lost; it lands in a one-shot subject that hands it to the re-subscription
                    // only after the caller has returned, so the re-run can never start (and mark
                    // itself running) before the fault it re-runs has been recorded. The listening
                    // subscription is owned by the returned chain: disposing the job, or the trigger
                    // firing, releases it.
                    var trigger = wait is { } delay
                        ? recovered.Amb(Observable.Timer(delay, scheduler).Select(_ => Unit.Default))
                        : recovered;
                    var fired = new AsyncSubject<Unit>();
                    var listening = trigger.Subscribe(fired);
                    try
                    {
                        onScheduled(new StoreRecoveryRerunScheduled(fault, rerun, timedBudget, wait));
                    }
                    catch
                    {
                        // The caller could not record the fault: the re-run is abandoned and the
                        // recording failure surfaces through the selector, unchanged.
                        listening.Dispose();
                        throw;
                    }
                    return fired.Finally(listening.Dispose);
                }));
        });
    }
}
