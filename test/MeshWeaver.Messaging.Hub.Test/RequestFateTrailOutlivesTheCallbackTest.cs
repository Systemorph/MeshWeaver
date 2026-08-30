using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>A request's fate trail must outlive the callback that opened it.</b>
///
/// <para>A cross-hub write awaits its owner's reply for a short bound (2 s), then hands the wait to
/// the late-patch watch for ~30 s more. Disposing that first <c>Observe</c> subscription used to
/// drop the trail from the <c>RequestFateLedger</c> — so when the late watch finally reported
/// <c>VERDICT_TIMEOUT</c>, the one record that could say WHERE the request stalled (never received;
/// DEFERRED behind an init gate; HANDLER_EXIT with no reply; RESPONSE_POSTED to the wrong hub) had
/// been gone for 29 s. Seven distinct tests failed with that empty sentence in one night
/// (MeshWeaver.Plugins#941).</para>
///
/// <para>The ledger now MOVES an untracked trail into a bounded recent ring instead of dropping it,
/// and stages keep landing on it — so the owner-side story that unfolds AFTER the requester stopped
/// awaiting is still written down, and <see cref="MessageHubExtensions.DescribeRequestFate"/> can
/// render it for the late diagnostic.</para>
/// </summary>
public class RequestFateTrailOutlivesTheCallbackTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record SlowRequest : IRequest<SlowResponse>;
    private record SlowResponse;

    private readonly TaskCompletionSource<IMessageDelivery> handlerRan = new();

    /// <summary>A handler that runs and deliberately answers nothing, so the requester's Observe
    /// can only end by its own timeout — the shape in which the trail used to vanish.</summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithHandler<SlowRequest>((_, delivery) =>
            {
                handlerRan.TrySetResult(delivery);
                return delivery.Processed();
            });

    [Fact(Timeout = 30_000)]
    public async Task AfterTheRequestersObserveTimedOut_TheTrailStillRendersTheReceivingSide()
    {
        var host = GetHost();

        // The requester gives up after 300 ms — the Observe subscription is then disposed, which
        // is the exact moment the trail used to be dropped.
        var timedOut = false;
        try
        {
            await host
                .Observe<SlowResponse>(new SlowRequest(), o => o.WithTarget(CreateHostAddress()))
                .Timeout(TimeSpan.FromMilliseconds(300))
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        timedOut.Should().BeTrue("the handler never replies, so the requester's bound is what ends the wait");
        // The handler saw the very delivery the requester awaited: its id is the ledger key.
        var handled = await handlerRan.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var requestId = handled.Id;

        var trail = host.DescribeRequestFate(requestId);
        Output.WriteLine(trail);

        trail.Should().NotContain("not tracked",
            "the requester's timeout must not erase the evidence the late diagnostic needs");
        trail.Should().Contain("HANDLER_EXIT",
            "the receiving side's stages must still be readable after the callback is gone — that is "
            + "what tells a VERDICT_TIMEOUT whether the owner ever ran the handler");
        trail.Should().NotContain("RESPONSE_POSTED",
            "nothing replied — the trail must say so rather than be missing");
    }
}
