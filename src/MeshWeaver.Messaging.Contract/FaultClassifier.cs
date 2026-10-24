using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data.Common;
using System.Net.Sockets;

namespace MeshWeaver.Messaging;

/// <summary>
/// WHY a piece of work ended in an exception — the one vocabulary every cancellation, teardown and
/// transient-fault question in the platform is answered in. An open set of string constants
/// (policy <c>open-vocabulary-string-constants</c>): a module may add its own kind and carry it on
/// its exception through <see cref="IClassifiedFault"/>; an unknown kind is treated as
/// <see cref="Fault"/> by every forwarder, never as a benign default.
/// </summary>
public static class FaultKind
{
    /// <summary>A token fired because someone cancelled it — the caller left, a pool drained.
    /// Nothing failed. Never a cancellation raised to express a timeout.</summary>
    public const string CooperativeCancel = nameof(CooperativeCancel);

    /// <summary>Something did not answer in time — a <see cref="TimeoutException"/>, a cancellation
    /// carrying one, or a routed transport timeout flattened into a delivery failure's text.</summary>
    public const string Timeout = nameof(Timeout);

    /// <summary>A DI scope (the Autofac <c>LifetimeScope</c>) was disposed under live work — or,
    /// with a <see cref="TeardownContext.ScopeDisposed"/> probe, the CALLER's own scope is gone.</summary>
    public const string ScopeTeardown = nameof(ScopeTeardown);

    /// <summary>A hub announced that it is going away — <see cref="HubDisposingException"/>,
    /// <see cref="HubDisposedBeforeResponseException"/>, a <see cref="ErrorType.ShuttingDown"/>
    /// delivery failure or its "Hub X is shutting down" banner. Retryable: the address reactivates.</summary>
    public const string HubTeardown = nameof(HubTeardown);

    /// <summary>A dependency was momentarily unreachable; <see cref="FaultClassification.Layer"/>
    /// names which one (<see cref="FaultLayer"/>).</summary>
    public const string Transient = nameof(Transient);

    /// <summary>Everything else — a defect, or a fault no rule recognises. The conservative answer.</summary>
    public const string Fault = nameof(Fault);
}

/// <summary>
/// WHICH dependency a <see cref="FaultKind.Transient"/> fault was in. Open string constants, like
/// <see cref="FaultKind"/>. The split is load-bearing: the consumers ask different questions — the
/// query fan-in retries only an unreachable STORE, a hub's initialization retires its activation on
/// store/contention/network faults, and a view re-subscribes on every one of them.
/// </summary>
public static class FaultLayer
{
    /// <summary>A bare socket failure — DNS, refused, reset — with no driver frame around it.</summary>
    public const string Network = nameof(Network);

    /// <summary>The data store could not be REACHED: a connection-class SQLSTATE, or a driver
    /// exception wrapping a timeout / socket / IO fault.</summary>
    public const string Store = nameof(Store);

    /// <summary>The store answered and the provider itself marked the fault transient
    /// (<see cref="DbException.IsTransient"/>) — a serialization conflict, a deadlock — without it
    /// being a connect-class fault.</summary>
    public const string StoreContention = nameof(StoreContention);

    /// <summary>The query provider stalled as a whole (every query missing its Initial bound
    /// together on a GC or thread-pool stall).</summary>
    public const string StoreStall = nameof(StoreStall);

    /// <summary>A store fault the OWNING hub met and classified as transient before answering
    /// across the hub boundary (<c>MeshNodeError.Transient</c> on a <c>MeshNodeStreamException</c>):
    /// the typed driver exception stayed on the owner, and its classification travelled as data.
    /// Distinct from <see cref="Store"/> so that only a consumer that chose to trust an owner's
    /// verdict (a standing watch) acts on it.</summary>
    public const string OwnerStore = nameof(OwnerStore);

    /// <summary>The message transport / routing — an Orleans rejection mid-deactivation, an
    /// undeliverable or not-found target hub, another hub recycling.</summary>
    public const string Transport = nameof(Transport);
}

