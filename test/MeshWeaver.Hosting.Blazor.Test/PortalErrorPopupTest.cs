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
/// (<see cref="PortalErrorReporting.WithPortalErrorReporting"/>); its own post pipeline rejects a
/// fire-and-forget post with <c>delivery.Failed(...)</c> — the SAME mechanism the never-null
/// AccessContext guard uses. <c>MessageService.Post</c> turns that Failed result into a
/// <see cref="DeliveryFailure"/> routed back to the sender (the portal), where it lands on the
/// <see cref="PortalErrorSink"/> — the exact source the modal (<c>PortalErrorModal</c>)
/// subscribes to. If the sink emits, the user gets the popup.
/// </summary>
public class PortalErrorPopupTest(ITestOutputHelper output) : HubTestBase(output)
{
    record FailingRequest;

    [Fact]
    public async Task FailedPortalPost_SurfacesToTheErrorSink()
    {
        using var sink = new PortalErrorSink();

        // Portal hub wired exactly as DefaultPortalConfig wires error reporting, PLUS a
        // post-pipeline step that FAILS the delivery the SAME way the never-null AccessContext
        // guard does — UserServicePostPipeline returns delivery.Failed(...), no throw. The post
        // pipeline rejecting a post is the REAL production trigger (and the ONLY one that turns a
        // failed post into a sink event): MessageService.Post sees the Failed result and calls
        // ReportFailure, which posts a DeliveryFailure back to the sender; that DeliveryFailure
        // lands on this same portal hub and the WithPortalErrorReporting handler pushes it to the
        // sink — the exact source PortalErrorModal subscribes to. (A handler that returns
        // delivery.Failed(...) on a DOWNSTREAM hub does NOT produce a DeliveryFailure — only the
        // sender-side post-pipeline rejection and the unhandled-request paths do — so we drive the
        // genuine pipe here rather than simulating it.)
        var portal = Mesh.GetHostedHub(new Address("portal", "1"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
                d.Message is FailingRequest
                    ? d.Failed("boom: the post could not be completed")
                    : next(d)))
            .WithPortalErrorReporting(sink));

        // Subscribe to the popup source BEFORE provoking the failure.
        var popup = sink.Errors.FirstAsync().ToTask();

        // Fire-and-forget post from the portal — its own post pipeline rejects it, and the
        // returning DeliveryFailure has nothing to consume it but the error-reporting handler.
        portal.Post(new FailingRequest());

        var shown = await popup.WaitAsync(TimeSpan.FromSeconds(10));
        shown.Should().Contain("boom",
            "a failed post originating from the portal hub must surface to the GUI as a popup");
    }

    record UnansweredRequest : IRequest<UnansweredResponse>;
    record UnansweredResponse;

    /// <summary>
    /// The inverse contract: an AWAITED failure must NOT pop the modal. A
    /// <c>hub.Observe(...)</c> caller receives the <see cref="DeliveryFailure"/> through its
    /// own <c>OnError</c> and handles it there (retry, fallback, user message) —
    /// <c>HandleCallbacks</c> stamps the delivery <see cref="PostOptions.CallbackDispatched"/>
    /// and the error-reporting filter skips it. Before this filter, the raw failure text
    /// popped as a blocking modal EVEN WHEN the caller recovered — the "Access denied: …
    /// lacks Thread permission on 'Doc'" modal shown while StartThread's user-partition
    /// fallback had already re-anchored the thread.
    /// </summary>
    [Fact]
    public async Task AwaitedFailure_DoesNotSurfaceToTheErrorSink()
    {
        using var sink = new PortalErrorSink();
        var reported = new List<string>();
        using var _ = sink.Errors.Subscribe(reported.Add);

        var portal = Mesh.GetHostedHub(new Address("portal", "2"), c => c
            .WithPortalErrorReporting(sink));
        // A hub with NO handler for the request: FinishDelivery answers the awaited request
        // with a DeliveryFailure (NotFound) routed back to the portal — the awaited-failure shape.
        var silent = Mesh.GetHostedHub(new Address("silent", "1"), c => c);

        // The Observe caller consumes the failure via OnError — the per-callsite surface.
        var observed = await portal
            .Observe(new UnansweredRequest(), o => o.WithTarget(silent.Address))
            .Materialize()
            .FirstAsync()
            .ToTask()
            .WaitAsync(TimeSpan.FromSeconds(10));

        observed.Kind.Should().Be(System.Reactive.NotificationKind.OnError,
            "the unhandled request must come back to the Observe caller as a failure");
        observed.Exception.Should().BeOfType<DeliveryFailureException>();

        // Negative assertion: give a residual report a short window to land, then require none.
        // (Sanctioned "confirm nothing happened" delay — there is no positive signal to await.)
        await Task.Delay(500);
        reported.Should().BeEmpty(
            "an awaited failure is handled at its call site's OnError and must not ALSO pop the global error modal");
    }
}
