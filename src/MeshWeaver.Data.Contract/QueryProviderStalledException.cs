using System.Globalization;

namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 <b>A query provider never answered, so the fan-in has no snapshot to merge — and says so
/// instead of hanging.</b> Raised by <c>MeshQuery.MergeProviderObservables</c>'s stall terminal
/// (<c>InitialStallProbe</c>) and by nothing else.
///
/// <para>🚨 <b>Why it lives in <c>MeshWeaver.Data.Contract</c></b> — the same reason
/// <see cref="MeshWeaver.Data.StorageFaults"/> does, and stated there: the layers that have to
/// recognise this fault sit on OPPOSITE sides of the assembly graph.
/// <c>MeshWeaver.Hosting</c>'s fan-in raises it, <c>MeshWeaver.Graph</c>'s RLS validator names it in
/// its unavailability report, and <c>MeshWeaver.Layout</c>'s <c>AreaErrorClassifier</c> has to
/// classify it — but <c>MeshWeaver.Mesh.Contract</c> REFERENCES <c>MeshWeaver.Layout</c>, so a type
/// declared there is invisible to the classifier. Matching it by type NAME instead would be a
/// classification by string, which this tree deliberately does not do.</para>
///
/// <para><b>What it means, exactly.</b> The merged <c>Initial</c> frame gates on EVERY registered
/// <c>IMeshQueryProvider</c> emitting one, because the frame is the union of their slices and a
/// missing slice is indistinguishable from an empty one. A provider that COMPLETES without an
/// Initial is counted as empty and NAMED on the frame
/// (<c>QueryResultChange.SilentProviders</c>, MeshWeaver#4557). A provider that neither
/// emits, completes NOR errors has no such representation: it starved the gate for ever. Until
/// this type existed the consumer simply hung — with no error, no log line at the consumer and
/// nothing to grep. Measured on <c>memex</c> over 400 minutes to 2026-09-21T04:12Z: 95
/// <c>No MeshNode emitted for</c> faults (~14/hour) plus 200+ stall-probe warnings that nothing
/// acted on.</para>
///
/// <para>🚨 <b>It is an availability failure, never a verdict.</b> Every consumer that turns a mesh
/// read into a decision has to read it that way: an access-control fold that cannot read its grants
/// answers <c>Undetermined</c> / <c>Unavailable</c> (fail CLOSED, retryable, attributed to the
/// read) — never "denied", which would send a correctly-entitled caller to request permissions they
/// already hold, and never "granted", which would be a hole. See
/// <c>Doc/Architecture/AccessControl</c> → "The fold can produce NO answer, and that is a third
/// outcome".</para>
///
/// <para>🚨 <b>Deliberately NOT a <see cref="TimeoutException"/>.</b> Several catch arms in the tree
/// match that type to mean "MY OWN bound elapsed" and then print their own budget
/// (<c>MeshOperations</c>, <c>MeshNodeCompilationService</c>, <c>BuildProtocolDriver</c>); inheriting
/// it would hand them a fault they would re-attribute to themselves, which is exactly the
/// misattribution <c>StreamPostGuardTimeoutException</c> was minted to prevent, in reverse. The
/// distinct type also keeps it out of <c>TransientStorageFaults.RetryTransientConnect</c>'s class:
/// a stalled provider must be FIXED, never retried behind the caller's back.</para>
///
/// <para><b>The budget is derived, never configured here</b> —
/// <c>MeshWeaver.Mesh.Services.MeshOperationOptions.QueryInitialBudget</c> is rung 4 of the one ladder
/// (<c>Timeout</c> → <c>NestedTimeout</c> → <c>PermissionEstablishmentBudget</c> →
/// <c>QueryInitialBudget</c>), so this terminal is strictly quicker to fire than the permission
/// fold's own bound that encloses it. That ordering is the whole point: this is the only level that
/// can name WHICH provider starved (issue #1198).</para>
///
/// <para>🚨 <b>The process's ThreadPool reading rides on the fault</b> (MeshWeaver#8100). A provider
/// is where the fan-in SEES a stall, not necessarily where it comes from. When a whole replica is
/// backlogged, every continuation (an Npgsql connector open, a provider's Initial) waits in the same
/// global queue, and each standing catalog on that replica files its own "provider stalled" ticket
/// for one process-wide backlog. Measured on one replica on 2026-11-11 at 14:40Z: 48,807 queued work
/// items over 42 threads, while its three siblings read 0, and four catalogs filed four tickets.
/// <see cref="ThreadPoolPending"/> and <see cref="ThreadPoolThreads"/> are read when the stall
/// terminal RUNS, and the message says which reading it is, so triage reads the process before the
/// provider. 🚨 That is not the instant the bound elapsed: the terminal is an <c>Observable.Timer</c>
/// callback on Rx's default scheduler, i.e. a ThreadPool work item, so on a backlogged replica it
/// runs only once a worker reaches it — later than the bound by about the queue delay. The reading is
/// of the queue at that later moment. A late terminal is itself a symptom of the backlog, so the
/// reading still points the right way, but it is neither exact at the bound nor a bound on the
/// queue's peak. See <c>Doc/Architecture/QueryFanInStallTerminal</c> → "A stall on a backlogged
/// replica".</para>
/// </summary>
public sealed class QueryProviderStalledException
    : Exception, MeshWeaver.Messaging.IClassifiedFault
{
    /// <summary>The queued-work-item count at or above which the message reads the REPLICA as
    /// backlogged rather than the provider as slow. Healthy replicas read 0 to a few hundred; the
    /// stall episodes on record read thousands to tens of thousands.</summary>
    public const long BackloggedPendingThreshold = 1_000;

    /// <summary>Raises the terminal without a process reading — the shape a caller that fabricates a
    /// stall uses.</summary>
    /// <param name="providers">Comma-joined names of the providers that had not emitted an Initial.</param>
    /// <param name="budget">The fan-in's own bound, i.e. the budget that elapsed.</param>
    /// <param name="query">The query text the fan-in was serving, for attribution.</param>
    /// <param name="userId">The identity the query ran under, for attribution.</param>
    public QueryProviderStalledException(string providers, TimeSpan budget, string? query, string? userId)
        : this(providers, budget, query, userId, threadPoolPending: null, threadPoolThreads: null)
    {
    }

    /// <summary>Raises the terminal with the process's ThreadPool reading, taken when the terminal ran.</summary>
    /// <param name="providers">Comma-joined names of the providers that had not emitted an Initial.</param>
    /// <param name="budget">The fan-in's own bound, i.e. the budget that elapsed.</param>
    /// <param name="query">The query text the fan-in was serving, for attribution.</param>
    /// <param name="userId">The identity the query ran under, for attribution.</param>
    /// <param name="threadPoolPending">The ThreadPool's queued work items when the terminal ran, or
    /// <c>null</c> when it was not read.</param>
    /// <param name="threadPoolThreads">The ThreadPool's thread count when the terminal ran, or
    /// <c>null</c> when it was not read.</param>
    /// <exception cref="ArgumentException">Exactly one of the two readings is given. They are one
    /// reading of one pool and travel as a PAIR: both, or neither. A half reading would render as no
    /// reading and silently drop the half it did carry.</exception>
    public QueryProviderStalledException(
        string providers, TimeSpan budget, string? query, string? userId,
        long? threadPoolPending, int? threadPoolThreads)
        : base(Compose(providers, budget, query, userId,
            RequirePair(threadPoolPending, threadPoolThreads), threadPoolThreads))
    {
        Providers = providers;
        Budget = budget;
        Query = query;
        UserId = userId;
        ThreadPoolPending = threadPoolPending;
        ThreadPoolThreads = threadPoolThreads;
    }

    private static long? RequirePair(long? threadPoolPending, int? threadPoolThreads)
        => threadPoolPending.HasValue == threadPoolThreads.HasValue
            ? threadPoolPending
            : throw new ArgumentException(
                "The ThreadPool reading is one reading of one pool: pass both threadPoolPending and "
                + "threadPoolThreads, or neither.",
                threadPoolPending.HasValue ? nameof(threadPoolThreads) : nameof(threadPoolPending));

    private static string Compose(
        string providers, TimeSpan budget, string? query, string? userId,
        long? threadPoolPending, int? threadPoolThreads)
    {
        var head = $"Query provider(s) [{providers}] did not emit an Initial within the query fan-in's "
            + FormattableString.Invariant($"{budget.TotalSeconds:0.###}")
            + $"s bound for query '{query}' (user '{userId}'). The merged "
            + "Initial gates on EVERY provider, so this query has NO snapshot to answer with — it is "
            + "reported as unavailable (retryable) rather than left hanging with no error. This is an "
            + "availability failure, never a permission verdict: a consumer deciding access must fail "
            + "CLOSED and say it could not establish the answer.";
        if (threadPoolPending is not { } pending)
            return head + " Fix the stalled provider; never bump the consumer's timeout.";
        var threads = threadPoolThreads!.Value.ToString(CultureInfo.InvariantCulture);
        var reading = " When this terminal ran (a ThreadPool callback, so on a backlogged replica later "
            + "than the bound) this process's ThreadPool held "
            + pending.ToString(CultureInfo.InvariantCulture) + " queued work item(s) over "
            + threads + " thread(s)";
        return pending >= BackloggedPendingThreshold
            ? head + reading + " — the REPLICA was backlogged: every continuation (this provider's "
              + "Initial, a store connector's open) waited in the same queue, so the provider is only "
              + "where the fan-in saw the stall. Read this replica's [LIVENESS] and [POOLSTALL] lines "
              + "for what flooded the pool before reading the provider; never bump the consumer's timeout."
            : head + reading + " — the process was not backlogged, so the provider itself did not "
              + "answer. Fix the stalled provider; never bump the consumer's timeout.";
    }

    /// <summary>A transient fault of the store as a whole — <see cref="MeshWeaver.Messaging.FaultLayer.StoreStall"/>,
    /// kept apart from an unreachable store so the connect retry never resubscribes a stall.</summary>
    public string ClassifiedKind => MeshWeaver.Messaging.FaultKind.Transient;

    /// <inheritdoc />
    public string? ClassifiedLayer => MeshWeaver.Messaging.FaultLayer.StoreStall;

    /// <summary>Comma-joined names of the providers that had not emitted an Initial when the
    /// fan-in's bound elapsed — the attribution nothing above this level can produce.</summary>
    public string Providers { get; }

    /// <summary>The fan-in's own bound (<c>MeshOperationOptions.QueryInitialBudget</c>), never a
    /// consumer's.</summary>
    public TimeSpan Budget { get; }

    /// <summary>The query text the fan-in was serving.</summary>
    public string? Query { get; }

    /// <summary>The identity the query ran under.</summary>
    public string? UserId { get; }

    /// <summary>The ThreadPool's queued work items when the stall terminal ran (a ThreadPool callback,
    /// so on a backlogged replica later than the bound), or <c>null</c> when the raiser did not read
    /// it. At or above <see cref="BackloggedPendingThreshold"/> the replica, not the provider, is the
    /// first place to look. Always non-null together with <see cref="ThreadPoolThreads"/>.</summary>
    public long? ThreadPoolPending { get; }

    /// <summary>The ThreadPool's thread count when the stall terminal ran, or <c>null</c> when the
    /// raiser did not read it. Always non-null together with <see cref="ThreadPoolPending"/>.</summary>
    public int? ThreadPoolThreads { get; }
}
