using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// A read whose owning hub never answered within the caller's budget. Derives from
/// <see cref="TimeoutException"/> on purpose: every classifier in the framework
/// (<c>AreaErrorClassifier.IsTransientHubFailure</c>,
/// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c>, <c>RoutingGrain.IsTransientFailure</c>)
/// already treats a <see cref="TimeoutException"/> as a TRANSIENT owner miss worth a retry, and a
/// brand-new exception type would have silently fallen out of all three — turning a retryable stall
/// into a negative-cached "missing node".
///
/// <para>It carries the two facts a bare timeout does not: WHICH address never answered, and WHAT
/// budget expired. Those are what let a caller answer "temporarily unavailable, retry" instead of
/// "not found" or a generic 500.</para>
/// </summary>
public sealed class HubUnreachableException : TimeoutException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The full diagnostic message (see <see cref="ReadBudget"/>).</param>
    /// <param name="target">The address that never answered.</param>
    /// <param name="budget">The budget that expired.</param>
    /// <param name="innerException">The underlying cause, when there is one.</param>
    public HubUnreachableException(string message, string target, TimeSpan budget, Exception? innerException = null)
        : base(message, innerException)
    {
        Target = target;
        Budget = budget;
    }

    /// <summary>The address whose hub never answered.</summary>
    public string Target { get; }

    /// <summary>The wall-clock budget that expired.</summary>
    public TimeSpan Budget { get; }
}

/// <summary>
/// 🚨 THE CALLER-SIDE BUDGET ON A READ WHOSE TARGET HUB MAY BE UNREACHABLE.
///
/// <para><b>The problem this exists for.</b> <c>MessageHub.Observe</c> bounds a request/response
/// exchange with the hub's <c>RequestTimeout</c> — <b>60 s</b>, one value for the whole process.
/// That is the framework's LAST-RESORT terminal, not a budget any particular read chose: it is the
/// number that has to cover a cold NodeType compile as well as a same-process warm reply. When the
/// target hub is unreachable, still starting, or its reply is dropped in transit, every caller that
/// applied no budget of its own therefore waits the full minute and then reports the hub's own
/// impatience — <c>"No response received in hub X within 00:01:00 … the target hub was not
/// found"</c> — which names neither what was being read nor what to do about it. Three production
/// incidents, one shape: Systemorph/MeshWeaver#1563, #1693, #1748.</para>
///
/// <para><b>The rule.</b> Same rule <c>MeshOperationOptions</c> states for writes, applied to
/// reads: <i>a bound nested inside another bound must be able to fire FIRST, because it is the only
/// one that knows WHICH read starved.</i> An interactive read seam — an HTTP endpoint, a GUI
/// binding, anything with a human waiting — carries its own budget, strictly inside the hub's
/// <c>RequestTimeout</c>, and reports the failure in its own terms.</para>
///
/// <para><b>The number is not new.</b> <see cref="Default"/> is 10 s — the budget
/// <c>MeshNodeStreamExtensions.GetMeshNode</c> has always defaulted to, and the band every other
/// interactive read in the GUI already uses (5–15 s across <c>NamedAreaView</c>,
/// <c>ThreadChatView</c>, <c>NavigationService</c>, <c>MeshNodePickerView</c>). This type does not
/// introduce a budget; it applies the existing one at the two seams that were missing it.</para>
///
/// <para>🚨 <b>READS ONLY.</b> A caller-side ceiling on a WRITE makes the caller abandon an
/// operation that is still running — it cancels nothing, and the continuation outlives the caller's
/// DI scope (MeshWeaver#1270 tried exactly that and got <c>ObjectDisposedException</c> on a
/// thread-pool thread after the test had reported success). Everything here is for an idempotent
/// read whose only effect is the answer; bound a write with the hub's <c>RequestTimeout</c>, which
/// is enforced where the request actually lives.</para>
///
/// <para><b>Two dispositions, and choosing between them is the whole design decision</b> — see
/// <see cref="FailIfNoFirstEmission{T}"/> (one-shot reads: ERROR) and
/// <see cref="DegradeIfNoFirstEmission{T}"/> (live bindings: emit a fallback and KEEP TRYING).
/// Neither ever completes empty: Rx's own <c>Timeout</c> overload that swaps in
/// <c>Observable.Empty</c> passes a COMPLETION downstream, which a reader cannot tell apart from
/// "there is genuinely nothing here" — the exact silent-failure shape this repo bans.</para>
/// </summary>
public static class ReadBudget
{
    /// <summary>
    /// The default caller-side read budget: <b>10 seconds</b>. Deliberately identical to
    /// <c>GetMeshNode</c>'s long-standing default so the framework has ONE interactive read budget
    /// rather than a new one per call site, and comfortably inside the 60 s hub
    /// <c>RequestTimeout</c> so this bound is the one that fires — and therefore the one that gets
    /// to say which read starved.
    /// </summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Bounds the wait for <paramref name="source"/>'s FIRST notification and <b>errors</b> with a
    /// <see cref="HubUnreachableException"/> when it does not arrive.
    ///
    /// <para>Only the FIRST notification is bounded. Once the source produces anything — a value or
    /// its own error — the budget timer is unsubscribed and the source flows untouched, so a live
    /// stream that legitimately sits idle afterwards is never cut short. (Rx's plain
    /// <c>Timeout(TimeSpan)</c> applies BETWEEN consecutive elements and would fault exactly that
    /// healthy idle stream; <see cref="Observable.Amb{TSource}(IObservable{TSource}, IObservable{TSource})"/>
    /// is the shape the framework already uses for this in
    /// <c>MessageHubGrain.BuildActivationChain</c>.)</para>
    ///
    /// <para>Use this for a ONE-SHOT read — a request/response exchange, an HTTP endpoint — where
    /// the caller has to answer now and "no answer" is a real outcome it must report.</para>
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The read to bound.</param>
    /// <param name="reader">The hub issuing the read; its pending-request snapshot goes in the
    /// diagnostic. May be null when there is no hub to interrogate.</param>
    /// <param name="target">The address expected to answer.</param>
    /// <param name="what">A short description of the read, e.g. <c>"content collection config"</c>.</param>
    /// <param name="budget">The budget; <see cref="Default"/> when omitted.</param>
    /// <param name="scheduler">Clock for the budget — pass a <c>TestScheduler</c> to drive it in
    /// virtual time (same seam as <c>MessageHubGrain.BuildActivationChain</c>).</param>
    /// <returns>The bounded source.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The budget is not positive.</exception>
    public static IObservable<T> FailIfNoFirstEmission<T>(
        this IObservable<T> source,
        IMessageHub? reader,
        string target,
        string what,
        TimeSpan? budget = null,
        IScheduler? scheduler = null)
    {
        var effective = Validate(budget);
        return Observable.Amb(
            source,
            Observable.Timer(effective, scheduler ?? Scheduler.Default)
                .SelectMany(_ => Observable.Throw<T>(
                    Unreachable(reader, target, what, effective))));
    }

