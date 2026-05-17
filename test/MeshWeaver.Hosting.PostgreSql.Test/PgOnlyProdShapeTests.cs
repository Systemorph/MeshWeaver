using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
/// <para>Skipped on CI (no local DB). Run locally with <c>MESHWEAVER_LOCAL_PG_CS</c>
/// pointing at the running Aspire <c>memex-postgres</c> container.</para>
/// </summary>
[Collection("PostgreSql")]
public class PgOnlyProdShapeTests : MonolithMeshTestBase
{
    private const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";

    public PgOnlyProdShapeTests(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
            ?? "Host=localhost;Database=test;Username=postgres;Password=postgres";
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(connectionString))
            .AddRowLevelSecurity()
            .AddGraph();
    }

    private static bool ShouldSkip(out string reason)
    {
        var cs = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        if (string.IsNullOrEmpty(cs))
        {
            reason = $"set ${ConnectionStringEnvVar} to enable (running Aspire DB only)";
            return true;
        }
        reason = "";
        return false;
    }

    [Fact(Timeout = 120000)]
    public async Task Resolve_UserPartition_BareSegment()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var username = Environment.GetEnvironmentVariable("MESHWEAVER_LOCAL_USER") ?? "rbuergi";
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath(username)
            .Where(r => r is not null)
            .Take(1)
            .Timeout(30.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync()
            .ToTask(ct);

        resolution.Should().NotBeNull(
            "/{0} is the prod symptom that motivated this refactor", username);
        resolution!.Prefix.Should().Be(username);
        resolution.Node.Should().NotBeNull();
    }

    [Fact(Timeout = 120000)]
    public async Task Write_AccessAssignment_LazyCreatesSchema()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        // Pick a fresh namespace per run to avoid colliding with prior test state.
        var ns = $"pg9a_{Guid.NewGuid():N}".ToLowerInvariant()[..16];
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
            .Where(n => n is not null)
            .Take(1)
            .Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .FirstAsync()
            .ToTask(ct);

        readBack.Should().NotBeNull("read-back after lazy schema-create must succeed");
        readBack!.Path.Should().Be(path);
    }

    [Fact(Timeout = 120000)]
    public async Task Write_OrgPartition_RoutesByFirstSegment()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        var org = $"pg9a_org_{Guid.NewGuid():N}".ToLowerInvariant()[..16];
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
            .Where(n => n is not null)
            .Take(1)
            .Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .FirstAsync()
            .ToTask(ct);
        readBack.Should().NotBeNull("write went to {0}.mesh_nodes; read-back must hit the same partition", org);
    }

    [Fact(Timeout = 120000)]
    public async Task Read_SatelliteUnion_AcrossPartitionTables()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        var ns = $"pg9a_sat_{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Primary (mesh_nodes)
        await meshService.CreateNode(new MeshNode(ns)
        {
            NodeType = "User",
            Name = ns,
            State = MeshNodeState.Active,
        }).Timeout(15.Seconds()).FirstAsync().ToTask(ct);

        // Satellite (_Access)
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
            .Where(r => r is not null)
            .Take(1)
            .Timeout(15.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync()
            .ToTask(ct);

        resolution.Should().NotBeNull("satellite UNION must surface _Access rows for ResolvePath");
        resolution!.Prefix.Should().Be(satPath);
    }
}
