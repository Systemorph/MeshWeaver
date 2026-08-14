using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #1486 — the NACK was the WEAKEST inbound path in the system.
///
/// <para><b>The asymmetry.</b> Every same-process delivery to a co-hosted hub short-circuits on
/// <c>OrleansRoutingService</c>'s LOCAL ROUTE TABLE and never touches an Orleans stream. The failure
/// leg did not: <c>RoutingGrain.PostFailure</c> published the <see cref="DeliveryFailure"/> to the
/// sender's Orleans stream unconditionally. So a hub whose stream subscription was never attached —
/// or was attached and then lost, which <c>SubscribeWhenStreamingReadyAsync</c> can do while the
/// local route stays live — is reachable for forward traffic and UNREACHABLE for NACKs.</para>
///
/// <para><b>And the failure is silent by construction.</b> A publish to a memory stream with no live
/// subscriber SUCCEEDS: nothing faults, the router's own <c>ContinueWith</c> never sees
/// <c>IsFaulted</c>, and the NACK is simply gone. The requester then waits forever for an answer the
/// router believes it sent — the <c>[STALE-CALLBACK]</c> shape. That is why bounding this leg was
/// explicitly NOT the fix: a timeout converts a silent hang into a logged one without ever
/// delivering the NACK.</para>
///
/// <para><b>What is pinned here</b> is the routing DECISION, not the transport: for a sender this
/// silo hosts, the failure resolves on the same local route the forward message would have taken.
/// The probe is the same one <c>OrleansRoutingShutdownClassificationTest</c> uses — a real hub at
/// the sender's address, so the answer asserted is the actual delivery, not a stand-in.</para>
/// </summary>
public class RoutingFailureTakesTheLocalRouteTest(ITestOutputHelper output) : TestBase(output)
{
    private static readonly Address CoHostedSender = new("portal", "local-route-sender");
    private static readonly Address ElsewhereSender = new("portal", "not-hosted-here");

    private OrleansRoutingService CreateRouter() =>
        new(null!, ServiceProvider, ServiceProvider.GetRequiredService<ILogger<OrleansRoutingService>>());

    /// <summary>
    /// The property the fix turns on: a hub registered on THIS silo is resolvable through the local
    /// route table, so the failure leg has somewhere to send that is not the stream.
    /// </summary>
    [Fact]
    public void ACoHostedSenderIsResolvableThroughTheLocalRoute()
    {
        var routing = CreateRouter();
        var delivered = 0;
        using var _ = routing.RegisterStream(CoHostedSender, (d, _) =>
        {
            delivered++;
            return Observable.Return(d);
        });

        var route = routing.TryGetLocalRoute(CoHostedSender);

        route.Should().NotBeNull(
            "the forward leg short-circuits on this table, and the NACK must be able to take the "
            + "same path — publishing to a stream instead is what made the failure leg the weakest "
            + "inbound path");

        route!.Invoke(NewDelivery(CoHostedSender), CancellationToken.None).Subscribe();
        delivered.Should().Be(1, "resolving the route must actually deliver, not merely find an entry");
    }

    /// <summary>
    /// The other half, and the reason this is a lookup rather than an unconditional change: a sender
    /// on ANOTHER silo has no local route, so the stream remains its only path and the fix must not
    /// pretend otherwise.
    /// </summary>
    [Fact]
    public void ASenderThisSiloDoesNotHostHasNoLocalRoute()
    {
        var routing = CreateRouter();
        using var _ = routing.RegisterStream(CoHostedSender, (d, _) => Observable.Return(d));

        routing.TryGetLocalRoute(ElsewhereSender).Should().BeNull(
            "only a hub this silo hosts is answerable locally — for anything else the Orleans "
            + "stream is still the transport, and claiming otherwise would drop the NACK");
    }

    /// <summary>
    /// Deregistration must be observable, or the failure leg would keep resolving a route to a hub
    /// that is gone — trading a silent stream drop for a silent local drop.
    /// </summary>
    [Fact]
    public void ADeregisteredHubStopsResolving()
    {
        var routing = CreateRouter();
        var registration = routing.RegisterStream(CoHostedSender, (d, _) => Observable.Return(d));

        routing.TryGetLocalRoute(CoHostedSender).Should().NotBeNull();
        registration.Dispose();

        routing.TryGetLocalRoute(CoHostedSender).Should().BeNull(
            "a torn-down hub must stop being resolvable, or the NACK is dropped just as silently "
            + "as it was over the stream");
    }

    private static IMessageDelivery NewDelivery(Address sender) =>
        new MessageDelivery<string>(sender, new Address("SomeNamespace", "SomeNode"), "payload",
            JsonSerializerOptions.Default);
}
