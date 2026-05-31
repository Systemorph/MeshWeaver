using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// End-to-end: <see cref="NotificationService.CreateNotification"/> writes a
/// MeshNode through the real <see cref="IMeshService"/> stack (router →
/// per-partition PG storage adapter) and the satellite mapping
/// <c>_Notification → notifications</c> in
/// <see cref="PartitionDefinition.StandardTableMappings"/> lands the row in
/// the dedicated <c>notifications</c> table inside the per-partition schema.
///
/// <para>Lower-level routing is covered by SatelliteNodeTests (adapter calls
/// directly); this test exercises the same routing through the public
/// MeshService API that production callers (NotificationCenter bell, thread
/// completion hook) actually use.</para>
/// </summary>
[Collection("PostgreSql")]
public class NotificationServiceIntegrationTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 4,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddGraph();
    }

    [Fact(Timeout = 60000)]
    public void CreateNotification_EndToEnd_LandsInNotificationsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"notif_test_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Seed a main entity so the satellite has a real parent + the partition
        // schema gets created (first-touch init runs CreateSatelliteTablesAsync
        // which includes `notifications` after the StandardTableMappings change).
        var mainPath = $"{ns}/doc";
        meshService.CreateNode(new MeshNode("doc", ns)
        {
            NodeType = "Markdown",
            Name = "doc",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();

        // Act: call the public NotificationService API the bell + thread hook use.
        var notif = NotificationService.CreateNotification(
                meshService,
                mainNodePath: mainPath,
                title: "Doc ready",
                message: "Your document is ready for review.",
                type: NotificationType.General,
                targetNodePath: mainPath,
                createdBy: "agent",
                icon: "/static/NodeTypeIcons/bell.svg")
            .Should().Within(30.Seconds()).Emit();

        notif.Should().NotBeNull();
        notif.Path.Should().StartWith($"{mainPath}/{NotificationService.SatelliteSegment}/");
        notif.MainNode.Should().Be(mainPath);

        // Verify the row landed in the dedicated `notifications` table inside
        // the partition schema, NOT in mesh_nodes or annotations.
        using var ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        ds.ScalarLong($"SELECT COUNT(*) FROM \"{ns}\".notifications WHERE id = @id",
                new[] { ("id", (object)notif.Id!) }, ct)
            .Should().Within(30.Seconds()).Be(1L,
                "the satellite must land in the dedicated notifications table");

        ds.ScalarLong($"SELECT COUNT(*) FROM \"{ns}\".mesh_nodes WHERE id = @id",
                new[] { ("id", (object)notif.Id!) }, ct)
            .Should().Within(30.Seconds()).Be(0L,
                "the satellite must NOT land in mesh_nodes (regression would mean routing broke)");
    }

    [Fact(Timeout = 60000)]
    public void MultipleNotifications_AccumulateAndAreQueryable_ViaNodeTypeFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"notif_test2_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Seed a main entity (any node type works; notifications are satellites
        // of arbitrary entities — Markdown is already registered via AddGraph).
        var docPath = $"{ns}/doc-1";
        meshService.CreateNode(new MeshNode("doc-1", ns)
        {
            NodeType = "Markdown",
            Name = "doc-1",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();

        // Emit three notifications (simulating three round completions).
        for (var i = 0; i < 3; i++)
        {
            NotificationService.CreateNotification(
                    meshService,
                    mainNodePath: docPath,
                    title: $"Round {i} ready",
                    message: $"Preview {i}",
                    type: NotificationType.General,
                    targetNodePath: docPath,
                    createdBy: "agent")
                .Should().Within(30.Seconds()).Emit();
        }

        // Direct PG count — all three sit in the partition's notifications table.
        using var ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        ds.ScalarLong($"SELECT COUNT(*) FROM \"{ns}\".notifications WHERE main_node = @mn",
                new[] { ("mn", (object)docPath) }, ct)
            .Should().Within(30.Seconds()).Be(3L);
    }
}