/// <summary>
/// An exception that states its own classification, for types that live ABOVE this assembly and so
/// cannot be named by <see cref="FaultClassifier"/> (a sync stream's owner refusal, the query
/// provider's stall). Both members have defaults, so implementing the interface obliges nothing.
/// </summary>
public interface IClassifiedFault
{
    /// <summary>The <see cref="FaultKind"/> this exception represents.</summary>
    string ClassifiedKind => FaultKind.Fault;

    /// <summary>The <see cref="FaultLayer"/> when <see cref="ClassifiedKind"/> is
    /// <see cref="FaultKind.Transient"/>.</summary>
    string? ClassifiedLayer => null;
}

/// <summary>
/// What the caller knows about its OWN lifecycle, which the exception alone cannot say.
/// </summary>
/// <param name="ScopeDisposed">Probe for "the caller's own DI scope is gone". When set, ANY
/// <see cref="ObjectDisposedException"/> in the graph is a <see cref="FaultKind.ScopeTeardown"/>
/// exactly when the probe confirms it — and an Autofac "LifetimeScope disposed" text that the probe
/// does NOT confirm is somebody else's scope, so it is not this caller's teardown. When null, the
/// Autofac text alone is the evidence.</param>
/// <param name="OwnAddress">When set, only a <see cref="HubDisposingException"/> for THIS address is
/// a <see cref="FaultKind.HubTeardown"/>; another hub's disposal is, from here, a transient fault of
/// the transport (<see cref="FaultLayer.Transport"/>).</param>
public sealed record TeardownContext(Func<bool>? ScopeDisposed = null, Address? OwnAddress = null)
{
    /// <summary>
    /// For a caller whose only possible source of an <see cref="ObjectDisposedException"/> is its own
    /// teardown — a sync hub's startup gate, whose pending callbacks are failed with one when the hub
    /// disposes; a stream's own error path. Any disposed-object fault is then its own scope ending.
    /// </summary>
    public static TeardownContext DisposedIsOwnTeardown { get; } = new(static () => true);
}

/// <summary>
/// The verdict of <see cref="FaultClassifier.Classify"/>.
/// </summary>
/// <param name="Kind">The <see cref="FaultKind"/>.</param>
/// <param name="Layer">The <see cref="FaultLayer"/> for a transient fault; null otherwise, and null
/// for an aggregate whose transient branches are in DIFFERENT layers.</param>
/// <param name="IsCancellation">The fault IS a cancellation token firing — every branch's outermost
/// frame is an <see cref="OperationCanceledException"/> that carries no timeout — whatever
/// <see cref="Kind"/> says about why (a cancellation raised by a hub teardown is still one).</param>
/// <param name="ByMessage">The verdict rests on MESSAGE text (what survives a routed failure being
/// flattened to a string) rather than on a type. Callers that must not trust text — the typed
/// store/infrastructure rules — refuse it.</param>
/// <param name="Branches">The per-branch verdicts the aggregate was combined from (one entry for a
/// plain chain). A caller that needs "every branch is X" reads these.</param>
public readonly record struct FaultClassification(
    string Kind,
    string? Layer,
    bool IsCancellation,
    bool ByMessage,
    ImmutableArray<FaultClassification> Branches)
{
    /// <summary>A hub or scope teardown.</summary>
    public bool IsTeardown => Kind is FaultKind.HubTeardown or FaultKind.ScopeTeardown;

    /// <summary>True when there is at least one branch and EVERY branch is a typed (not
    /// message-matched) transient fault in one of <paramref name="layers"/>. A mixed aggregate — one
    /// transient branch, one defect — is not transient: retrying it trades a latch for a loop.</summary>
    /// <param name="layers">The acceptable layers.</param>
    public bool EveryBranchTypedTransientIn(params string[] layers)
    {
        if (Branches.IsDefaultOrEmpty)
            return false;
        foreach (var branch in Branches)
            if (branch.Kind != FaultKind.Transient || branch.ByMessage
                || branch.Layer is null || Array.IndexOf(layers, branch.Layer) < 0)
                return false;
        return true;
    }
}

