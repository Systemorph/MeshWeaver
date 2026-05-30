using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Stage 9a — PG-only persistence as the prod portal (<c>Memex.Portal.Distributed</c>)
/// wires it. No InMemory wildcard, no FileSystem fallback; every routing
/// decision goes through the PG path-routing adapter.
///
/// <para>Uses the shared testcontainer-backed <see cref="PostgreSqlFixture"/>
/// so the same suite runs in CI (Docker available) and locally without
/// requiring an Aspire DB on a fixed port.</para>
/// </summary>
[Collection("PostgreSql")]
public class PgOnlyProdShapeTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;
    private CancellationToken TestTimeout => new CancellationTokenSource(60.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 4,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph();
    }

    [Fact(Timeout = 60000)]
    public async Task Write_AccessAssignment_LazyCreatesSchema()
    {
        var ct = TestTimeout;
        var ns = $"pg9a_lazy_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var path = $"{ns}/_Access/grant1";

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var assignment = new AccessAssignment
        {
            AccessObject = "TestUser",
            DisplayName = "Test",
            Roles = [new RoleAssignment { Role = "Admin" }],
        };
        var node = new MeshNode("grant1", $"{ns}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "grant1",
            Content = assignment,
            MainNode = ns,
            State = MeshNodeState.Active,
        };

        var saved = await meshService.CreateNode(node)
            .Timeout(30.Seconds())
            .FirstAsync()
            .ToTask(ct);

        saved.Should().NotBeNull(
            "PG provider's lazy-create policy must accept the first write to an unknown namespace's _Access satellite");

        var workspace = Mesh.GetWorkspace();
        var readBack = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .FirstAsync().ToTask(ct);

        readBack.Should().NotBeNull("read-back after lazy schema-create must succeed");
        readBack!.Path.Should().Be(path);
    }

    [Fact(Timeout = 60000)]
    public async Task Write_OrgPartition_RoutesByFirstSegment()
    {
        var ct = TestTimeout;
        var org = $"pg9a_org_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var path = $"{org}/Project/Todo/1";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode("1", $"{org}/Project/Todo")
        {
            NodeType = "Markdown",
            Name = "Item 1",
            State = MeshNodeState.Active,
        };

        var saved = await meshService.CreateNode(node).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        saved.Should().NotBeNull();
        saved.Path.Should().Be(path);

        var workspace = Mesh.GetWorkspace();
        var readBack = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .FirstAsync().ToTask(ct);
        readBack.Should().NotBeNull("write went to {0}.mesh_nodes; read-back must hit the same partition", org);
    }

    [Fact(Timeout = 60000)]
    public async Task Read_SatelliteUnion_AcrossPartitionTables()
    {
        var ct = TestTimeout;
        var ns = $"pg9a_sat_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // ImpersonateAsSystem: the test creates a brand-new top-level partition
        // root (pg9a_sat_*) which the auto-logged-in admin user has no Create
        // permission on. This is an infrastructure-setup operation, the
        // documented use case for ImpersonateAsSystem.
        using var _systemScope = accessService.ImpersonateAsSystem();

        await meshService.CreateNode(new MeshNode(ns)
        {
            NodeType = "User",
            Name = ns,
            State = MeshNodeState.Active,
        }).Timeout(15.Seconds()).FirstAsync().ToTask(ct);

        var satPath = $"{ns}/_Access/sat";
        await meshService.CreateNode(new MeshNode("sat", $"{ns}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "sat",
            Content = new AccessAssignment
            {
                AccessObject = "TestUser",
                Roles = [new RoleAssignment { Role = "Viewer" }],
            },
            MainNode = ns,
            State = MeshNodeState.Active,
        }).Timeout(15.Seconds()).FirstAsync().ToTask(ct);

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath(satPath)
            .Where(r => r is not null).Take(1).Timeout(15.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync().ToTask(ct);

        resolution.Should().NotBeNull("satellite UNION must surface _Access rows for ResolvePath");
        resolution!.Prefix.Should().Be(satPath);
    }
}
