using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// The routing that the listener wiring was blocked on: the <c>mesh_node_changes</c> channel is per
/// DATABASE — one LISTEN session receives every schema's events — while the change feed under
/// <see cref="PostgreSqlPathRoutingAdapter"/> is per SCHEMA. <see cref="PartitionChangeRouter"/> is
/// the answer to "whose feed is this?", and these tests pin the three outcomes it can produce.
///
/// <para>No container and no connection: a data source is built, never opened. Routing is pure
/// string work on the same resolution a WRITE uses, and that is exactly what is under test — a
/// second, drifting resolution is the failure this design forecloses.</para>
/// </summary>
public class PartitionChangeRouterTests : IDisposable
{
    // Never connected to — the router resolves paths, it does not touch the database.
    private const string UnusedConnectionString =
        "Host=localhost;Database=meshweaver_test;Username=postgres;Password=postgres";

    private readonly NpgsqlDataSource _dataSource =
        new NpgsqlDataSourceBuilder(UnusedConnectionString).Build();

    private PostgreSqlPartitionStorageProvider NewProvider()
        => new(_dataSource, UnusedConnectionString, new PostgreSqlStorageOptions());

    public void Dispose() => _dataSource.Dispose();

    [Fact]
    public void ANotificationLandsOnTheFeedOfThePartitionThatOwnsThePath()
    {
        using var provider = NewProvider();
        // Materialise the partition's adapter the way anything reading that partition would.
        var chess = provider.GetSchemaAdapter("Chess/Board");
        chess.Should().NotBeNull();

        var onChess = new List<DataChangeNotification>();
        using var sub = chess!.Changes.Subscribe(onChess.Add);
        var router = new PartitionChangeRouter(provider);

        router.OnNext(DataChangeNotification.Updated("Chess/Board", null));

        onChess.Should().HaveCount(1,
            "the per-schema feed is the one a WRITE to that path would have published on, so it is "
            + "the one a cross-process notification for it must reach");
        onChess[0].Path.Should().Be("Chess/Board");
        router.RoutedCount.Should().Be(1L);
        router.DiscardedCount.Should().Be(0L);
    }

    [Fact]
    public void APartitionThisProcessNeverTouched_ReachesTheMergedFeed_WithoutMaterialisingAnAdapter()
    {
        using var provider = NewProvider();
        var onMerged = new List<DataChangeNotification>();
        using var sub = provider.MergedChanges.Subscribe(onMerged.Add);
        var router = new PartitionChangeRouter(provider);

        router.OnNext(DataChangeNotification.Updated("Store/Plugin", null));

        onMerged.Should().HaveCount(1,
            "the cross-partition subscribers (the unscoped fan-out query, the pedestrian provider) "
            + "watch the merge, so a notification must not be lost just because no per-schema "
            + "adapter exists for it here");
        provider.MaterialisedPartitionAdapterCount.Should().Be(0,
            "routing must not CONSTRUCT a per-schema adapter: on a 140-partition mesh every process "
            + "would end up holding one per partition purely because someone, somewhere, wrote");
        router.RoutedCount.Should().Be(1L);
    }

    [Theory]
    // A NodeType name as first segment — the 2026-05-21 regression guard: routing these as
    // partitions is what once tried to CREATE SCHEMA "thread".
    [InlineData("Thread/abc")]
    [InlineData("AccessAssignment/x")]
    // URL- and query-string-shaped segments — the 2026-06-05 prod DB corruption shape.
    [InlineData("login?error=auth_failed/x")]
    [InlineData("search?q=agent&hq=scope%3adescendants/x")]
    // An unregistered global-satellite namespace: not routable until the boot-time seeding
    // registers its real schema (_Access → system_access).
    [InlineData("_Access/whoever_Access")]
    public void AnUnroutablePath_IsDiscardedWithoutTouchingAnything(string path)
    {
        using var provider = NewProvider();
        var onMerged = new List<DataChangeNotification>();
        using var sub = provider.MergedChanges.Subscribe(onMerged.Add);
        var router = new PartitionChangeRouter(provider);

        router.OnNext(DataChangeNotification.Updated(path, null));

        onMerged.Should().HaveCount(0, "an unroutable path has no feed to land on");
        provider.MaterialisedPartitionAdapterCount.Should().Be(0);
        router.DiscardedCount.Should().Be(1L);
        router.RoutedCount.Should().Be(0L);
    }

    [Fact]
    public void AGlobalSatelliteNamespace_RoutesToItsRegisteredSchema()
    {
        using var provider = NewProvider();
        // What the boot-time static-partition seeding does: _Access is NOT the lowercased
        // namespace, it is system_access.
        provider.RegisterPartition(new PartitionDefinition
        {
            Namespace = "_Access",
            DataSource = "default",
            Schema = "system_access",
            Table = "mesh_nodes",
        });
        var access = provider.GetSchemaAdapter("_Access/somebody_Access");
        access.Should().NotBeNull();

        var received = new List<DataChangeNotification>();
        using var sub = access!.Changes.Subscribe(received.Add);
        var router = new PartitionChangeRouter(provider);

        router.OnNext(DataChangeNotification.Updated("_Access/somebody_Access", null));

        received.Should().HaveCount(1,
            "the router resolves exactly as a write resolves, so the namespace whose schema is NOT "
            + "its lowercased name still lands on the right feed — the very case whose absence "
            + "froze the $security-access fold");
        router.RoutedCount.Should().Be(1L);
    }

    [Fact]
    public void AThrowingSubscriberNeverReachesTheListener()
    {
        using var provider = NewProvider();
        var chess = provider.GetSchemaAdapter("Chess/Board")!;
        using var poison = chess.Changes.Subscribe(_ => throw new InvalidOperationException("boom"));
        var router = new PartitionChangeRouter(provider);

        // 🚨 A throw escaping here would tear the LISTEN loop into its reconnect branch, so ONE bad
        // subscriber would cost EVERY path its cross-process feed until the reconnect completed.
        router.OnNext(DataChangeNotification.Updated("Chess/Board", null));

        router.RoutedCount.Should().Be(1L);
    }
}
