using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the wedge-safety invariant for the root mesh hub: it is routing INFRASTRUCTURE, not a
/// message endpoint. A message addressed directly to it (its <c>mesh/{id}</c> address is a transient
/// per-process id, never an addressable node) reaches no handler — and the mesh hub must
/// <b>Ignore</b> it (logging an error to expose the abusing sender), <b>never</b> answer a
/// <see cref="DeliveryFailure"/> back. Echoing a failure to the sender is the classic wedge: the
/// sender treats it as a fault and retries → storm → the 60s-timeout mesh-wide outage class.
///
/// This is the exact inverse of <see cref="UnhandledMessageReportsFailureTest"/> (a normal host
/// hub DOES NACK unhandled requests) — here the sender must observe a <see cref="TimeoutException"/>
/// (no response) rather than <see cref="DeliveryFailureException"/>.
/// </summary>
public class MeshHubNotAnEndpointTest(ITestOutputHelper output) : HubTestBase(output)
{
    record ProbeRequest : IRequest<ProbeResponse>;
    record ProbeResponse;

    [Fact]
    public async Task RequestAddressedToMeshHub_IsIgnored_NeverNacksBackToSender()
    {
        // Mesh is a mesh-typed hub (HubTestBase creates it at CreateMeshAddress()); it has no
        // handler for ProbeRequest, so the delivery reaches FinishDelivery unhandled. The
        // mesh-hub guard must Ignore it — so the client's observe TIMES OUT (no response),
        // it must NOT throw DeliveryFailureException (which a normal hub would).
        var client = GetClient();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.Observe(new ProbeRequest(), o => o.WithTarget(Mesh.Address))
                .Timeout(TimeSpan.FromSeconds(3))
                .FirstAsync()
                .ToTask());
    }
}
