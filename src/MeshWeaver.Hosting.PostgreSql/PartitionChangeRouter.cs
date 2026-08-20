using System.Threading;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// The missing half of the cross-process change feed: routes every
/// <c>LISTEN mesh_node_changes</c> event <see cref="PostgreSqlChangeListener"/> receives to the
/// per-partition feed that owns its path.
///
/// <para><b>Why this class exists.</b> The NOTIFY channel is per DATABASE — one listener connection
/// receives every schema's events — while the in-process change feed under
/// <see cref="PostgreSqlPathRoutingAdapter"/> is per SCHEMA. Nothing answered "whose feed is this
/// notification?", so the listener's DI wiring sat commented out with a TODO for exactly this
/// routing (#1440). The consequence was measured in production: a mirror in one process could be
/// arbitrarily far behind a rival process's write and would never be told, which is why the
/// cross-hub write conflict behind the 2026-08-17 outage was DETERMINISTIC rather than a race — the
/// staleness was in the snapshot, and no retry could refresh it (#1814).</para>
///
/// <para><b>The routing is the write's own routing.</b> A path resolves to a schema exactly as
/// <see cref="PostgreSqlPathRoutingAdapter"/> resolves it for a write — first segment → schema,
/// synchronously, with the same guards (satellite node types, <c>_</c>-prefix globals resolved
/// through the registered-partition map, invalid segments refused). A notification therefore can
/// never reach a feed the write itself would not have published to, and there is no second
/// resolution to drift out of step with the first.</para>
///
/// <para><b>A notification this process has no use for is discarded cheaply</b> — one synchronous
/// segment resolution, no DB round-trip, and (deliberately) no per-schema adapter materialised for
/// a partition nobody here has touched. <see cref="DiscardedCount"/> counts them so "the listener
/// is running but nothing routes" is observable rather than something to infer from silence.</para>
///
/// <para>🚨 Delivery is SYNCHRONOUS on the Npgsql notification callback, matching the single-schema
/// wiring this replaces. That is safe because every subscriber's expensive work is already
/// off-thread: the query providers hand their re-query to an <c>IIoPool</c> leaf, and the per-node
/// hub's re-read is coalesced onto the default scheduler before it reads. Adding a queue here would
/// buy nothing and would be an unbounded buffer in front of a storm.</para>
/// </summary>
internal sealed class PartitionChangeRouter : IObserver<DataChangeNotification>
{
    private readonly PostgreSqlPartitionStorageProvider _provider;
    private readonly ILogger? _logger;
    private long _routed;
    private long _discarded;

    /// <summary>Creates the router over the provider that owns the per-schema feeds.</summary>
    public PartitionChangeRouter(
        PostgreSqlPartitionStorageProvider provider,
        ILogger<PartitionChangeRouter>? logger = null)
    {
        _provider = provider;
        _logger = logger;
    }

    /// <summary>Test seam: notifications routed onto a partition feed since startup.</summary>
    internal long RoutedCount => Interlocked.Read(ref _routed);

    /// <summary>
    /// Test seam: notifications discarded because their path is not a routable partition. A
    /// discard is normal (the database can hold rows this mesh does not route) and costs one
    /// segment resolution — but a count that equals the routed count means the routing is wrong,
    /// not that the database is quiet.
    /// </summary>
    internal long DiscardedCount => Interlocked.Read(ref _discarded);

    /// <inheritdoc />
    public void OnNext(DataChangeNotification value)
    {
        // 🚨 Never throw back into the listener: an exception raised on the Npgsql notification
        // callback would tear the LISTEN loop down into its reconnect branch, and a routing fault
        // for ONE path would then cost every path its cross-process feed until the reconnect
        // completes. Log it and keep listening — that is a surfaced fault, not a swallowed one.
        try
        {
            if (_provider.PublishExternalChange(value))
            {
                Interlocked.Increment(ref _routed);
                return;
            }

            Interlocked.Increment(ref _discarded);
            _logger?.LogDebug(
                "Cross-process change notification for '{Path}' ({Kind}) is not a routable "
                + "partition — discarded", value.Path, value.Kind);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Routing the cross-process change notification for '{Path}' ({Kind}) failed",
                value.Path, value.Kind);
        }
    }

    /// <inheritdoc />
    public void OnError(Exception error)
        => _logger?.LogError(error, "The cross-process change feed faulted");

    /// <inheritdoc />
    public void OnCompleted()
        => _logger?.LogInformation("The cross-process change feed completed");
}
