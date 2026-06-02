using System;
using System.Reactive.Linq;
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
    public void Write_AccessAssignment_IntoProvisionedPartition()
    {
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

        // Provision the partition first — the same way ACME's schema exists ahead of any write
        // (partitions are configured/provisioned, never created implicitly on first content
        // write — that's the "no partition, no write" guard). EnsurePartitionProvisionedAsync
        // runs the ensure_partition_schema DDL directly; it is NOT a node write, so there's no
        // identity to impersonate. Then the logged-in admin writes the grant with his OWN
        // rights — exactly the production flow once the space exists.
        ProvisionPartition(ns);

        var saved = meshService.CreateNode(node)
            .Should().Within(30.Seconds()).Emit();

        saved.Should().NotBeNull(
            "writing the _Access satellite into the provisioned partition must succeed as the user");

        var workspace = Mesh.GetWorkspace();
        var readBack = workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();

        readBack.Should().NotBeNull("read-back from the provisioned partition's _Access table must succeed");
        readBack!.Path.Should().Be(path);
    }

    /// <summary>
    /// Provision a brand-new top-level partition the platform way: run each storage provider's
    /// <see cref="IPartitionStorageProvider.EnsurePartitionProvisionedAsync"/> (the PG provider
    /// routes to the <c>ensure_partition_schema</c> DDL; non-PG providers are no-ops). This is
    /// the schema-creation step a Space performs before its root write — after it, the partition
    /// exists and ordinary user writes route into it without tripping the write guard.
    /// </summary>
    private void ProvisionPartition(string ns)
    {
        foreach (var provider in Mesh.ServiceProvider.GetServices<IPartitionStorageProvider>())
            provider.EnsurePartitionProvisionedAsync(ns).GetAwaiter().GetResult();
    }

    [Fact(Timeout = 60000)]
    public void Write_OrgPartition_RoutesByFirstSegment()
    {
        var org = $"pg9a_org_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var path = $"{org}/Project/Todo/1";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode("1", $"{org}/Project/Todo")
        {
            NodeType = "Markdown",
            Name = "Item 1",
            State = MeshNodeState.Active,
        };

        // Provision the org partition (schema) first, then the logged-in admin creates the Todo
        // with his own rights — a user with access creating content needs no System
        // impersonation; the partition just has to exist (the guard only forbids implicit
        // partition creation, not authorized content writes). See ProvisionPartition.
        ProvisionPartition(org);

        var saved = meshService.CreateNode(node).Should().Within(30.Seconds()).Emit();
        saved.Should().NotBeNull();
        saved.Path.Should().Be(path);

        var workspace = Mesh.GetWorkspace();
        var readBack = workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        readBack.Should().NotBeNull("write went to {0}.mesh_nodes; read-back must hit the same partition", org);
    }

    [Fact(Timeout = 60000)]
    public void Read_SatelliteUnion_AcrossPartitionTables()
    {
        var ns = $"pg9a_sat_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // ImpersonateAsSystem: the test creates a brand-new top-level partition
        // root (pg9a_sat_*) which the auto-logged-in admin user has no Create
        // permission on. This is an infrastructure-setup operation, the
        // documented use case for ImpersonateAsSystem.
        using var _systemScope = accessService.ImpersonateAsSystem();

        meshService.CreateNode(new MeshNode(ns)
        {
            NodeType = "User",
            Name = ns,
            State = MeshNodeState.Active,
        }).Should().Within(15.Seconds()).Emit();

        var satPath = $"{ns}/_Access/sat";
        meshService.CreateNode(new MeshNode("sat", $"{ns}/_Access")
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
        }).Should().Within(15.Seconds()).Emit();

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = resolver.ResolvePath(satPath)
            .Where(r => r is not null).Take(1).Timeout(15.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .Should().Within(30.Seconds()).Emit();

        resolution.Should().NotBeNull("satellite UNION must surface _Access rows for ResolvePath");
        resolution!.Prefix.Should().Be(satPath);
    }
}