/// <summary>
/// 🚨 THE ONE classifier for "why did this fail": cooperative cancellation vs timeout vs scope
/// teardown vs hub teardown vs a transient dependency fault vs a real fault. Every predicate that
/// used to answer a slice of this question on its own — and they disagreed (a cancellation that hid
/// a timeout called benign in one place and a fault in another; a teardown found at aggregate index
/// 0 but missed at index 1; the Autofac text copied three times) — is now a forwarder onto it.
///
/// <para><b>Walk.</b> The exception GRAPH, never the <c>InnerException</c> line
/// (<see cref="ExceptionChain"/>'s remarks): each branch of every <see cref="AggregateException"/>
/// is classified on its own, outer frame to inner, and the FIRST frame that decides wins — so a
/// driver exception wrapping a socket fault is a store fault, not a network one. Bounded at
/// <see cref="MaxNodes"/> exceptions, reference-deduplicated, cycle-safe; an unresolved branch is a
/// <see cref="FaultKind.Fault"/>.</para>
///
/// <para><b>Combination.</b> A teardown ANYWHERE in the graph wins — a hub that is going away cannot
/// serve whatever else went wrong alongside, and the next activation re-meets any real defect.
/// Otherwise the most severe branch wins: Fault &gt; Timeout &gt; Transient &gt; teardown-of-another
/// &gt; CooperativeCancel. A real defect is never hidden by a benign sibling.</para>
/// </summary>
public static class FaultClassifier
{
    /// <summary>Node budget per classification — see <see cref="ExceptionChain"/>.</summary>
    private const int MaxNodes = 256;

