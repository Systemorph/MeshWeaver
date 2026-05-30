using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans equivalent of the Monolith <c>SubscribeRequestNotFoundSurfaceTest</c>.
///
/// <para><b>What we pin:</b></para>
/// <list type="number">
///   <item><b>Not-found path</b> â€” subscribing to a layout area on a non-existent
///   address must surface <c>OnError</c> within a few seconds with a
///   "No node found" message, NOT spin forever. Catches the regression where
///   <c>RoutingGrain</c> failed to route <c>DeliveryFailure</c> back to the
///   portal/client hub before the portal-type early-exit check was added.</item>
///   <item><b>Success path</b> â€” subscribing to a layout area on an existing address
///   (the seeded "TestUser" node) must produce at least one data emission within a
///   few seconds. Confirms the full "RoutingGrain â†’ MessageHubGrain â†’ layout area"
///   path works before testing the failure case.</item>
/// </list>
///
/// <para><b>The Monolith test passes but Orleans still showed a spinner.</b>
/// The routing paths differ:</para>
/// <list type="bullet">
///   <item>Monolith: <c>RoutingServiceBase.PostNotFound</c> posts directly back
///   to the caller's hub â€” no cross-process hop.</item>
///   <item>Orleans: <c>RoutingGrain.RouteMessage</c> returns <see cref="MessageDeliveryState.Failed"/>;
///   <c>OrleansRoutingService.DeliverViaGrainAsync</c> reads the failure and calls
///   <c>SendDeliveryFailure</c>; the failure must then route from the mesh hub back
///   through the routing-service <c>streams</c> dict to the client/portal hub, and
///   from there to the sync sub-hub's built-in <see cref="DeliveryFailure"/> handler
///   which calls <c>Store.OnError</c>.</item>
/// </list>
/// </summary>
public class OrleansSubscribeRequestNotFoundSurfaceTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    // -------------------------------------------------------------------------
    // FAILURE path: non-existent address â†’ OnError within a few seconds
    // -------------------------------------------------------------------------

    /// <summary>
    /// Subscribing to a layout area on a non-existent address must surface
    /// <c>OnError</c> within a few seconds with a "No node found" message â€”
    /// NOT spin forever waiting on the framework's 30 s RequestTimeout. The
    /// test fails closed (20 s timeout on the OnError wait) so a regression
    /// into the swallowed-timeout shape is loud.
    /// </summary>
    [Fact]
    public async Task GetRemoteStream_on_nonexistent_address_surfaces_OnError_with_NotFound_message()
    {
        var client = await GetClientAsync("notfound-" + Guid.NewGuid().ToString("N")[..8]);

        // Address with NO node at all and no ancestor â€” catalog resolves to null.
        var address = new Address("doesnotexist", "missing-instance-" + Guid.NewGuid().ToString("N")[..8]);
        var reference = new LayoutAreaReference("Overview");

        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);
        stream.Should().NotBeNull(
            "GetRemoteStream must always return a stream â€” even when the target is unreachable, " +
            "the OnError path is the surface signal.");

        Exception? captured = null;
        var done = new ManualResetEventSlim(false);

        using var sub = stream.Subscribe(
            _ => { /* no value should arrive */ },
            ex => { captured = ex; done.Set(); },
            () => done.Set());

        var fired = done.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        fired.Should().BeTrue(
            "the stream must surface OnError (not spin) when the target address routes to NotFound. " +
            "If this fails, OrleansRoutingService.SendDeliveryFailure / RoutingGrain is dropping " +
            "the DeliveryFailure response and the SubscribeRequest's Observe is timing out â€” " +
            "exactly the symptom that caused the /rbuergi Orleans endless-spinner regression.");

        captured.Should().NotBeNull(
            "OnError must propagate the failure, not be swallowed by the timeout-as-success branch.");

        // Pin the content: "No node found" lets the GUI's IsExpectedUserActionFailure
        // classifier match it and render a friendly markdown instead of an endless spinner.
        captured!.Message.Should().Contain("No node found",
            because: "the routing failure must carry the actionable description so the GUI's " +
                     "expected-user-action-failure path matches and shows a friendly error markdown.");
    }

    // -------------------------------------------------------------------------
    // SUCCESS path: existing address â†’ data within a few seconds
    // -------------------------------------------------------------------------

    /// <summary>
    /// Subscribing to a layout area on an existing seeded address ("TestUser")
    /// must produce at least one data emission within a few seconds. This confirms
    /// the full Orleans routing â†’ grain activation â†’ layout area rendering path
    /// works, so a failing not-found test can't be blamed on missing plumbing.
    /// </summary>
    [Fact]
    public async Task GetRemoteStream_on_existing_address_receives_layout_area_data()
    {
        var client = await GetClientAsync("found-" + Guid.NewGuid().ToString("N")[..8]);

        // "TestUser" is seeded in SharedSiloConfigurator + OrleansTestSeedProvider
        // and has AddDefaultLayoutAreas registered on the silo.
        var address = new Address("TestUser");
        var reference = new LayoutAreaReference("Overview");

        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);
        stream.Should().NotBeNull();

        var dataReceived = false;
        Exception? error = null;
        var done = new ManualResetEventSlim(false);

        using var sub = stream.Subscribe(
            _ => { dataReceived = true; done.Set(); },
            ex => { error = ex; done.Set(); },
            () => done.Set());

        var fired = done.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        error.Should().BeNull($"expected data from {address}/Overview, got error: {error?.Message}");
        fired.Should().BeTrue(
            "a layout area stream on an existing address must emit data, not hang. " +
            "If this fails, the Orleans routing â†’ MessageHubGrain activation â†’ layout area path is broken.");
        dataReceived.Should().BeTrue("at least one data emission must arrive from the existing address");
    }
}
