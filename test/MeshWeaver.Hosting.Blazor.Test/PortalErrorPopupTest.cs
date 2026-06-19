using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// "We actually get the popup": a failed post ORIGINATING FROM THE PORTAL HUB must surface to
/// the GUI. The portal hub here is wired exactly as <c>DefaultPortalConfig</c> wires it
/// (<see cref="PortalErrorReporting.WithPortalErrorReporting"/>); a downstream hub rejects a
/// fire-and-forget post with a <see cref="DeliveryFailure"/>; that failure returns to the
/// portal hub and must land on the <see cref="PortalErrorSink"/> — the exact source the modal
/// (<c>PortalErrorModal</c>) subscribes to. If the sink emits, the user gets the popup.
/// </summary>
public class PortalErrorPopupTest(ITestOutputHelper output) : HubTestBase(output)
{
    record FailingRequest;

    [Fact]
    public async Task FailedPortalPost_SurfacesToTheErrorSink()
    {
        using var sink = new PortalErrorSink();

        // Downstream hub that FAILS the delivery the SAME way the never-null AccessContext guard
        // does (MessageHubConfiguration: return delivery.Failed(...) — no throw). The framework's
        // NotifyAsync sees State == Failed and ReportFailure posts a DeliveryFailure back to the
        // sender. That IS the failed-message pipe; the portal hub is the consumer.
        var target = Mesh.GetHostedHub(new Address("target", "1"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<FailingRequest>((_, d) =>
                d.Failed("boom: the post could not be completed")));

        // Portal hub wired exactly as DefaultPortalConfig wires error reporting.
        var portal = Mesh.GetHostedHub(new Address("portal", "1"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithPortalErrorReporting(sink));

        // Subscribe to the popup source BEFORE provoking the failure.
        var popup = sink.Errors.FirstAsync().ToTask();

        // Fire-and-forget post from the portal — no Observe callback, so the returning
        // DeliveryFailure has nothing to consume it but the error-reporting handler.
        portal.Post(new FailingRequest(), o => o.WithTarget(target.Address));

        var shown = await popup.WaitAsync(TimeSpan.FromSeconds(10));
        shown.Should().Contain("boom",
            "a failed post originating from the portal hub must surface to the GUI as a popup");
    }
}
