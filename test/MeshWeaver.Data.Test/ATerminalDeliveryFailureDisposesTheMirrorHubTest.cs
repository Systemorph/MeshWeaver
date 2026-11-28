using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A mirror that ends on a terminal <see cref="DeliveryFailure"/> disposes its sync hub, so no
/// recovery path (change-feed resubscribe latch, end/recycle re-arm) stays registered on a faulted
/// mirror. Those paths re-subscribe an infrastructure mirror as System, so a faulted mirror that
/// kept them alive kept re-subscribing with System rights.
/// </summary>
public class ATerminalDeliveryFailureDisposesTheMirrorHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddData(data => data);

    /// <summary>
    /// Subscribing to an owner address that does not exist yields a terminal failure; the mirror's
    /// sync hub must then be torn down.
    /// </summary>
    [HubFact]
    public async Task AMirrorFaultedByATerminalDeliveryFailure_LeavesNoLiveSyncHub()
    {
        var client = GetClient();
        var mirror = client.GetWorkspace()
            .GetRemoteStream<EntityStore>(new Address("nowhere", "absent"), new CollectionsReference("Anything"));
        var syncHub = mirror.Hub;

        var ended = mirror.Materialize()
            .Where(n => n.Kind == NotificationKind.OnError)
            .Select(n => n.Exception!)
            .Replay(1);
        using var connection = ended.Connect();

        var error = await ended.Should().Within(TestTimeouts.Convergence)
            .Emit("the missing owner is a terminal verdict for the mirror");
        error.Should().BeOfType<DeliveryFailureException>();

        await syncHub.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("a faulted mirror serves nobody, so its sync hub must be disposed with it");
    }
}
