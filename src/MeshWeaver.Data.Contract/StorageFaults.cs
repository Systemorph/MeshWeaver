using System.Data.Common;
using MeshWeaver.Messaging;

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
    /// True when <paramref name="ex"/> is a TRANSIENT database connect/timeout fault: a
    /// <see cref="DbException"/> whose <see cref="DbException.SqlState"/> is in the connection
    /// class, or one wrapping a network-level <see cref="TimeoutException"/> /
    /// <see cref="System.Net.Sockets.SocketException"/> / <see cref="IOException"/> (the shape of
    /// Npgsql's "Failed to connect … ---&gt; TimeoutException: Timeout during connection attempt").
    /// A timeout WITHOUT a database exception in the chain is NOT matched — hub/request timeouts
    /// have their own policy (<c>AreaErrorClassifier.IsTransientHubFailure</c>) and must not be
    /// double-retried, nor rendered as a storage outage.
    ///
    /// <para>A forwarder onto <see cref="FaultClassifier"/> — the SQLSTATE set and the driver-wraps-
    /// network rule live there, once — asking for a TYPED <see cref="FaultLayer.Store"/> verdict on
    /// EVERY branch: an aggregate that also carries a defect is not an outage.</para>
    /// </summary>
    /// <param name="ex">The exception to classify; may be null.</param>
    public static bool IsTransientConnectFault(Exception? ex)
        => FaultClassifier.Classify(ex).EveryBranchTypedTransientIn(FaultLayer.Store);
}
