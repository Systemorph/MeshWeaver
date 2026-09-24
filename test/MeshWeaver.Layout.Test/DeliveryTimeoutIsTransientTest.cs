using System;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// A transport timeout on a view's control stream is a hub that did not answer IN TIME — retried,
/// bounded — never a permanent error rendered as the area's content (issues #5599, #5605, #5624).
///
/// <para>Across the grain boundary the <see cref="TimeoutException"/> object does not survive: the
/// router posts its message text as a <see cref="DeliveryFailure"/> with
/// <see cref="ErrorType.Failed"/>. The classifier matched none of that text, so NamedAreaView fell
/// through to its generic arm — an Error log (one incident per occurrence), no retry, and the raw
/// Orleans banner rendered until a reload. The messages below are the production ones, verbatim.</para>
/// </summary>
public class DeliveryTimeoutIsTransientTest
{
    private const string ResponseTimeout =
        "Delivery to 'Store' failed: Response did not arrive on time in 00:00:30 for message: Request "
        + "[S10.244.9.163:11111:149190240 sys.client/hosted-10.244.9.163:11111@149190240]->"
        + "[S10.244.3.240:11111:149190162 messagehub/Store] "
        + "MeshWeaver.Connection.Orleans.IMessageHubGrain.DeliverMessage(MeshWeaver.Messaging.IMessageDelivery) #2F39412CFB5DCBF6. ";

    private const string PlacementTimeout =
        "Delivery to 'Store' failed: Grain placement operation timed out for grain messagehub/Store.";

    private static DeliveryFailureException Routed(string message, ErrorType errorType = ErrorType.Failed) =>
        new(new DeliveryFailure(null!, message) { ErrorType = errorType });

    /// <summary>The three production shapes: both transport timeouts, and the router's typed transient verdict.</summary>
    [Theory]
    [InlineData(ResponseTimeout, ErrorType.Failed)]
    [InlineData(PlacementTimeout, ErrorType.Failed)]
    [InlineData("Delivery to 'Approvals/Workspace' failed: Forwarding failed: tried to forward message to invalid activation. Rejecting now.",
        ErrorType.ShuttingDown)]
    public void ATransportTimeoutOrTypedTransient_IsRetried(string message, ErrorType errorType)
    {
        var error = Routed(message, errorType);
        Assert.True(AreaErrorClassifier.IsTransientHubFailure(error));
        Assert.True(AreaErrorClassifier.ShouldRetryArea(error));
    }

    /// <summary>
    /// 🚨 Negative control: widening WHAT is retried must not reach the verdicts that are terminal by
    /// construction — a gone node, a hub whose initialisation failed, a NO VERDICT, a real exception.
    /// Retrying those is the inexistent-address storm.
    /// </summary>
    [Theory]
    [InlineData("No node found at 'rbuergi/_Activity/markdown-1'. Closest ancestor is 'rbuergi'", ErrorType.NotFound)]
    [InlineData("Could not authorize the read", ErrorType.Unavailable)]
    [InlineData("Object reference not set to an instance of an object.", ErrorType.Exception)]
    [InlineData("Delivery to 'Store' failed at its owning hub.", ErrorType.Failed)]
    public void ATerminalVerdict_IsStillNotRetried(string message, ErrorType errorType)
        => Assert.False(AreaErrorClassifier.ShouldRetryArea(Routed(message, errorType)));

    /// <summary>
    /// End to end through the view's own retry: the control stream faults once with the #5599 banner
    /// and the resubscribe is served — the view renders instead of dying.
    /// </summary>
    [Fact]
    public void TheControlStream_RecoversFromOneResponseTimeout()
    {
        var scheduler = new TestScheduler();
        var subscriptions = 0;
        var source = Observable.Defer(() => ++subscriptions == 1
            ? Observable.Throw<int>(Routed(ResponseTimeout), scheduler)
            : Observable.Return(42, scheduler));

        var observer = scheduler.CreateObserver<int>();
        source.RetryAreaWithBackoff(AreaErrorClassifier.ShouldRetryArea, scheduler: scheduler).Subscribe(observer);
        scheduler.Start();

        Assert.Equal(2, subscriptions);
        Assert.Contains(observer.Messages, m => m.Value.Kind == NotificationKind.OnNext && m.Value.Value == 42);
        Assert.DoesNotContain(observer.Messages, m => m.Value.Kind == NotificationKind.OnError);
    }

    /// <summary>
    /// Negative control for the end-to-end: a terminal failure is surfaced on the FIRST subscription,
    /// so the fix did not turn the retry into a blanket resubscribe.
    /// </summary>
    [Fact]
    public void AGoneNode_IsSurfacedAtOnce_NotResubscribed()
    {
        var scheduler = new TestScheduler();
        var subscriptions = 0;
        var source = Observable.Defer(() =>
        {
            subscriptions++;
            return Observable.Throw<int>(Routed("No node found at 'x'.", ErrorType.NotFound), scheduler);
        });

        var observer = scheduler.CreateObserver<int>();
        source.RetryAreaWithBackoff(AreaErrorClassifier.ShouldRetryArea, scheduler: scheduler).Subscribe(observer);
        scheduler.Start();

        Assert.Equal(1, subscriptions);
        Assert.Single(observer.Messages, m => m.Value.Kind == NotificationKind.OnError);
    }
}
