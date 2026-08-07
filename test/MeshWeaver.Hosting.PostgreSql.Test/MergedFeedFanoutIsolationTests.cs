using System.Reactive.Subjects;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the fan-out contract of the ROUTER-level merged change feed
/// (<see cref="PostgreSqlPathRoutingAdapter"/> — the feed behind
/// <c>PostgreSqlPartitionStorageProvider.MergedChanges</c> that every scoped
/// <see cref="PostgreSqlMeshQuery"/> and every pedestrian
/// <c>StorageAdapterMeshQueryProvider</c> pipeline watches).
///
/// <para>These are the merged-feed twins of <see cref="IsolatedChangeFeedTests"/>, and they exist
/// because the defect those tests killed at the PER-SCHEMA feed was still alive one level up
/// (issue #889 — the residual PaywallRealGateShapeTests buyer-wait flake). The router merged the
/// per-schema <c>IsolatedChangeFeed</c>s through a plain <see cref="Subject{T}"/>, which is
/// fan-out-hostile twice over:</para>
///
/// <list type="number">
///   <item><b>Mid-fan-out abort:</b> <c>Subject.OnNext</c> delivers synchronously in subscription
///     order, so ONE subscriber throwing (a torn-down query pipeline's disposed
///     <c>changeBuffer</c>) aborts delivery to every subscriber after it — a dropped update with
///     no reconcile (pg_notify LISTEN is disabled for partitioned PG).</item>
///   <item><b>Permanent schema disconnection:</b> the throw propagates INTO the publishing
///     schema's <c>IsolatedChangeFeed.OnNext</c>, whose <c>ObjectDisposedException</c> branch
///     (correctly) drops a provably-dead DIRECT observer — but here the direct observer is the
///     router's merge Subject, which is NOT dead; a subscriber OF the Subject was. The router is
///     removed from that schema's feed, and every future write to the schema — the buyer's
///     <c>AccessAssignment</c> — reaches nobody. That is the "one fold, then 45 s of silence
///     although the write committed" signature.</item>
/// </list>
///
/// <para>No database is involved: notifications are pushed straight into the per-schema adapter's
/// in-process <c>ChangeObserver</c>; building an <see cref="NpgsqlDataSource"/> opens no
/// connection.</para>
/// </summary>
public class MergedFeedFanoutIsolationTests : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlPartitionStorageProvider _provider;

    public MergedFeedFanoutIsolationTests()
    {
        // Never connected — the test drives the in-process feed only.
        const string cs = "Host=localhost;Port=1;Username=none;Password=none;Database=none";
        _dataSource = new NpgsqlDataSourceBuilder(cs).Build();
        _provider = new PostgreSqlPartitionStorageProvider(
            _dataSource, cs, new PostgreSqlStorageOptions { ConnectionString = cs });
    }

    public void Dispose()
    {
        _provider.Dispose();
        _dataSource.Dispose();
    }

    /// <summary>
    /// A query pipeline caught in its teardown window: the buffer <see cref="Subject{T}"/> it
    /// feeds is already disposed while the merged-feed subscription feeding it is still live —
    /// the exact shape a torn-down <c>StorageAdapterMeshQueryProvider</c> /
    /// <c>PostgreSqlMeshQuery</c> pipeline exposes to the feed.
    /// </summary>
    private (IDisposable FeedSubscription, Subject<DataChangeNotification> DeadBuffer) SubscribeDeadPipeline()
    {
        var deadBuffer = new Subject<DataChangeNotification>();
        var feedSub = _provider.MergedChanges.Subscribe(deadBuffer);
        deadBuffer.Dispose();
        return (feedSub, deadBuffer);
    }

    [Fact]
    public void A_disposed_sibling_subscriber_must_not_abort_delivery_to_later_subscribers()
    {
        const string partition = "GateShapeAbort";
        var adapter = _provider.GetSchemaAdapter($"{partition}/_Access/x")!;

        // Subscription ORDER is the point: the dead pipeline sits BEFORE the victim, exactly like
        // an ephemeral one-shot query relative to the long-lived cached $security-access:{scope}
        // pipeline it out-orders on a later resubscribe.
        var (deadFeedSub, _) = SubscribeDeadPipeline();
        var received = new List<string>();
        using var victim = _provider.MergedChanges.Subscribe(n => received.Add(n.Path));

        adapter.ChangeObserver.OnNext(
            DataChangeNotification.Updated($"{partition}/_Access/grant1", null));

        deadFeedSub.Dispose();

        received.Should().Contain($"{partition}/_Access/grant1",
            "a disposed sibling subscriber must not abort the merged-feed fan-out " +
            "to the subscribers after it — that is a silently dropped change notification");
    }

    [Fact]
    public void One_ObjectDisposedException_must_not_permanently_disconnect_a_schema_from_the_merged_feed()
    {
        const string partition = "GateShapePersist";
        var adapter = _provider.GetSchemaAdapter($"{partition}/_Access/x")!;

        // Publish while a dead pipeline is attached. On the defective code the resulting
        // ObjectDisposedException is misattributed to the router's merge observer, which the
        // schema's IsolatedChangeFeed then removes as "provably dead".
        var (deadFeedSub, _) = SubscribeDeadPipeline();
        adapter.ChangeObserver.OnNext(
            DataChangeNotification.Updated($"{partition}/_Access/grant1", null));
        deadFeedSub.Dispose();

        // A perfectly healthy subscriber attaching AFTERWARDS — the buyer's-grant shape from
        // issue #889: the write commits, and its notification must still reach the merged feed.
        var received = new List<string>();
        using var late = _provider.MergedChanges.Subscribe(n => received.Add(n.Path));
        adapter.ChangeObserver.OnNext(
            DataChangeNotification.Updated($"{partition}/_Access/grant2", null));

        received.Should().Contain($"{partition}/_Access/grant2",
            "one ObjectDisposedException during an earlier publish must not permanently " +
            "silence every future notification from the schema — that freezes the " +
            "$security-access Replay(1) snapshot with no reconcile path");
    }
}
