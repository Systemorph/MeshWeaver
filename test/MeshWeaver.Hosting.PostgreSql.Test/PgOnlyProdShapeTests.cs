using System;
using System.Linq;
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

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
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
    public async Task Write_AccessAssignment_IntoProvisionedPartition()
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
        await ProvisionPartition(ns);

        var saved = await meshService.CreateNode(node)
            .Should().Within(30.Seconds()).Emit();

        saved.Should().NotBeNull(
            "writing the _Access satellite into the provisioned partition must succeed as the user");

        var workspace = Mesh.GetWorkspace();
        var readBack = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();

        readBack.Should().NotBeNull("read-back from the provisioned partition's _Access table must succeed");
        readBack!.Path.Should().Be(path);
    }

    /// <summary>
    /// Provision a brand-new top-level partition the platform way: run each storage provider's
    /// <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/> (the PG provider
    /// routes to the <c>ensure_partition_schema</c> DDL; non-PG providers are no-ops). This is
    /// the schema-creation step a Space/User performs before its root write — after it, the
    /// partition exists and ordinary user writes route into it without tripping the write guard.
    /// Reactive surface; the test blocks on the composed observable (tests may block — §2a).
    /// </summary>
    private Task ProvisionPartition(string ns) =>
        Mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(provider => provider.EnsurePartitionProvisioned(ns))
            .Concat()
            .ToTask();

    [Fact(Timeout = 60000)]
    public async Task Write_OrgPartition_RoutesByFirstSegment()
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
        await ProvisionPartition(org);

        var saved = await meshService.CreateNode(node).Should().Within(30.Seconds()).Emit();
        saved.Should().NotBeNull();
        saved.Path.Should().Be(path);

        var workspace = Mesh.GetWorkspace();
        var readBack = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        readBack.Should().NotBeNull("write went to {0}.mesh_nodes; read-back must hit the same partition", org);
    }

    [Fact(Timeout = 60000)]
    public async Task Read_SatelliteUnion_AcrossPartitionTables()
    {
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
        }).Should().Within(15.Seconds()).Emit();

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
        }).Should().Within(15.Seconds()).Emit();

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath(satPath)
            .Where(r => r is not null).Take(1).Timeout(15.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .Should().Within(30.Seconds()).Emit();

        resolution.Should().NotBeNull("satellite UNION must surface _Access rows for ResolvePath");
        resolution!.Prefix.Should().Be(satPath);
    }

    /// <summary>
    /// The route-resolution algorithm for an area-suffixed URL (e.g. <c>{partition}/Files</c>):
    /// the first segment maps to the partition's schema; there is NO node at the exact path
    /// <c>{partition}/Files</c> (Files is a layout AREA, not a node), so resolution must fall
    /// back to the longest existing prefix — the partition ROOT (the main node) — with the area
    /// as the <see cref="AddressResolution.Remainder"/>. Without this, <c>/{space}/Files</c> 404s
    /// with "does not match any registered address pattern" even though the space exists.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ResolvePath_AreaSuffixOnExistingPartition_FallsBackToMainNode()
    {
        var ns = $"pg9a_area_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Brand-new top-level partition root (the "main node"). Created under System because
        // the auto-logged-in admin has no Create on a fresh top-level partition.
        using var _systemScope = accessService.ImpersonateAsSystem();
        await meshService.CreateNode(new MeshNode(ns)
        {
            NodeType = "User",
            Name = ns,
            State = MeshNodeState.Active,
        }).Should().Within(15.Seconds()).Emit();

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        // No node exists at "{ns}/Files" — resolution must fall back to the partition root.
        var resolution = await resolver.ResolvePath($"{ns}/Files")
            .Where(r => r is not null).Take(1).Timeout(15.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .Should().Within(30.Seconds()).Emit();

        resolution.Should().NotBeNull(
            "'{0}/Files' must resolve to the partition root (main node), not 404 — Files is an area, not a node", ns);
        resolution!.Prefix.Should().Be(ns, "first path segment maps to the partition root / main node");
        resolution.Remainder.Should().Be("Files", "the area suffix becomes the resolution Remainder");
    }
}
