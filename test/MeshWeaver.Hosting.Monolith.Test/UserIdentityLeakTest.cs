using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests verifying that user identity does not leak between requests.
/// Reproduces the bug where "Welcome back, Roland Buergi" was shown to other users
/// because LayoutAreaHost captured the first user's AccessContext and reused it.
/// </summary>
public class UserIdentityLeakTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddSampleUsers()
            .AddMeshNodes(MeshNode.FromPath("User/Alice") with
            {
                Name = "Alice",
                NodeType = "User",
                State = MeshNodeState.Active,
            })
            .AddMeshNodes(MeshNode.FromPath("User/Bob") with
            {
                Name = "Bob",
                NodeType = "User",
                State = MeshNodeState.Active,
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>
    /// Waits for a stream emission whose JSON contains the expected substring.
    /// Layout area streams emit an initial state (menu + progress), then the
    /// actual rendered content in a subsequent update.
    /// </summary>
    private static async Task<string> WaitForContent(
        ISynchronizationStream<JsonElement> stream, string expectedSubstring, TimeSpan timeout)
    {
        var changeItem = await ((IObservable<ChangeItem<JsonElement>>)stream)
            .Where(ci => ci.Value.ToString().Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase))
            .Timeout(timeout)
            .FirstAsync();
        return changeItem.Value.ToString();
    }

    /// <summary>
    /// Verifies that AccessService.Context returns only the AsyncLocal value (delivery context)
    /// and does NOT fall back to CircuitContext.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void AccessService_Context_ReturnsOnlyAsyncLocal()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Set circuit context to UserA
        var userA = new AccessContext { ObjectId = "UserA", Name = "User A" };
        accessService.SetCircuitContext(userA);

        // Context should NOT fall back to circuit context (AsyncLocal is null)
        accessService.Context.Should().BeNull(
            "Context should return only the AsyncLocal value, not fall back to CircuitContext");

        // CircuitContext should still be accessible explicitly
        accessService.CircuitContext.Should().Be(userA,
            "CircuitContext should return the persistent circuit-level context");

        // Set AsyncLocal to UserB
        var userB = new AccessContext { ObjectId = "UserB", Name = "User B" };
        accessService.SetContext(userB);

        // Context should return UserB (AsyncLocal takes priority)
        accessService.Context.Should().Be(userB,
            "Context should return the AsyncLocal value when set");

        // Clear AsyncLocal
        accessService.SetContext(null);

        // Context should be null again, CircuitContext unchanged
        accessService.Context.Should().BeNull(
            "Context should be null after clearing AsyncLocal");
        accessService.CircuitContext.Should().Be(userA,
            "CircuitContext should remain unchanged after clearing AsyncLocal");

        // Cleanup
        accessService.SetCircuitContext(null);
    }

    /// <summary>
    /// Verifies that the Activity area shows the node owner's name (from MeshNode data),
    /// not the viewer's name from AccessContext.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ActivityArea_ShowsNodeOwnerName_NotViewerName()
    {
        // Login as Bob (different from Alice, the node owner)
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "Bob", Name = "Bob" });

        var client = GetClient();
        var aliceAddress = new Address("User/Alice");

        // Ensure the hub is ready
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(UserActivityLayoutAreas.ActivityArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            aliceAddress, reference);

        // Wait for the rendered content (contains "Alice" as the node owner name)
        var json = await WaitForContent(stream, "Alice", TimeSpan.FromSeconds(15));

        // The welcome banner should show "Alice" (the node owner), not "Bob" (the viewer)
        json.Should().NotContain("Welcome back, Bob",
            "The personal welcome banner should not appear when a visitor views someone else's page");
    }

    /// <summary>
    /// Verifies that the node owner sees their personal dashboard
    /// with the welcome banner and chat section.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ActivityArea_OwnerSeesPersonalDashboard()
    {
        // Login as Alice (the node owner)
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "Alice", Name = "Alice" });

        var client = GetClient();
        var aliceAddress = new Address("User/Alice");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(UserActivityLayoutAreas.ActivityArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            aliceAddress, reference);

        // Wait for the rendered dashboard content
        var json = await WaitForContent(stream, "Welcome back", TimeSpan.FromSeconds(15));

        // Owner should see the personal dashboard
        json.Should().Contain("Welcome back, Alice",
            "The owner should see a welcome banner with their name");
    }

    /// <summary>
    /// Verifies that a visitor does NOT see the personal dashboard
    /// (no "Welcome back" banner, no chat).
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ActivityArea_VisitorSeesPublicProfile()
    {
        // Login as Bob (visiting Alice's page)
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "Bob", Name = "Bob" });

        var client = GetClient();
        var aliceAddress = new Address("User/Alice");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(UserActivityLayoutAreas.ActivityArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            aliceAddress, reference);

        // Wait for the rendered profile content (visitor sees Alice's name in the profile card)
        var json = await WaitForContent(stream, "Alice", TimeSpan.FromSeconds(15));

        // Visitor should NOT see the personal welcome banner
        json.Should().NotContain("Welcome back",
            "Visitors should not see the 'Welcome back' banner on someone else's page");
    }
}