    /// <summary>Connection-class SQLSTATEs: the server could not be reached or would not serve a
    /// session. Query/schema errors (<c>42P01</c>) and conflicts (<c>40P01</c>) are deliberately
    /// absent.</summary>
    private static readonly FrozenSet<string> StoreUnreachableSqlStates = new[]
    {
        "08000", "08001", "08003", "08004", "08006", // connection_exception family
        "57P01", "57P02", "57P03",                   // admin/crash shutdown, cannot_connect_now
        "53300", "53400",                            // too_many_connections, configuration_limit_exceeded
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Message markers of an unreachable database. Matched by TEXT on purpose: across the Orleans
    /// grain boundary the typed driver exception is flattened to its message, so this is what reaches
    /// a remote reader. A verdict reached through them carries <see cref="FaultClassification.ByMessage"/>.
    /// </summary>
    public static readonly ImmutableArray<string> StoreUnreachableMarkers =
    [
        "Failed to connect",                                    // Npgsql: endpoint unreachable
        "Timeout during connection attempt",                    // Npgsql: connect timed out
        "error connecting",                                     // Npgsql connect wrapper
        "connection reset",                                     // socket reset by peer
        "existing connection was forcibly closed",              // Windows socket reset
        "server closed the connection unexpectedly",            // backend gone mid-query
        "connection pool has been exhausted",                   // pool starvation during a blackout
        "terminating connection due to administrator command",  // 57P01 admin_shutdown / failover
        "the database system is starting up",                   // 57P03 — post-restart warmup
        "the database system is shutting down",                 // 57P03
        "the database system is in recovery mode",              // crash recovery
        "no connection could be made",                          // socket refused
        "connection refused",                                   // socket refused
    ];

    /// <summary>Routed-delivery banners of a target that is momentarily unreachable.</summary>
    private static readonly ImmutableArray<string> TransportUnreachableMarkers =
    [
        "target hub was not found",
        "undeliverable",
        "invalid activation",
        "Rejecting now",
        "Forwarding failed",
    ];

    /// <summary>The routed hub-timeout banner — a <see cref="TimeoutException"/> flattened to text.</summary>
    private const string NoResponseMarker = "No response received in hub";

    /// <summary>
    /// Classifies <paramref name="exception"/>. A null exception is a <see cref="FaultKind.Fault"/>
    /// with no branches — there is nothing benign to say about an unknown.
    /// </summary>
    /// <param name="exception">The fault; may be null.</param>
    /// <param name="context">What the caller knows about its own lifecycle; may be null.</param>
    /// <returns>The verdict.</returns>
    public static FaultClassification Classify(Exception? exception, TeardownContext? context = null)
    {
        if (exception is null)
            return new(FaultKind.Fault, null, false, false, ImmutableArray<FaultClassification>.Empty);

        var walk = new Walk(context);
        var branches = walk.Collect(exception, isHead: true, fallback: null);
        if (branches.IsDefaultOrEmpty)
            branches = [Leaf(FaultKind.Fault)];
        var isCancellation = branches.All(b => b.IsCancellation);

        if (IsScopeTeardown(exception, context))
            return new(FaultKind.ScopeTeardown, null, isCancellation, false, branches);
        if (IsHubTeardown(exception, context))
            return new(FaultKind.HubTeardown, null, isCancellation,
                // Recognised by a BANNER only (no typed teardown frame anywhere): a verdict by message.
                ByMessage: !ExceptionChain.Contains(exception, IsTypedHubTeardownFrame), branches);

        return Combine(branches, isCancellation);
    }

    /// <summary>
    /// True when <paramref name="exception"/> is (or carries anywhere in its graph) the DI container's
    /// "this scope is gone" — an <see cref="ObjectDisposedException"/> naming a disposed Autofac
    /// <c>LifetimeScope</c>. The one copy of that text match.
    /// </summary>
    /// <param name="exception">The fault; may be null.</param>
    public static bool IsDisposedContainer(Exception? exception)
        => ExceptionChain.Contains(exception, IsDisposedContainerFrameCore);

    /// <summary>
    /// True when EVERY branch of <paramref name="exception"/>'s graph — through wrappers and nested
    /// aggregates, bounded and cycle-safe — reaches the disposed-<c>LifetimeScope</c> frame. The
    /// teardown suppressors' rule: a wrapper over a disposed scope BESIDE a real defect carries the
    /// defect, so it is not benign (where <see cref="IsDisposedContainer"/>, which asks whether the
    /// graph CONTAINS one, says yes).
    /// </summary>
    /// <param name="exception">The fault; may be null.</param>
    public static bool EveryBranchIsDisposedContainer(Exception? exception)
        => EveryBranchReaches(exception, IsDisposedContainerFrameCore);

    /// <summary>
    /// True when <paramref name="exception"/> ITSELF is the disposed-<c>LifetimeScope</c>
    /// <see cref="ObjectDisposedException"/> — no walk. For a first-chance handler, which must never
    /// admit an <see cref="AggregateException"/> whose message it would then format (exponential in
    /// the depth of a shared nested graph).
    /// </summary>
    /// <param name="exception">One frame; may be null.</param>
    public static bool IsDisposedContainerFrame(Exception? exception)
        => exception is not null && IsDisposedContainerFrameCore(exception);

    /// <summary>
    /// True when <paramref name="exception"/> carries a <see cref="HubDisposingException"/> raised for
    /// <paramref name="address"/> — this hub's own disposal, as opposed to another hub's.
    /// </summary>
    /// <param name="exception">The fault; may be null.</param>
    /// <param name="address">The hub asking.</param>
    public static bool IsHubDisposalOf(Exception? exception, Address? address)
        => address is not null
           && ExceptionChain.Contains(exception, e =>
               e is HubDisposingException disposing
               && string.Equals(disposing.HubAddress.Path, address.Path, StringComparison.Ordinal));

    /// <summary>A bare network-level fault frame: a <see cref="SocketException"/>.</summary>
    /// <param name="exception">One frame.</param>
    public static bool IsNetworkFault(Exception exception) => exception is SocketException;

    private static bool IsDisposedContainerFrameCore(Exception e) =>
        e is ObjectDisposedException
        && ((e.Message?.Contains("LifetimeScope", StringComparison.OrdinalIgnoreCase) ?? false)
            || (e.Message?.Contains("nested lifetimes cannot be created", StringComparison.OrdinalIgnoreCase) ?? false));

    private static bool IsScopeTeardown(Exception exception, TeardownContext? context)
        => context?.ScopeDisposed is { } probe
            ? ExceptionChain.Contains<ObjectDisposedException>(exception) && probe()
            : IsDisposedContainer(exception);

    private static bool IsHubTeardown(Exception exception, TeardownContext? context)
        => context?.OwnAddress is { } own
            ? IsHubDisposalOf(exception, own)
            : ExceptionChain.Contains(exception, IsHubTeardownFrame);

    private static bool IsHubTeardownFrame(Exception e) =>
        IsTypedHubTeardownFrame(e) || IsHubShutdownBanner(e);

    /// <summary>A hub teardown stated by a TYPE (or a typed delivery-failure kind), never by text.</summary>
    private static bool IsTypedHubTeardownFrame(Exception e) =>
        e is HubDisposingException or HubDisposedBeforeResponseException
        || e is DeliveryFailureException { Failure.ErrorType: ErrorType.ShuttingDown }
        || (e is IClassifiedFault classified && classified.ClassifiedKind == FaultKind.HubTeardown);

    /// <summary>"Hub X is shutting down" / "Host is shutting down" — but never the DATABASE's
    /// "the database system is shutting down", which is a store fault.</summary>
    private static bool IsHubShutdownBanner(Exception e)
    {
        // 🚨 Never read an AggregateException's Message: it is COMPOSED from every branch's message,
        // recursively, so on a graph of nested aggregates reading it is exponential (it overflowed a
        // string in FaultClassifierTest.APathologicallyDeepGraph_Terminates). Its branches are
        // visited on their own, so the aggregate's text adds nothing.
        if (e is DbException or AggregateException)
            return false;
        var message = e.Message ?? string.Empty;
        return message.Contains(" is shutting down", StringComparison.OrdinalIgnoreCase)
               && !message.Contains("database system is shutting down", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One classification's walk over the exception GRAPH. Each node is expanded once per
    /// (head-ness, pending text verdict) and its verdicts are memoized, so a node SHARED by two
    /// branches contributes its verdict to BOTH — whichever branch is visited first — while a graph of
    /// nested aggregates sharing one child stays linear. A node already on the current path is a
    /// cycle that never reached a verdict: a fault. The node budget (<see cref="MaxNodes"/>) bounds
    /// the expansions; an exhausted budget is a fault.
    /// </summary>
    private sealed class Walk(TeardownContext? context)
    {
        // Keyed by the node's REFERENCE: an exception type may override Equals (a provider's driver
        // exception comparing by value), and two distinct instances are two branches, never one.
        private readonly Dictionary<(Exception Node, bool IsHead, FaultClassification? Fallback), ImmutableArray<FaultClassification>> memo
            = new(MemoKeyComparer.Instance);
        private readonly HashSet<Exception> path = new(ReferenceEqualityComparer.Instance);
        private int expansions;

        /// <summary>The distinct verdicts of the branches below <paramref name="node"/>.</summary>
        /// <param name="node">The frame to start from.</param>
        /// <param name="isHead">The frame is the OUTERMOST of its branch — an aggregate's children
        /// inherit the aggregate's head-ness, so a fault wrapping an aggregate of cancellations is not
        /// a cancellation.</param>
        /// <param name="fallback">A verdict an OUTER frame reached by message text only; it stands
        /// when nothing below decides by TYPE, and a typed verdict below replaces it.</param>
        public ImmutableArray<FaultClassification> Collect(Exception node, bool isHead, FaultClassification? fallback)
        {
            var key = (node, isHead, fallback);
            if (memo.TryGetValue(key, out var known))
                return known;
            if (path.Contains(node))
                return [Leaf(FaultKind.Fault)];
            if (++expansions > MaxNodes)
                return [Leaf(FaultKind.Fault)];

            path.Add(node);
            var result = Expand(node, isHead, fallback);
            path.Remove(node);
            memo[key] = result;
            return result;
        }

        private ImmutableArray<FaultClassification> Expand(Exception node, bool isHead, FaultClassification? fallback)
        {
            if (node is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count == 0)
                    return [fallback ?? Leaf(FaultKind.Fault)];
                // The children are judged WITHOUT the outer stand-in: a wrapper's verdict speaks for
                // the aggregate as a whole only when NO child decides anything. Copied into each
                // child, it would turn a defect beside a socket fault into a second transient branch.
                var verdicts = ImmutableArray.CreateBuilder<FaultClassification>();
                foreach (var inner in aggregate.InnerExceptions)
                    foreach (var verdict in Collect(inner, isHead, fallback: null))
                        if (!verdicts.Contains(verdict))
                            verdicts.Add(verdict);
                return fallback is { } standIn && verdicts.All(static v => v == Undecided)
                    ? [standIn]
                    : verdicts.ToImmutable();
            }

            if (Decide(node, isHead, context) is { } decided)
            {
                // A TYPE decides for the whole chain below it (a driver exception wrapping a socket
                // fault is a store fault). A message match only stands in: the typed cause it wraps,
                // if any, is the better witness. So does a typed TIMEOUT over typed store or network
                // evidence: a stalled provider, a socket fault or a driver exception is WHY it timed
                // out — read as a plain timeout, a view would retry a stall and the connect retry and
                // standing-watch recovery would give up on a recoverable outage. A cause-free timeout,
                // or one over nothing typed, still stands as a timeout.
                if (!decided.ByMessage && !(decided.Kind == FaultKind.Timeout && WrapsTypedStoreOrNetworkCause(node.InnerException)))
                    return [decided];
                fallback = decided.ByMessage ? fallback ?? decided : decided;
            }

            return node.InnerException is { } next
                ? Collect(next, isHead: false, fallback)
                : [fallback ?? Leaf(FaultKind.Fault)];
        }
    }

    /// <summary>The verdict of a chain nothing in which decided — what an unresolved branch is.</summary>
    private static readonly FaultClassification Undecided = Leaf(FaultKind.Fault);

    /// <summary>True when <paramref name="exception"/>'s graph carries typed store or network evidence:
    /// a stalled query provider (a typed <see cref="FaultLayer.StoreStall"/>), a store fault the owner
    /// classified as transient (<see cref="FaultLayer.OwnerStore"/>), a socket fault, or a driver
    /// exception. The frames below then decide, by the same walk; a defect beside that evidence
    /// still wins as the worst branch.</summary>
    private static bool WrapsTypedStoreOrNetworkCause(Exception? exception)
        => ExceptionChain.Contains(exception, static e =>
            e is IClassifiedFault { ClassifiedKind: FaultKind.Transient, ClassifiedLayer: FaultLayer.StoreStall or FaultLayer.OwnerStore }
            || e is DbException
            || IsNetworkFault(e));

    /// <summary>Compares a memo key's exception by REFERENCE and the rest by value.</summary>
    private sealed class MemoKeyComparer : IEqualityComparer<(Exception Node, bool IsHead, FaultClassification? Fallback)>
    {
        public static MemoKeyComparer Instance { get; } = new();

        public bool Equals((Exception Node, bool IsHead, FaultClassification? Fallback) x,
            (Exception Node, bool IsHead, FaultClassification? Fallback) y)
            => ReferenceEquals(x.Node, y.Node) && x.IsHead == y.IsHead && Nullable.Equals(x.Fallback, y.Fallback);

        public int GetHashCode((Exception Node, bool IsHead, FaultClassification? Fallback) key)
            => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Node), key.IsHead, key.Fallback);
    }

