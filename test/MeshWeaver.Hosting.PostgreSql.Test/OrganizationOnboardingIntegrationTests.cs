using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Memex.Portal.Shared;
using Memex.Portal.Shared.Authentication;
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
/// Stage 9d — End-to-end organization + user onboarding integration. Drives
/// the same code paths the prod portal hits when:
///
/// <list type="bullet">
///   <item>a new user signs in for the first time → <see cref="UserOnboardingService"/>
///     writes Admin/Partition/{user} + {user} root + user-catalog mirror.</item>
///   <item>an admin creates a new organization → Admin/Partition/{org} +
///     {org} partition root.</item>
/// </list>
///
/// <para>Both flows depend on PgPartitionCache invalidation / lazy
/// schema-create + try-then-claim Write. The pg_notify partition_changes
/// channel covers cross-silo invalidation in Stage 9e; here we exercise
/// single-silo Monolith semantics.</para>
/// </summary>
[Collection("PostgreSql")]
public class OrganizationOnboardingIntegrationTests : MonolithMeshTestBase
{
    private const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";

    public OrganizationOnboardingIntegrationTests(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
            ?? "Host=localhost;Database=test;Username=postgres;Password=postgres";
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                services.AddPartitionedPostgreSqlPersistence(connectionString);
                services.AddSingleton<UserOnboardingService>();
                return services;
            })
            .AddRowLevelSecurity()
            .AddGraph()
            .AddOrganizationType();
    }

    private static bool ShouldSkip(out string reason)
    {
        var cs = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        if (string.IsNullOrEmpty(cs))
        {
            reason = $"set ${ConnectionStringEnvVar} to enable";
            return true;
        }
        reason = "";
        return false;
    }

    [Fact(Timeout = 120000)]
    public async Task UserOnboarding_CreatesPerUserPartition()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(60.Seconds()).Token;

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

        // After onboarding, /{username} must resolve through the routing layer.
        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath(username)
            .Where(r => r is not null).Take(1).Timeout(20.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync().ToTask(ct);
        resolution.Should().NotBeNull(
            "post-onboarding the per-user partition must be routable");
        resolution!.Node.Should().NotBeNull();
    }

    [Fact(Timeout = 120000)]
    public async Task CreateOrganization_RoutableAfterAdminPartitionWrite()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        var orgId = $"pg9d_org_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Step 1 — register the Admin/Partition/{org} catalog row first
        // (the contract: writing Admin/Partition triggers schema creation
        // and invalidates the negative cache for orgId).
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

        // Step 2 — write the organization root (the bare {org} node lives at
        // partition root). Should now succeed routing into {org}.mesh_nodes.
        var orgRoot = await meshService.CreateNode(new MeshNode(orgId)
        {
            NodeType = "Organization",
            Name = orgId,
            State = MeshNodeState.Active,
        }).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        orgRoot.Should().NotBeNull();

        // Step 3 — resolve via path resolver
        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await resolver.ResolvePath(orgId)
            .Where(r => r is not null).Take(1).Timeout(20.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .FirstAsync().ToTask(ct);
        resolution.Should().NotBeNull(
            "post-create the organization partition must be routable from any silo");
    }

    [Fact(Timeout = 120000)]
    public async Task OnboardedUser_CanWriteAndReadInOwnNamespace()
    {
        if (ShouldSkip(out var reason)) { Output.WriteLine($"SKIPPED: {reason}"); return; }
        var ct = new CancellationTokenSource(60.Seconds()).Token;

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
