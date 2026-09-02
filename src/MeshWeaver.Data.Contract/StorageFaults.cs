using System.Collections.Frozen;
using System.Data.Common;

namespace MeshWeaver.Data;

/// <summary>
/// The ONE definition of "the data store could not be REACHED" — a transient connect/timeout fault
/// from whatever ADO.NET driver a deployment's storage backend happens to use.
///
/// <para><b>Why it lives here and not next to a consumer.</b> Two layers need the same answer and
/// they sit on opposite sides of the assembly graph: <c>MeshWeaver.Hosting</c>'s query fan-in
/// (<c>TransientStorageFaults.RetryTransientConnect</c>) asks it to decide whether a bounded retry
/// is worth attempting, and <c>MeshWeaver.Layout</c>'s render-failure path
/// (<c>AreaErrorClassifier.IsStorageUnavailable</c>) asks it to decide what an area SHOWS once that
/// retry is spent. Both referenced <c>MeshWeaver.Data.Contract</c> already. A second copy of the
/// rule is exactly the defect the codebase has been bitten by before — two implementations of one
/// classification drift, and here the drift is silent: a fault the retry treats as transient but
/// the renderer treats as a defect (or vice versa) produces an area that either lies about a real
/// bug or presents an infrastructure blip as a permanent failure.</para>
///
/// <para><b>Why the predicate is typed on <see cref="DbException"/>.</b> The storage adapters live
/// in provider packages (the plugins repo), so no assembly here can name <c>NpgsqlException</c>.
/// It does not need to: every ADO.NET driver derives its faults from the BCL
/// <see cref="DbException"/>, which carries <see cref="DbException.SqlState"/> — enough to match
/// exactly the transient connect/timeout class. Client-side connect timeouts arrive as a
/// <see cref="DbException"/> wrapping a <see cref="TimeoutException"/> /
/// <see cref="System.Net.Sockets.SocketException"/> / <see cref="IOException"/>; server-side
/// refusals carry a connection-class SQLSTATE. A real query/schema error (<c>42P01</c>,
/// <c>23505</c>, a syntax error) is NOT matched and propagates unchanged, as does every
/// non-database fault — treating those as "the store is unreachable" would mask a defect twice
/// over: once by retrying it, once by telling the viewer to come back later.</para>
///
/// <para>Issues: #2521 (the retry), #2876 (the render frame the retry's exhausted budget needs).</para>
/// </summary>
public static class StorageFaults
{
    /// <summary>
    /// SQLSTATE classes meaning "the database could not be REACHED or is momentarily refusing
    /// connections" — the transient connect class, deliberately WITHOUT the in-query races
    /// (<c>40001</c>/<c>40P01</c>): those belong to the layer that owns the statement (the
    /// adapters' own retry), not to the query fan-in and not to a render frame.
    ///
    /// <para>🚨 <c>FrozenSet</c>, not <c>HashSet</c>, and not an allowlist entry on
    /// <c>NoStaticCollectionsTest</c>. The rule bans a static COLLECTION because it is
    /// process-wide state that survives mesh disposal; this is a constant, so the fix is to say
    /// so in the TYPE rather than to argue for it in a list. A mutable type here would also be a
    /// standing invitation for someone to write to it later, which is the failure the ban exists
    /// to prevent — and it read as an exception to a rule that has none.</para>
    /// </summary>
    private static readonly FrozenSet<string> TransientConnectSqlStates = new[]
    {
        "08000", "08001", "08003", "08004", "08006", // connection_exception family
        "57P01", "57P02", "57P03",                   // admin/crash shutdown, cannot_connect_now
        "53300", "53400",                            // too_many_connections, configuration_limit_exceeded
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="ex"/> is a TRANSIENT database connect/timeout fault: a
    /// <see cref="DbException"/> whose <see cref="DbException.SqlState"/> is in the connection
    /// class, or one wrapping a network-level <see cref="TimeoutException"/> /
    /// <see cref="System.Net.Sockets.SocketException"/> / <see cref="IOException"/> (the shape of
    /// Npgsql's "Failed to connect … ---&gt; TimeoutException: Timeout during connection attempt").
    /// A timeout WITHOUT a database exception in the chain is NOT matched — hub/request timeouts
    /// have their own policy (<c>AreaErrorClassifier.IsTransientHubFailure</c>) and must not be
    /// double-retried, nor rendered as a storage outage.
    /// </summary>
    /// <param name="ex">The exception to classify; may be null.</param>
    public static bool IsTransientConnectFault(Exception? ex)
    {
        var seenDbException = false;
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case DbException db:
                    var sqlState = db.SqlState;
                    if (sqlState is not null && TransientConnectSqlStates.Contains(sqlState))
                        return true;
                    seenDbException = true;
                    break;
                // The network-level cause INSIDE a driver exception (drivers wrap the socket
                // fault; the DbException is always the OUTER frame, so walking outer→inner
                // seeing the DbException first is the invariant this flag encodes).
                case TimeoutException or System.Net.Sockets.SocketException or IOException
                    when seenDbException:
                    return true;
            }
        }
        return false;
    }
}
