using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>A post the intake REFUSED must say so in the envelope it hands back.</b>
///
/// <para><c>MessageService.ScheduleNotify</c> is where a post is dropped without ever being
/// enqueued: the per-key storm breaker and the aggregate shedder both <c>return
/// delivery.Ignored()</c> having queued nothing, and the teardown intake gate returns
/// <c>Failed</c>/<c>FailedAndNacked</c>. <c>MessageDelivery.ChangeState</c> is
/// <c>this with { State = state }</c> — a NEW record — so <c>PostImplGeneric</c> returning the
/// pre-pipeline delivery threw every one of those verdicts away and handed the poster a
/// <c>Submitted</c> envelope for a message that had, in fact, been dropped.</para>
///
/// <para><b>What that made inert.</b> <c>MeshExtensions.WasCarried</c> — the claim-then-verify on
/// both create-verdict posts — states in its own documentation that <c>Ignored</c> <i>"is the one
/// that reads like a success: the storm breaker and the aggregate shedder return it WITHOUT
/// enqueueing anything, so treating it as carried would claim the once-only gate, skip the parent
/// fallback, and leave the caller waiting out its budget for a verdict that was dropped on the
/// floor."</i> It could never see one. Every post — local or routed — goes through this same
/// <c>ScheduleNotify</c>, and its verdict never reached the caller, so a guard written to catch
/// exactly this could not fire. A verification step that cannot fail is not a verification
/// step.</para>
///
/// <para>Measured on MeshWeaver#1174: node CRUD runs on <c>portal/nodeops-{meshId}</c> and
/// <c>MeshService</c> ISSUES it there too, so request, handler and reply are all one hub — the
/// only thing that can lose a delivery is that hub's own intake, and the poster was told
/// nothing.</para>
/// </summary>
public class PostSurfacesARefusedIntakeTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Sheddable by the aggregate shedder: <c>[CanBeIgnored]</c> and not one of the four
    /// lifecycle types it refuses to shed (<c>ShutdownRequest</c>, <c>DisposeRequest</c>,
    /// <c>DeliveryFailure</c>, <c>InitializeHubRequest</c>). Nobody awaits it, which is why
    /// shedding it is legitimate — the defect under test is not the drop, it is the SILENCE
    /// about the drop.
    /// </summary>
    [CanBeIgnored]
    private record Sheddable;

    /// <summary>
    /// A watermark of 0 makes the aggregate shedder fire on the very first sheddable message
    /// (<c>inboundDepth &lt; aggregateWatermark</c> is false for every depth), so the drop is
    /// DETERMINISTIC — no flooding, no queue-depth race, no load generated on the host.
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(Sheddable))
            .WithAggregateWatermark(0);

    [Fact]
    public void APostTheAggregateShedderDropped_IsReportedAsIgnored_NotAsSubmitted()
    {
        var host = GetHost();

        var posted = host.Post(new Sheddable(), o => o.WithTarget(host.Address));

        Assert.NotNull(posted);
        Assert.Equal(MessageDeliveryState.Ignored, posted!.State);
    }

    /// <summary>
    /// The other half, and the one a change here could silently break: the SUCCESS path must keep
    /// handing back exactly the envelope it always did. Only a DROP is surfaced — a caller that
    /// reads the returned delivery on the happy path sees no change.
    /// </summary>
    [Fact]
    public void APostThatWasAccepted_IsUnchanged()
    {
        // A CLIENT hub, configured here with the DEFAULT watermark: nothing is shed, so the same
        // post is accepted and the returned envelope must be exactly what it always was.
        var client = GetClient(c => c
            .WithTypes(typeof(Sheddable))
            .WithPostingIdentity(PostingIdentity.System));

        var posted = client.Post(new Sheddable(), o => o.WithTarget(client.Address));

        Assert.NotNull(posted);
        Assert.Equal(MessageDeliveryState.Submitted, posted!.State);
    }
}
