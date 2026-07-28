using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the CLASSIFICATION half of the recycle-window contract: when a handler cannot complete
/// because the hub it needs is tearing down — signalled by <see cref="HubDisposingException"/> —
/// the sender must receive the TRANSIENT <see cref="ErrorType.ShuttingDown"/>, never a generic
/// terminal failure.
///
/// <para><b>Why this matters.</b> A disposing hub cannot know whether its address is gone for
/// good or about to REACTIVATE (recycle / restart), so "ask again" is the only honest answer.
/// #672 established exactly this for the two paths the MESSAGING layer owns (a message arriving
/// after the intake gate closed, and a delivery abandoned in the deferred queue at disposal).
/// The DATA/LAYOUT layer has a third: hosted-hub creation is frozen from the first instant of
/// <c>Dispose</c> — strictly BEFORE the intake gate closes — so a <c>SubscribeRequest</c> in
/// that window is accepted, reaches <c>LayoutAreaHost</c>, and cannot build its
/// <c>SynchronizationStream</c>. Reported as <see cref="ErrorType.Unknown"/> that killed the
/// subscriber's resubscribe latch and the page never came back.</para>
///
/// <para>Also pinned: the exception reaches <c>MessageService</c> WRAPPED (the layout subscribe
/// path dispatches through reflection, so the real cause arrives inside a
/// <see cref="System.Reflection.TargetInvocationException"/>). The classifier must walk the
/// inner-exception chain, not test the outermost type — a top-level-only check would have
/// missed the production case entirely.</para>
/// </summary>
public class HubDisposalFailureClassificationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record DirectRequest : IRequest<Ack>;

    private record WrappedRequest : IRequest<Ack>;

    private record OrdinaryFailureRequest : IRequest<Ack>;

    private record Ack;

    private static readonly Address ServerAddress = new("host", "1");

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(DirectRequest), typeof(WrappedRequest), typeof(OrdinaryFailureRequest), typeof(Ack))
            .WithHandler<DirectRequest>((h, _) => throw new HubDisposingException(h.Address, "area/Overview"))
            .WithHandler<WrappedRequest>((h, _) => throw new System.Reflection.TargetInvocationException(
                new HubDisposingException(h.Address, "area/Overview")))
            .WithHandler<OrdinaryFailureRequest>((_, _) => throw new InvalidOperationException("a genuine bug"));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(DirectRequest), typeof(WrappedRequest), typeof(OrdinaryFailureRequest), typeof(Ack));

    [HubFact]
    public async Task HubDisposalDuringHandling_IsReportedAsTransient()
    {
        var failure = await Nack(new DirectRequest());
        failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "the address may reactivate, so the caller must read this as 'ask again'; consumers "
            + "with their own recovery machinery (SynchronizationStream's resubscribe latch) ride "
            + "it out instead of tearing down");
    }

    [HubFact]
    public async Task WrappedHubDisposal_IsStillReportedAsTransient()
    {
        var failure = await Nack(new WrappedRequest());
        failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "the production path (Workspace.SubscribeToClient's reflective dispatch) delivers the "
            + "cause wrapped in a TargetInvocationException — classifying only the outermost type "
            + "would miss every real occurrence");
    }

    [HubFact]
    public async Task AnOrdinaryHandlerBug_StaysTerminal()
    {
        var failure = await Nack(new OrdinaryFailureRequest());
        failure.Failure!.ErrorType.Should().NotBe(ErrorType.ShuttingDown,
            "only a hub-disposal race is retryable — dressing a genuine bug as transient would "
            + "turn a hard failure into an invisible retry loop");
    }

    private async Task<DeliveryFailureException> Nack(IRequest<Ack> request)
    {
        var client = GetClient();
        var response = client
            .Observe<Ack>(request, o => o.WithTarget(ServerAddress))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        Output.WriteLine($"{request.GetType().Name}: errorType={failure.Failure?.ErrorType} message={failure.Failure?.Message}");
        failure.Failure.Should().NotBeNull();
        return failure;
    }
}