    /// <summary>
    /// True when EVERY branch of <paramref name="exception"/>'s graph reaches a frame that
    /// <paramref name="frame"/> accepts. An aggregate needs all of its branches; a chain that ends
    /// without one, an empty aggregate, a cycle and an exhausted budget are all false. A node's
    /// answer is memoized by reference BEFORE the budget is charged again, so a graph that shares a
    /// child between many aggregates costs one visit per DISTINCT node, not one per path to it.
    /// </summary>
    private static bool EveryBranchReaches(Exception? exception, Func<Exception, bool> frame)
    {
        var budget = MaxNodes;
        var path = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var known = new Dictionary<Exception, bool>(ReferenceEqualityComparer.Instance);
        return Reaches(exception);

        bool Reaches(Exception? e)
        {
            if (e is null)
                return false;
            if (known.TryGetValue(e, out var answer))
                return answer;
            if (--budget < 0 || !path.Add(e))
                return false;
            try
            {
                answer = e is AggregateException aggregate
                    ? aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(Reaches)
                    : frame(e) || Reaches(e.InnerException);
                known[e] = answer;
                return answer;
            }
            finally
            {
                path.Remove(e);
            }
        }
    }

    // 🚨 A flagged NESTED driver frame is accepted only when it encloses no aggregate: over one, its
    // flag is not the evidence — its branches are, and EveryBranchReaches walks into them because this
    // frame then does not accept. Two flagged drivers over Aggregate(socket, JsonException) carry the
    // defect, exactly as one flagged driver over it does.
    private static bool IsTransientDriverCause(Exception inner)
        => IsTransportFrame(inner)
           || IsUnreachableSqlStateFrame(inner)
           || (inner is DbException { IsTransient: true } flagged
               && !ExceptionChain.Contains<AggregateException>(flagged.InnerException));

