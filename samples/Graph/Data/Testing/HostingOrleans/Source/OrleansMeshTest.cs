// <meshweaver>
// Id: Testing/HostingOrleans/OrleansMeshTest
// DisplayName: Testing/HostingOrleans/OrleansMeshTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using System.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Connection.Orleans;
using Orleans;

using System.Reactive.Linq;
public class OrleansMeshTests(MeshTestContext context) : OrleansMeshTestBase(output)
{
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"mesh-{name}-{Guid.NewGuid():N}", "TestUser");

    [MeshFact(TimeoutSeconds = 30)]
    public async Task PingPong()
    {
        var client = GetClient();
        var response = await client
            .Observe(new PingRequest(), o => o.WithTarget(OrleansTestMeshNodeAttribute.Address)).FirstAsync().Await(new CancellationTokenSource(20.Seconds()).Token);
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }

    [MeshTheory(TimeoutSeconds = 30)]
    [MeshInlineData("HubFactory")]
    [MeshInlineData("Kernel")]
    public async Task HubWorksAfterDisposal(string id)
    {
        var client = GetClient();
        var address = AddressExtensions.CreateAppAddress(id);

        var response = await client
            .Observe(new PingRequest(), o => o.WithTarget(address)).FirstAsync().Await(new CancellationTokenSource(20.Seconds()).Token);
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();

        client.Post(new DisposeRequest(), o => o.WithTarget(address));
        await Task.Delay(500, CancellationToken.None);

        // The probe can land while the hub is STILL disposing (under CI load disposal takes
        // longer than the 500ms above). The hub answers that window with the documented
        // TRANSIENT ShuttingDown NACK — "the address may reactivate … the sender's next probe
        // gets the authoritative answer" (MessageService dispose-reject). Ride it out exactly
        // as the contract prescribes: re-probe on ShuttingDown, bounded by the SAME 20s budget
        // (no widened bound — a hub that never reactivates still fails loudly).
        response = await Observable
            .Defer(() => client
                .Observe(new PingRequest(), o => o.WithTarget(address))
                .FirstAsync())
            .RetryWhen(errors => errors.SelectMany(ex =>
                ex is DeliveryFailureException { Failure.ErrorType: ErrorType.ShuttingDown }
                    ? Observable.Timer(TimeSpan.FromMilliseconds(200))
                    : Observable.Throw<long>(ex)))
            .Await(new CancellationTokenSource(20.Seconds()).Token);
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }
}
