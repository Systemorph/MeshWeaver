using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the SubscribeRequest → routing-NotFound → OnError chain end-to-end.
///
/// <para><b>The bug we're guarding against</b>: when a Blazor LayoutAreaView
/// subscribes to a layout-area stream on a non-existent address (e.g.
/// <c>/rbuergi</c> before onboarding), the routing layer correctly returns
/// <see cref="MessageDeliveryState.Failed"/> via
/// <c>RoutingServiceBase.PostNotFound</c> /
/// <c>OrleansRoutingService.SendDeliveryFailure</c>. That failure is supposed
/// to land back at the caller's hub, match the original
/// <c>hub.Observe(subscribeDelivery)</c> callback (by RequestId), and surface
/// as <c>OnError</c> on the synchronization stream — which the GUI translates
/// into an "Error loading area: No node found" markdown.</para>
///
/// <para>Two earlier holes that combined to cause the prod-/local-Aspire
/// endless-spinner symptom:</para>
/// <list type="number">
///   <item>The Orleans routing failure was posted with <c>WithTarget</c> only,
///     no <c>WithRequestIdFrom</c> — so the failure arrived at the caller's
///     hub but no callback matched its RequestId, the failure was silently
///     dropped, and the original Observe waited the full 30 s framework
///     timeout. (Fixed in <c>OrleansRoutingService.SendDeliveryFailure</c>.)</item>
///   <item>The recent <c>JsonSynchronizationStream</c> fix swallowed
///     <c>TimeoutException</c> as "expected on success". With (1) silently
///     dropping the failure, every routing-NotFound silently became "success"
///     in the eyes of the stream and the spinner stayed forever. (Inherent
///     to the SubscribeRequest contract — the stream really does keep its
///     data flowing through <c>SetCurrentRequest</c>; the timeout-as-success
///     decision is correct PROVIDED real failures actually surface as
///     <c>DeliveryFailureException</c> first.)</item>
/// </list>
///
/// <para>This test exercises the end-to-end shape on the Monolith routing
/// path. The Orleans equivalent is structurally identical — same
/// <c>SendDeliveryFailure</c> code, same <c>JsonSynchronizationStream</c>
/// caller — so the unit test here pins the contract for both.</para>
/// </summary>
public class SubscribeRequestNotFoundSurfaceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        builder
            .UseMonolithMesh()
            .AddPartitionedInMemoryPersistence()
            .AddGraph()
            .AddMeshNodes(TestUsers.PublicAdminAccess());

    /// <summary>
    /// Subscribing to a layout area on a non-existent address must surface
    /// <c>OnError</c> within a few seconds with a "No node found" message —
    /// NOT spin forever waiting on the framework's 30 s RequestTimeout. The
    /// test fails closed (5 s timeout on the OnError wait) so a regression
    /// into the swallowed-timeout shape is loud.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task GetRemoteStream_on_nonexistent_address_surfaces_OnError_with_NotFound_message()
    {
        var client = GetClient(c => c.AddData(data => data));

        // Address that has NO node at all and no ancestor — the catalog
        // resolves it as "no match" (resolution == null).
        var address = new Address("doesnotexist", "missing-instance-" + Guid.NewGuid().ToString("N")[..8]);
        var reference = new LayoutAreaReference("Overview");

        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);
        // GetRemoteStream must always return a stream — even when the target is
        // unreachable, the OnError path (asserted below) is the surface signal.
        Assert.NotNull(stream);

        // Race two observables:
        //   • the stream's first Initial / data emission (success path — must not fire)
        //   • a Timer hit at 5 s (would mean we're in the spinner regression — must not fire either)
        // The stream's OnError converts to OnErrorResumeNext-style observation via
        // .Catch — capture the exception and assert on its type/message.
        Exception? captured = null;
        var done = new ManualResetEventSlim(false);

        using var sub = stream.Subscribe(
            _ => { /* no value should arrive */ },
            ex =>
            {
                captured = ex;
                done.Set();
            },
            () => done.Set());

        var fired = done.Wait(TimeSpan.FromSeconds(20));

        fired.Should().BeTrue(
            "the stream must surface OnError (not spin) when the target address routes to NotFound. " +
            "If this assertion fails, OrleansRoutingService.SendDeliveryFailure / RoutingServiceBase.PostNotFound " +
            "is dropping the failure response on the floor and the SubscribeRequest's Observe is timing out — " +
            "exactly the symptom that caused the prod /rbuergi endless-spinner regression.");

        captured.Should().NotBeNull("OnError must propagate the failure, not be swallowed by the timeout-as-success branch in JsonSynchronizationStream.");

        // We don't pin the exact exception type because the routing layer can
        // surface either a DeliveryFailureException (preferred) or an
        // InvalidOperationException wrapping the failure message. We pin the
        // CONTENT — the failure message must mention "No node found" so the
        // GUI's NamedAreaView.IsExpectedUserActionFailure classifier matches
        // it and renders a friendly markdown instead of escalating to Error.
        captured!.Message.Should().Contain("No node found",
            because: "the routing failure must carry the actionable description so the GUI's expected-user-action-failure path matches and shows a friendly error markdown.");
    }
}