    /// <summary>
    /// Bounds the wait for <paramref name="source"/>'s FIRST value and, when it does not arrive,
    /// emits <paramref name="fallback"/> and <b>stays subscribed</b>.
    ///
    /// <para>This is the disposition for a LIVE binding, and the difference from
    /// <see cref="FailIfNoFirstEmission{T}"/> is deliberate in both directions:</para>
    /// <list type="bullet">
    ///   <item><b>It does not error</b>, because an error tears the subscription down — and a hub
    ///     that is merely slow (a cold NodeType compile legitimately outruns any interactive
    ///     budget) would then never populate the control at all. Here the late value still lands
    ///     and replaces the fallback.</item>
    ///   <item><b>It does not complete</b>, for the same reason plus the empty-completion trap: a
    ///     completed binding is indistinguishable from a field that has no more values.</item>
    ///   <item><b>It is not silent.</b> <paramref name="onDegraded"/> fires with the same
    ///     <see cref="HubUnreachableException"/> the failing disposition would have thrown, so the
    ///     caller logs/surfaces WHICH node did not answer and within what budget. Emitting the
    ///     fallback with no record is the silent-empty failure this repo bans.</item>
    /// </list>
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The live read to bound.</param>
    /// <param name="fallback">What to emit when nothing arrived in time — the "we have nothing
    /// yet" value the consumer would render for an absent field.</param>
    /// <param name="onDegraded">Called exactly once, with the attributable failure, when the
    /// fallback is emitted. Never called when a real value arrived first.</param>
    /// <param name="reader">The hub issuing the read (diagnostics only; may be null).</param>
    /// <param name="target">The address expected to answer.</param>
    /// <param name="what">A short description of the read.</param>
    /// <param name="budget">The budget; <see cref="Default"/> when omitted.</param>
    /// <param name="scheduler">Clock for the budget (virtual time in tests).</param>
    /// <returns>The bounded source.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The budget is not positive.</exception>
    public static IObservable<T> DegradeIfNoFirstEmission<T>(
        this IObservable<T> source,
        T fallback,
        Action<HubUnreachableException> onDegraded,
        IMessageHub? reader,
        string target,
        string what,
        TimeSpan? budget = null,
        IScheduler? scheduler = null)
    {
        var effective = Validate(budget);
        return Observable.Create<T>(observer =>
            {
                // 0 = nothing has been emitted yet. Whoever flips it to 1 first wins: a real value
                // cancels the degradation, and the degradation cannot fire twice.
                var emitted = 0;
                var subscription = source.Subscribe(
                    value =>
                    {
                        Interlocked.Exchange(ref emitted, 1);
                        observer.OnNext(value);
                    },
                    observer.OnError,
                    observer.OnCompleted);
                var timer = Observable.Timer(effective, scheduler ?? Scheduler.Default)
                    .Subscribe(_ =>
                    {
                        if (Interlocked.CompareExchange(ref emitted, 1, 0) != 0)
                            return;
                        var failure = Unreachable(reader, target, what, effective);
                        try
                        {
                            onDegraded(failure);
                        }
                        catch
                        {
                            // Reporting the degradation must never replace it: the consumer still
                            // needs the fallback so its control draws instead of spinning.
                        }
                        observer.OnNext(fallback);
                    });
                return new CompositeDisposable(subscription, timer);
            })
            // The value path and the budget timer run on different threads; Rx's grammar requires
            // serialized notifications, and the CAS above only guarantees the degradation is not
            // DUPLICATED, not that it cannot interleave with an in-flight OnNext.
            .Synchronize();
    }