    // 🚨 The same rule for a connection-class SQLSTATE: it is the evidence for its frame only when that
    // frame encloses no aggregate. Over one, its branches decide — 57P03 over Aggregate(socket,
    // JsonException) carries the defect, directly or as a nested driver frame (#7117 review).
    private static bool IsUnreachableSqlStateFrame(Exception e)
        => IsUnreachableSqlState(e) && !ExceptionChain.Contains<AggregateException>(e.InnerException);

    // 🚨 A timeout or IO frame inside a driver exception is the evidence only when it has NO cause: with
    // one, its cause is — EveryBranchReaches then walks into it, so Timeout(Aggregate(socket,
    // JsonException)) carries the defect and IO(socket) reaches the socket. A cause-free one stays a
    // transient driver fault. A socket fault is network evidence by itself.
    private static bool IsTransportFrame(Exception e)
        => e is SocketException
           || (e is TimeoutException or IOException && e.InnerException is null);

    private static bool IsUnreachableSqlState(Exception e)
        => e is DbException { SqlState: { } sqlState } && StoreUnreachableSqlStates.Contains(sqlState);

    /// <summary>The verdict of ONE frame, or null when the frame does not decide.</summary>
    private static FaultClassification? Decide(Exception e, bool isHead, TeardownContext? context)
    {
        if (e is IClassifiedFault classified && classified.ClassifiedKind != FaultKind.Fault)
            return classified.ClassifiedKind == FaultKind.HubTeardown
                ? Leaf(FaultKind.Transient, FaultLayer.Transport)   // only reached for ANOTHER hub (OwnAddress set)
                : Leaf(classified.ClassifiedKind, classified.ClassifiedLayer);

        if (e is DbException db)
        {
            if (IsUnreachableSqlState(db)
                && (!ExceptionChain.Contains<AggregateException>(db.InnerException)
                    || EveryBranchReaches(db.InnerException, IsTransientDriverCause)))
                return Leaf(FaultKind.Transient, FaultLayer.Store);
            // The network-level cause INSIDE a driver exception: drivers wrap the socket fault, so
            // the DbException is the OUTER frame and decides for the whole chain below it — and so
            // does a nested driver frame carrying a connection-class SQLSTATE.
            // EVERY branch below must be such a fault: a permanent defect beside a socket fault in the
            // same graph is not an unreachable store.
            if (EveryBranchReaches(db.InnerException,
                    static inner => IsTransportFrame(inner) || IsUnreachableSqlStateFrame(inner)))
                return Leaf(FaultKind.Transient, FaultLayer.Store);
            // The provider's flag speaks for the driver frame — but not over NESTED branch evidence:
            // a flagged driver over an aggregate in which one branch is a permanent defect (a socket
            // reset beside a JsonException) carries that defect, and retrying it trades a latch for
            // a loop. With an aggregate below, every branch must be a transient frame.
            if (db.IsTransient
                && (!ExceptionChain.Contains<AggregateException>(db.InnerException)
                    || EveryBranchReaches(db.InnerException, IsTransientDriverCause)))
                return Leaf(FaultKind.Transient, FaultLayer.StoreContention);
            // Nothing typed decided: the driver's TEXT may still name an unreachable store (the same
            // banner a remote reader matches once the type is flattened away) — a verdict by message,
            // which a typed cause further down still replaces.
            return DecideByMessage(db);
        }

        if (e is OperationCanceledException cancelled)
            return ExceptionChain.Contains<TimeoutException>(cancelled.InnerException)
                ? Leaf(FaultKind.Timeout)
                : new(FaultKind.CooperativeCancel, null, isHead, false, default);

        if (e is TimeoutException)
            return Leaf(FaultKind.Timeout);
        if (IsNetworkFault(e))
            return Leaf(FaultKind.Transient, FaultLayer.Network);

        // Reached only when the teardown pre-check did not claim it, i.e. it is ANOTHER hub going away.
        // A typed teardown is typed transport evidence; the banner alone is a verdict by message.
        if (IsTypedHubTeardownFrame(e))
            return Leaf(FaultKind.Transient, FaultLayer.Transport);
        if (IsHubShutdownBanner(e))
            return Leaf(FaultKind.Transient, FaultLayer.Transport, byMessage: true);

        // Orleans.Core is not (and must not be) referenced here: match the grain reject by name.
        if (e.GetType().Name == "OrleansMessageRejectionException")
            return Leaf(FaultKind.Transient, FaultLayer.Transport);

        return DecideByMessage(e);
    }

