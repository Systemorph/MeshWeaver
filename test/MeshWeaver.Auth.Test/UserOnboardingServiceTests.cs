using System;
using System.Threading;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
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
    /// The service emits the partition-root MeshNode and persists exactly that one row
    /// (no User/Auth mirror write); subsequent reads find it at the bare <c>{username}</c> path.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void CreateUser_WritesPartitionRootOnly_NoUserMirror()
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

        // (b) NO User-catalog mirror is written anymore. The legacy
        // `new MeshNode(username, "User")` write routed to the unregistered `User`
        // first-segment and lazily provisioned a stray `user` schema; it is gone.
        // Login now resolves `nodeType:User` via the Auth partition (fed by the V27
        // mirror trigger from this partition-root write) — see CreateUser_LoginQueryFindsUserByEmail.
        var catalogMirror = ReadNode($"User/{username}").Should().Emit();
        catalogMirror.Should().BeNull(
            "onboarding must NOT write into the User/Auth mirror partition — the per-user " +
            "partition root is the single onboarding write; the auth mirror is maintained by the trigger");

        // No Admin/Partition catalog entry is emitted either — the path-routing
        // adapter's PendingCreate branch creates the per-user schema lazily on
        // the first write to {username}/... (see PostgreSqlPathRoutingAdapter.cs).
    }

    /// <summary>
    /// Regression for the "user already exists" onboarding dead-end: calling
    /// CreateUser when the partition-root already exists (a leftover from a
    /// half-finished onboarding or a pre-gate activity row) must NOT throw
    /// "Node already exists" — it folds into an update so onboarding completes.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void CreateUser_WhenPartitionRootExists_RepairsViaUpdate_NoThrow()
    {
        var username = $"obtest-{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var service = Mesh.ServiceProvider.GetRequiredService<UserOnboardingService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var request = new UserOnboardingRequest(
            username, $"{username}@example.com", FullName: "Repair Test");

        MeshNode repaired;
        using (accessService.ImpersonateAsSystem())
        {
            // First onboarding writes the partition root.
            service.CreateUser(request).Should().Emit();
            // Second call must repair (update) the existing root, not throw.
            repaired = service.CreateUser(request with { FullName = "Repaired Name" })
                .Should().Emit();
        }

        repaired.Should().NotBeNull();
        repaired.Id.Should().Be(username);

        var root = ReadNode(username).Should().Emit();
        root.Should().NotBeNull();
        root!.Name.Should().Be("Repaired Name",
            "the second CreateUser must have updated the existing partition root, not dead-ended");
    }

    /// <summary>
    /// Login query (the actual one used by <c>OnboardingMiddleware.FindUserByEmail</c>)
    /// must find the just-created user by email. With the User-catalog mirror write removed,
    /// this is now served by the partition-root User node directly (the query is
    /// namespace-agnostic; in prod the Auth mirror trigger also surfaces it in the Auth schema).
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

    /// <summary>
    /// ONBOARD-FIRST (the rule): tracking activity for an identity that has NOT
    /// been onboarded (no partition root / User node) must NOT create the
    /// partition. Reproduces the "partition created before onboarding" bug —
    /// before the HandleTrackActivity gate, the first-time create wrote
    /// {userId}/_UserActivity and lazily materialised the {userId} schema ahead
    /// of onboarding. The gate now probes the partition root and skips when absent.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TrackActivity_BeforeOnboarding_DoesNotCreatePartition()
    {
        var ghost = $"ghost-{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // A login activity for an un-onboarded identity (no CreateUser has run).
        using (accessService.ImpersonateAsSystem())
            Mesh.Post(new TrackActivityRequest(
                NodePath: ghost, UserId: ghost, NodeName: ghost, NodeType: "User", Namespace: "")
            { ActivityType = ActivityType.Login });

        // Give the fire-and-forget handler time to run (and skip). Sanctioned
        // bounded wait for a "nothing happened" assertion (WritingTests.md).
        Thread.Sleep(3000);

        // Query the activity catalog GLOBALLY (no `content.userId:{ghost}` filter — that
        // routes to the non-existent ghost partition and hangs). Assert nothing was
        // written under the {ghost} partition. If the gate had failed, the ghost
        // partition would exist and its activity node would show up here.
        var result = meshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("nodeType:UserActivity"))
            .Should().Within(8.Seconds()).Match(_ => true);
        result.Items.Should().NotContain(
            n => n.Path != null && n.Path.StartsWith($"{ghost}/", StringComparison.Ordinal),
            "activity tracking for an un-onboarded identity must not create the " +
            "partition/activity node — onboarding is the only thing that creates a partition");
    }

    /// <summary>
    /// After onboarding (the partition root exists), activity tracking DOES
    /// write — the gate blocks only un-onboarded identities, not real users.
    /// Also guards the negative test above: if the handler weren't reachable
    /// this would fail, so a green positive proves the negative is meaningful.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TrackActivity_AfterOnboarding_WritesActivity()
    {
        var username = $"obtest-{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var service = Mesh.ServiceProvider.GetRequiredService<UserOnboardingService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        using (accessService.ImpersonateAsSystem())
        {
            service.CreateUser(new UserOnboardingRequest(
                    username, $"{username}@example.com", FullName: "Track Test"))
                .Should().Emit();
            Mesh.Post(new TrackActivityRequest(
                NodePath: username, UserId: username, NodeName: username, NodeType: "User", Namespace: "")
            { ActivityType = ActivityType.Login });
        }

        var result = meshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"nodeType:UserActivity content.userId:{username}"))
            .Should().Within(15.Seconds()).Match(c => c.Items.Count > 0);
        result.Items.Should().NotBeEmpty(
            "an onboarded user's activity IS tracked — the gate blocks only un-onboarded identities");
    }
}