    private static TimeSpan Validate(TimeSpan? budget)
    {
        var effective = budget ?? Default;
        return effective > TimeSpan.Zero
            ? effective
            : throw new ArgumentOutOfRangeException(nameof(budget), effective,
                "A read budget must be positive — a non-positive budget expires before the read is "
                + "even posted, which reports 'unreachable' about a hub nobody asked.");
    }

    /// <summary>
    /// The failure a lapsed budget produces, carrying everything needed to tell the three causes
    /// apart WITHOUT re-running anything: the reader's own in-flight snapshot (our request still
    /// pending ⇒ the reply never came), and whether the target hub exists in this process at all
    /// (⇒ it never activated here, or it is owned by another silo and the reply was lost in
    /// transit — MeshWeaver#1742).
    /// </summary>
    private static HubUnreachableException Unreachable(
        IMessageHub? reader, string target, string what, TimeSpan budget)
    {
        var readerState = Describe(() => reader?.GetPendingRequestDiagnostics() ?? "<no reader hub>");
        var targetState = Describe(() => DescribeTarget(reader, target));
        return new HubUnreachableException(
            $"Reading {what} from '{target}' gave up after {budget.TotalSeconds:F0}s — the owning hub "
            + "never answered. This is NOT 'not found' and NOT a denial: no verdict was reached, so "
            + $"the read is retryable. Reader: {readerState} {targetState}",
            target,
            budget);
    }

    private static string DescribeTarget(IMessageHub? reader, string target)
    {
        if (reader is null)
            return "Target: <not probed — no reader hub>.";
        if (string.Equals(reader.Address.ToString(), target, StringComparison.Ordinal))
            return "Target: this hub itself.";
        // HostedHubCreation.Never — a pure dictionary probe. Diagnosing a hub must never activate it.
        return reader.GetHostedHub(new Address(target), HostedHubCreation.Never) is { } owner
            ? $"Target: {owner.GetPendingRequestDiagnostics()}"
            : $"Target: NO LOCAL HUB at '{target}' — it never activated in this process (or it is "
              + "owned by another silo and the reply was lost in transit).";
    }

    private static string Describe(Func<string> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            // Diagnostics run on the budget timer's thread, where the hub may already be torn down.
            // A throw here would be an unobserved fault on a pool thread — precisely the class of
            // failure this type exists to remove.
            return $"<diagnostics unavailable: {ex.GetType().Name}>";
        }
    }

    /// <summary>
    /// The logger every read-budget degradation reports on, so one channel covers every seam.
    /// </summary>
    /// <param name="hub">The hub whose service provider owns the logger factory.</param>
    /// <returns>The logger, or null when none can be resolved.</returns>
    public static ILogger? Logger(IMessageHub? hub)
    {
        try
        {
            return hub?.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Mesh.ReadBudget");
        }
        catch
        {
            return null;
        }
    }
}
