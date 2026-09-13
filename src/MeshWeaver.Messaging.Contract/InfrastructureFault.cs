using System.Data.Common;
using System.Net.Sockets;

namespace MeshWeaver.Messaging;

/// <summary>
/// Classifies a fault of the INFRASTRUCTURE a hub depends on — the database could not be reached,
/// a host name did not resolve, a connection attempt timed out — as distinct from a fault of the
/// hub's own configuration or code.
///
/// <para>🚨 <b>Why the distinction is load-bearing (Systemorph/MeshWeaver#4067, #4068).</b> A hub
/// whose initialization faults enters a FAILED state that answers every later request with a
/// terminal <c>DeliveryFailure</c> and is never re-run — the right outcome for a NodeType that
/// does not compile or a handler that throws, which no retry would change. Applied to a
/// transient infrastructure fault it is wrong in kind: nothing about the activation is broken, the
/// dependency was merely away for a moment, and latching the activation turned a two-minute DNS
/// blip into "this address is broken until the process restarts". Measured on memex-cloud,
/// 2026-09-12: three <c>DataContext</c> initializations failed on
/// <c>SocketException: Name or service not known</c> inside two minutes and stayed FAILED; on
/// memex, 19 <c>sync/*</c> BuildupActions failed on <c>NpgsqlException: Failed to connect</c>
/// across three weeks, each leaving its hub refusing forever.</para>
///
/// <para><b>What counts as transient.</b> A <see cref="DbException"/> whose own
/// <see cref="DbException.IsTransient"/> says so — every ADO.NET provider classifies its
/// connection failures, timeouts and serialization conflicts there, so this needs no reference to
/// a provider (Npgsql sets it for exactly the two shapes measured: a connection timeout and a
/// name-resolution failure) — or a bare <see cref="SocketException"/> anywhere along the
/// inner-exception chain, so a reflective wrapper or an "initialization failed" wrapper does not
/// hide the cause. The walk is bounded and reference-deduplicated like <see cref="ExceptionChain"/>'s.</para>
///
/// <para><b>What deliberately does not count.</b> A <see cref="TimeoutException"/> on its own: the
/// initialization time-box mints one for a hang, and a hang is not a transient dependency fault.
/// An <c>HttpRequestException</c>: it carries a status for most failures and the callers that
/// meet it have their own retry contracts. A provider that leaves <c>IsTransient</c> false has
/// made its own classification, which is honoured.</para>
///
/// <para>🚨 <b>An <see cref="AggregateException"/> is transient only when EVERY branch is.</b> A
/// <c>DataContext</c> initialises its data sources under <c>Task.WhenAll</c>, so one aggregate can
/// carry a connection timeout from one source AND a genuine defect from another. Reading "any
/// branch transient" as transient would retire the activation, discard the defect, and re-run the
/// same failing initialization on every reactivation — a latch traded for a loop. So the aggregate
/// is transient iff all of its (flattened) branches are, and a mixed aggregate keeps the FAILED
/// latch, whose recorded error still carries the transient branch for the reader.</para>
/// </summary>
public static class InfrastructureFault
{
    /// <summary>
    /// True when <paramref name="exception"/> is — or carries, along its inner-exception chain — a
    /// transient infrastructure fault: a <see cref="DbException"/> with
    /// <see cref="DbException.IsTransient"/> set, or a <see cref="SocketException"/>. An
    /// <see cref="AggregateException"/> qualifies only when every one of its branches does.
    /// </summary>
    /// <param name="exception">The fault to classify; may be null.</param>
    /// <returns><c>true</c> for a fault a fresh attempt could reasonably not meet again.</returns>
    public static bool IsTransient(Exception? exception)
    {
        // Bounded like ExceptionChain: a caller-supplied graph can be cyclic or shared.
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        return IsTransient(exception, seen);
    }

    private const int MaxNodes = 256;

    private static bool IsTransient(Exception? exception, HashSet<Exception> seen)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!seen.Add(current) || seen.Count > MaxNodes)
                return false;
            if (current is AggregateException aggregate)
            {
                // Every branch, not any: see the type remarks. Flatten first so a nested aggregate
                // (WhenAll inside WhenAll) contributes its leaves, not itself.
                var branches = aggregate.Flatten().InnerExceptions;
                return branches.Count > 0 && branches.All(branch => IsTransient(branch, seen));
            }
            if (current is DbException { IsTransient: true } || current is SocketException)
                return true;
        }
        return false;
    }
}
