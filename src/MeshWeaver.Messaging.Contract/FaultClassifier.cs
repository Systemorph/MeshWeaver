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

        var builder = ImmutableArray.CreateBuilder<FaultClassification>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        CollectBranches(exception, context, seen, builder);
        if (builder.Count == 0)
            builder.Add(Leaf(FaultKind.Fault));
        var branches = builder.ToImmutable();
        var isCancellation = branches.All(b => b.IsCancellation);

        if (IsScopeTeardown(exception, context))
            return new(FaultKind.ScopeTeardown, null, isCancellation, false, branches);
        if (IsHubTeardown(exception, context))
            return new(FaultKind.HubTeardown, null, isCancellation, false, branches);

        return Combine(branches, isCancellation);
    }

    /// <summary>
    /// True when <paramref name="exception"/> is (or carries anywhere in its graph) the DI container's
    /// "this scope is gone" — an <see cref="ObjectDisposedException"/> naming a disposed Autofac
    /// <c>LifetimeScope</c>. The one copy of that text match.
    /// </summary>
    /// <param name="exception">The fault; may be null.</param>
    public static bool IsDisposedContainer(Exception? exception)
        => ExceptionChain.Contains(exception, IsDisposedContainerFrame);

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

    private static bool IsDisposedContainerFrame(Exception e) =>
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
        e is HubDisposingException or HubDisposedBeforeResponseException
        || e is DeliveryFailureException { Failure.ErrorType: ErrorType.ShuttingDown }
        || (e is IClassifiedFault classified && classified.ClassifiedKind == FaultKind.HubTeardown)
        || IsHubShutdownBanner(e);

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

    private static void CollectBranches(
        Exception head, TeardownContext? context, HashSet<Exception> seen,
        ImmutableArray<FaultClassification>.Builder branches)
    {
        var path = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var isHead = true;
        for (var current = head; current is not null; current = current.InnerException, isHead = false)
        {
            if (seen.Count >= MaxNodes)
            {
                branches.Add(Leaf(FaultKind.Fault));
                return;
            }
            if (!seen.Add(current))
            {
                // A cycle within this branch never reached a verdict: a fault. A node shared with a
                // branch already classified adds nothing — that branch carries its verdict.
                if (path.Contains(current))
                    branches.Add(Leaf(FaultKind.Fault));
                return;
            }
            path.Add(current);

            if (current is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count == 0)
                    branches.Add(Leaf(FaultKind.Fault));
                foreach (var inner in aggregate.InnerExceptions)
                    CollectBranches(inner, context, seen, branches);
                return;
            }

            if (Decide(current, isHead, context) is { } verdict)
            {
                branches.Add(verdict);
                return;
            }
        }
        branches.Add(Leaf(FaultKind.Fault));
    }

    /// <summary>The verdict of ONE frame, or null when the frame does not decide.</summary>
    private static FaultClassification? Decide(Exception e, bool isHead, TeardownContext? context)
    {
        if (e is IClassifiedFault classified && classified.ClassifiedKind != FaultKind.Fault)
            return classified.ClassifiedKind == FaultKind.HubTeardown
                ? Leaf(FaultKind.Transient, FaultLayer.Transport)   // only reached for ANOTHER hub (OwnAddress set)
                : Leaf(classified.ClassifiedKind, classified.ClassifiedLayer);

        if (e is DbException db)
        {
            if (db.SqlState is { } sqlState && StoreUnreachableSqlStates.Contains(sqlState))
                return Leaf(FaultKind.Transient, FaultLayer.Store);
            // The network-level cause INSIDE a driver exception: drivers wrap the socket fault, so
            // the DbException is the OUTER frame and decides for the whole chain below it.
            if (ExceptionChain.Contains(db.InnerException,
                    static inner => inner is TimeoutException or SocketException or IOException))
                return Leaf(FaultKind.Transient, FaultLayer.Store);
            if (db.IsTransient)
                return Leaf(FaultKind.Transient, FaultLayer.StoreContention);
            return null;
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
        if (IsHubTeardownFrame(e))
            return Leaf(FaultKind.Transient, FaultLayer.Transport);

        // Orleans.Core is not (and must not be) referenced here: match the grain reject by name.
        if (e.GetType().Name == "OrleansMessageRejectionException")
            return Leaf(FaultKind.Transient, FaultLayer.Transport);

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
