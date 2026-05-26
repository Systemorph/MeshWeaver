using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Repros the running-portal symptom: hitting <c>/{username}</c> shows
/// "No node found at '{username}'" (or, post-Admin/Partition-fix, renders
/// nothing) even though <c>{username}.mesh_nodes</c> has the bare User row.
///
/// <para>The end-to-end contract under test:</para>
/// <list type="number">
///   <item><b>Routing</b> — <see cref="IPathResolver.ResolvePath"/> on
///         <c>"rbuergi"</c> must return <c>AddressResolution(Prefix="rbuergi",
///         Remainder=null, Node!=null)</c>. Pre-fix: returns null because the
///         partition isn't routable (Admin/Partition entry missing).</item>
///   <item><b>Workspace</b> — <c>workspace.GetMeshNodeStream("rbuergi")</c>
///         must emit the persisted MeshNode. Pre-fix: stream never emits
///         because the route NotFounds.</item>
/// </list>
///
/// <para>This is the deterministic reproducer the user asked for: it fails
/// the same way the portal does, without needing a browser or Aspire.</para>
/// </summary>
[Collection("PostgreSql")]
public class UserPartitionResolutionTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(60.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            MaxPoolSize = 4,
            ConnectionIdleLifetime = 10,
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddGraph();
    }

    /// <summary>
    /// User partition with a bare User row at <c>(namespace='', id=username)</c>
    /// must resolve via <see cref="IPathResolver.ResolvePath"/>.
    ///
    /// <para>The route shape post-V20: the per-user partition (e.g.
    /// <c>rbuergi.mesh_nodes</c>) holds the FULL User row at path=username
    /// (namespace='', id=username), and Admin/Partition/{username} registers
    /// the partition so the routing layer's first-segment lookup matches.
    /// Hitting <c>/rbuergi</c> in the portal calls
    /// <c>NavigationService → IPathResolver.ResolvePath("rbuergi")</c>;
    /// the result drives the page layout. This test pins the resolver
    /// contract end-to-end — Postgres-backed.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ResolvePath_UserPartitionRoot_ReturnsUserNode()
    {
        const string username = "rbuergi_test_resolve";

        var pgProvider = Mesh.ServiceProvider.GetRequiredService<PostgreSqlPartitionStorageProvider>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var pathResolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Infrastructure setup: writing a brand-new partition root the admin
        // doesn't own — same pattern as Read_SatelliteUnion_AcrossPartitionTables
        // and the onboarding-service. ImpersonateAsSystem grants Permission.All
        // for the scope so the Create writes don't hit RlsNodeValidator.
        using var _systemScope = accessService.ImpersonateAsSystem();

        // 1) Register the per-user partition (Admin/Partition/{username}).
        //    Without this, PostgreSqlPartitionStorageProvider.Matches(username)
        //    returns false, the route NotFounds, and the page is blank.
        var partitionDef = new PartitionDefinition
        {
            Namespace = username,
            DataSource = "default",
            Schema = username.ToLowerInvariant(),
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Versioned = true,
        };
        await pgProvider.EnsureSchemaForPartitionAsync(partitionDef, TestTimeout);
        pgProvider.RegisterPartition(partitionDef);

        var partitionNode = new MeshNode(username, "Admin/Partition")
        {
            NodeType = "Partition",
            Name = username,
            State = MeshNodeState.Active,
            Content = partitionDef,
        };
        await meshService.CreateNode(partitionNode)
            .FirstAsync()
            .ToTask(TestTimeout);

        // 2) Write the bare User row directly into the per-user schema at
        //    namespace='' / id=username — the post-V20 layout the onboarding
        //    refactor + V20 migration produce.
        var userNode = MeshNode.FromPath(username) with
        {
            Name = "Test User",
            NodeType = "User",
            State = MeshNodeState.Active,
        };
        await meshService.CreateNode(userNode)
            .FirstAsync()
            .ToTask(TestTimeout);

        // 3) Resolve.
        var resolution = await pathResolver.ResolvePath(username)
            .FirstAsync()
            .ToTask(TestTimeout);

        // 4) Assert.
        resolution.Should().NotBeNull(
            "PathResolutionService must find the User row at ns='' id='{0}' " +
            "in the {0}.mesh_nodes partition — that's what the portal's " +
            "NavigationService consumes for the user's home page.", username);
        resolution!.Prefix.Should().Be(username,
            "the resolved prefix is the partition root, not an ancestor");
        resolution.Remainder.Should().BeNull(
            "exact-path match has no remainder");
        resolution.Node.Should().NotBeNull(
            "the matched MeshNode must travel back on AddressResolution.Node — " +
            "RoutingServiceBase consumes it to instantiate the per-node hub " +
            "without a second `path:X` round-trip");
        resolution.Node!.NodeType.Should().Be("User",
            "the bare partition-root row is a User identity, not a Partition catalog entry");
    }

    /// <summary>
    /// Once the path resolves, the routing layer must actually instantiate
    /// the per-node hub so <c>workspace.GetMeshNodeStream(username)</c>
    /// emits the persisted node. This is the second hop the portal needs —
    /// path resolution alone isn't enough if the hub never activates.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GetMeshNodeStream_UserPartitionRoot_EmitsUserNode()
    {
        const string username = "rbuergi_test_stream";

        var pgProvider = Mesh.ServiceProvider.GetRequiredService<PostgreSqlPartitionStorageProvider>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Same shape as ResolvePath_UserPartitionRoot_ReturnsUserNode — creating
        // a brand-new top-level partition root the admin doesn't own.
        // RlsNodeValidator rejects without ImpersonateAsSystem.
        using var _systemScope = accessService.ImpersonateAsSystem();

        var partitionDef = new PartitionDefinition
        {
            Namespace = username,
            DataSource = "default",
            Schema = username.ToLowerInvariant(),
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Versioned = true,
        };
        await pgProvider.EnsureSchemaForPartitionAsync(partitionDef, TestTimeout);
        pgProvider.RegisterPartition(partitionDef);

        await meshService.CreateNode(new MeshNode(username, "Admin/Partition")
        {
            NodeType = "Partition",
            Name = username,
            State = MeshNodeState.Active,
            Content = partitionDef,
        }).FirstAsync().ToTask(TestTimeout);

        await meshService.CreateNode(MeshNode.FromPath(username) with
        {
            Name = "Test User",
            NodeType = "User",
            State = MeshNodeState.Active,
        }).FirstAsync().ToTask(TestTimeout);

        var node = await workspace
            .GetMeshNodeStream(username)
            .Where(n => n != null)
            .Take(1)
            .Timeout(30.Seconds())
            .FirstAsync()
            .ToTask(TestTimeout);

        node.Should().NotBeNull(
            "workspace.GetMeshNodeStream must emit the User row — the portal's " +
            "layout area subscribes through this same primitive");
        node.Path.Should().Be(username);
        node.NodeType.Should().Be("User");
    }
}
