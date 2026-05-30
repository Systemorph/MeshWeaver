using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Pins the onboarding dual-write contract — every <see cref="UserOnboardingService.CreateUser"/>
/// call lands TWO rows: partition-root and user-catalog mirror. The dedicated
/// <c>Admin/Partition/{user}</c> entry was dropped (2026-05-26): the path-routing
/// adapter's <c>PendingCreate</c> branch creates the per-user schema lazily on
/// first write, so no explicit Partition MeshNode is needed.
///
/// <para>Real mesh, in-memory partitioned persistence, no mocks — this is the
/// shape prod hits when a new user signs up. The chain stays 100% reactive
/// inside the service; the test asserts directly on the observables via
/// <c>.Should().Emit()</c> / <c>.Match(...)</c> — no async bridge.</para>
/// </summary>
public class UserOnboardingServiceTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureServices(services =>
            {
                services.AddScoped<UserOnboardingService>();
                return services;
            });

    /// <summary>
    /// Pre-warm User + Partition type hubs so the post-creation pipeline doesn't
    /// have to cold-start them during the test.
    /// </summary>
    protected override void PreWarmNodeTypeHubs()
    {
        base.PreWarmNodeTypeHubs();
        foreach (var typeName in new[] { "User", "Partition" })
        {
            var typeNode = Mesh.ServiceProvider.FindStaticNode(typeName);
            if (typeNode?.HubConfiguration is { } cfg)
            {
                _ = Mesh.GetHostedHub(new Address(typeName), cfg);
            }
        }
    }

    private void ImpersonateAsUser(string userId)
        => Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });

    /// <summary>
    /// The service emits the partition-root MeshNode AND persists both
    /// rows (a/b) such that subsequent reads find them at the expected paths.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void CreateUser_WritesBothRows()
    {
        var username = $"obtest-{Guid.NewGuid():N}".ToLowerInvariant()[..16];

        // Prod onboarding wraps the create chain in
        // `AccessService.ImpersonateAsHub(PortalApplication.Hub)` — the portal
        // hub identity has the platform-admin scope needed to write into
        // Admin/Partition. The test runs the equivalent: impersonate as the
        // mesh hub which is the same identity surface in test config.
        var service = Mesh.ServiceProvider.GetRequiredService<UserOnboardingService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var request = new UserOnboardingRequest(
            Username: username,
            Email: $"{username}@example.com",
            FullName: "Obtest User",
            Bio: "test bio");

        MeshNode emitted;
        using (accessService.ImpersonateAsSystem())
        {
            emitted = service.CreateUser(request).Should().Emit();
        }

        // The observable emits the partition-root node — that's the canonical
        // identity for downstream consumers (path = "{username}").
        emitted.Should().NotBeNull();
        emitted.Id.Should().Be(username);
        emitted.Namespace.Should().BeNullOrEmpty(
            "partition-root entry MUST have empty namespace (path = '{username}')");
        emitted.Path.Should().Be(username);
        emitted.NodeType.Should().Be("User");

        // (a) Per-user partition root — path = "{username}", ns = ""
        var partitionRoot = ReadNode(username).Should().Emit();
        partitionRoot.Should().NotBeNull("partition-root entry must be readable at the bare path");
        partitionRoot!.NodeType.Should().Be("User");
        partitionRoot.Namespace.Should().BeNullOrEmpty();

        // (b) User-catalog mirror — path = "User/{username}", ns = "User"
        var catalogMirror = ReadNode($"User/{username}").Should().Emit();
        catalogMirror.Should().NotBeNull(
            "user-catalog mirror at user.mesh_nodes (ns=User) is what the login " +
            "query `nodeType:User content.email:X` scans — without it, every signed-in user " +
            "bounces back to /onboarding");
        catalogMirror!.NodeType.Should().Be("User");
        catalogMirror.Namespace.Should().Be("User");

        // No Admin/Partition catalog entry is emitted anymore — the path-routing
        // adapter's PendingCreate branch creates the per-user schema lazily on
        // the first write to {username}/... (see PostgreSqlPathRoutingAdapter.cs).
    }

    /// <summary>
    /// Login query (the actual one used by <c>OnboardingMiddleware.FindUserByEmail</c>)
    /// must find the just-created user by email. This is the contract that the
    /// catalog-mirror row (b) exists for.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void CreateUser_LoginQueryFindsUserByEmail()
    {
        var username = $"obtest-{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var email = $"{username}@example.com";

        var service = Mesh.ServiceProvider.GetRequiredService<UserOnboardingService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.ImpersonateAsSystem())
        {
            service.CreateUser(new UserOnboardingRequest(username, email, FullName: "Login Test"))
                .Should().Emit();
        }

        // Same shape as OnboardingMiddleware.FindUserByEmail — search for the user
        // by email. We use IMeshService (public surface; the middleware uses the
        // internal IMeshQueryCore but the query string + result shape are identical).
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var found = meshService
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery(
                    $"nodeType:User content.email:{email} limit:1"))
            .Should().Within(10.Seconds())
            .Match(c => c.Items.Count > 0);

        found.Items.Should().ContainSingle(
            "the login query MUST find the user by email — that's why we keep the " +
            "user-catalog mirror at user.mesh_nodes (namespace=User)");
        found.Items[0].Id.Should().Be(username);
    }
}
