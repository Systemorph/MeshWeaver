using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the "the target hub did not TREAT this message" contract — the emit half of MessageIgnored →
/// page-not-found. When a delivery reaches a hub but NO registered handler processes it, the hub must post
/// a typed <see cref="DeliveryFailure"/> back to the sender (never a silent drop), so an awaiting caller
/// (e.g. Blazor navigation) gets <see cref="DeliveryFailureException"/> and can surface page-not-found:
/// <list type="bullet">
///   <item>an unhandled <c>IRequest&lt;T&gt;</c> → <see cref="ErrorType.NotFound"/> (MessageHub.FinishDelivery);</item>
///   <item>an unhandled non-request → <see cref="ErrorType.Ignored"/> (MessageService reports the on-target Ignored).</item>
/// </list>
/// </summary>
public class UnhandledMessageReportsFailureTest(ITestOutputHelper output) : HubTestBase(output)
{
    // The host (base ConfigureHost) registers NO handler for these, so they reach it unhandled.
    record UnhandledRequest : IRequest<UnhandledResponse>;
    record UnhandledResponse;
    record UnhandledNotification;

    [Fact]
    public async Task UnhandledRequest_PostsDeliveryFailure_NotFound()
    {
        var host = GetHost();
        var ex = await Assert.ThrowsAsync<DeliveryFailureException>(() =>
            host.Observe(new UnhandledRequest(), o => o.WithTarget(CreateHostAddress()))
                .Timeout(TimeSpan.FromSeconds(10)).FirstAsync().ToTask());

        ex.Failure.ErrorType.Should().Be(ErrorType.NotFound,
            "an unhandled IRequest<T> must come back as DeliveryFailure{NotFound}, never a silent drop");
    }

    [Fact]
    public async Task UnhandledNotification_PostsDeliveryFailure_Ignored()
    {
        var host = GetHost();
        // A non-request can't be observed via the typed Observe<T> (it needs IRequest<T>), so capture the
        // failure routed back to the SENDER via a DeliveryFailure handler. Post (not Observe) → no callback
        // intercepts the DeliveryFailure, so it flows to the client's rule chain.
        var failure = new TaskCompletionSource<DeliveryFailure>();
        var client = GetClient(c => c
            .WithHandler<DeliveryFailure>((_, d) => { failure.TrySetResult(d.Message); return d.Processed(); })
            .WithPostingIdentity(PostingIdentity.System));

        client.Post(new UnhandledNotification(), o => o.WithTarget(CreateHostAddress()));

        var result = await failure.Task.WaitAsync(TimeSpan.FromSeconds(10));
        result.ErrorType.Should().Be(ErrorType.Ignored,
            "an unhandled non-request reaching its target hub must come back as DeliveryFailure{Ignored} " +
            "(the typed 'message not treated on the target hub' signal), never a silent drop");
    }
}