    /// <summary>The verdict a frame's message TEXT reaches, or null; always <see cref="FaultClassification.ByMessage"/>.</summary>
    /// <param name="e">One frame.</param>
    private static FaultClassification? DecideByMessage(Exception e)
    {
        var message = e.Message ?? string.Empty;
        if (TransportTimeout.IsRoutedTransportTimeout(e)
            || message.Contains(TransportTimeout.ResponseTimeoutPhrase, StringComparison.OrdinalIgnoreCase)
            || message.Contains(TransportTimeout.PlacementTimeoutPhrase, StringComparison.OrdinalIgnoreCase)
            || message.Contains(NoResponseMarker, StringComparison.OrdinalIgnoreCase))
            return Leaf(FaultKind.Timeout, byMessage: true);
        foreach (var marker in TransportUnreachableMarkers)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return Leaf(FaultKind.Transient, FaultLayer.Transport, byMessage: true);
        foreach (var marker in StoreUnreachableMarkers)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return Leaf(FaultKind.Transient, FaultLayer.Store, byMessage: true);

        return null;
    }

    private static FaultClassification Leaf(string kind, string? layer = null, bool byMessage = false)
        => new(kind, layer, false, byMessage, default);

    private static int Severity(string kind) => kind switch
    {
        FaultKind.CooperativeCancel => 1,
        FaultKind.HubTeardown or FaultKind.ScopeTeardown => 2,
        FaultKind.Transient => 3,
        FaultKind.Timeout => 4,
        _ => 5,   // Fault, and any kind this classifier does not know
    };

    private static FaultClassification Combine(ImmutableArray<FaultClassification> branches, bool isCancellation)
    {
        var worst = branches[0];
        foreach (var branch in branches)
            if (Severity(branch.Kind) > Severity(worst.Kind))
                worst = branch;

        string? layer = null;
        var byMessage = false;
        var first = true;
        foreach (var branch in branches)
        {
            if (branch.Kind != worst.Kind)
                continue;
            byMessage |= branch.ByMessage;
            if (first)
                layer = branch.Layer;
            else if (!string.Equals(layer, branch.Layer, StringComparison.Ordinal))
                layer = null;
            first = false;
        }

        var kind = Severity(worst.Kind) == 5 ? FaultKind.Fault : worst.Kind;
        return new(kind, layer, isCancellation, byMessage, branches);
    }
}
