using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Blazor.Portal;
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
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Stage 9d — End-to-end Space + user onboarding integration. Drives
/// the same code paths the prod portal hits when:
///
/// <list type="bullet">
///   <item>a new user signs in for the first time → <see cref="UserOnboardingService"/>
///     writes Admin/Partition/{user} + {user} root + user-catalog mirror.</item>
///   <item>an admin creates a new Space → schema is created lazily on first write
///     by the PG routing adapter; no Admin/Partition record is emitted anymore.</item>
/// </list>
/// </summary>
[Collection("PostgreSql")]
public class SpaceOnboardingIntegrationTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;
    private CancellationToken TestTimeout => new CancellationTokenSource(90.Seconds()).Token;

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
            {
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString);
                services.AddSingleton<UserOnboardingService>();
                return services;
            })
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType();
    }

    [Fact(Timeout = 60000)]
    public async Task UserOnboarding_CreatesPerUserPartition()
    {
        var ct = TestTimeout;
        var username = $"pg9d_user_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var onboarding = Mesh.ServiceProvider.GetRequiredService<UserOnboardingService>();

        var partitionRoot = await onboarding.CreateUser(new UserOnboardingRequest(
                Username: username,
                Email: $"{username}@example.com",
                FullName: "Test User"))
            .Timeout(45.Seconds())
            .FirstAsync()
            .ToTask(ct);

        partitionRoot.Should().NotBeNull("UserOnboardingService.CreateUser must emit the partition-root node");
        partitionRoot.Path.Should().Be(username);

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath(username)
            .Where(r => r is not null).Take(1).Timeout(20.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync().ToTask(ct);
        resolution.Should().NotBeNull(
            "post-onboarding the per-user partition must be routable");
        resolution!.Node.Should().NotBeNull();
    }

    [Fact(Timeout = 60000)]
    public async Task CreateSpace_RoutableAfterAdminPartitionWrite()
    {
        var ct = TestTimeout;
        var orgId = $"pg9d_org_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        await meshService.CreateNode(new MeshNode(orgId, "Admin/Partition")
        {
            NodeType = "Partition",
            Name = orgId,
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = orgId,
                DataSource = "default",
                Schema = orgId,
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.StandardTableMappings,
                Versioned = true,
            },
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);

        var orgRoot = await meshService.CreateNode(new MeshNode(orgId)
        {
            NodeType = "Space",
            Name = orgId,
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        orgRoot.Should().NotBeNull();

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath(orgId)
            .Where(r => r is not null).Take(1).Timeout(20.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync().ToTask(ct);
        resolution.Should().NotBeNull(
            "post-create the Space partition must be routable");
    }

    [Fact(Timeout = 60000)]
    public async Task OnboardedUser_CanWriteAndReadInOwnNamespace()
    {
        var ct = TestTimeout;
        var username = $"pg9d_owner_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var onboarding = Mesh.ServiceProvider.GetRequiredService<UserOnboardingService>();
        await onboarding.CreateUser(new UserOnboardingRequest(
                Username: username,
                Email: $"{username}@example.com",
                FullName: "Owner"))
            .Timeout(45.Seconds())
            .FirstAsync()
            .ToTask(ct);

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var notePath = $"{username}/notebook/note1";
        var saved = await meshService.CreateNode(new MeshNode("note1", $"{username}/notebook")
        {
            NodeType = "Markdown",
            Name = "note1",
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        saved.Path.Should().Be(notePath);

        var workspace = Mesh.GetWorkspace();
        var readBack = await workspace.GetMeshNodeStream(notePath)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .FirstAsync().ToTask(ct);
        readBack.Should().NotBeNull(
            "the newly onboarded user must be able to read back their own writes");
    }
}
