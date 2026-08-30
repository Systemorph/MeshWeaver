using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 Disposing a remote synchronization stream must cancel its in-flight
/// <see cref="SubscribeRequest"/> <b>synchronously</b> — not several hub action-block turns later
/// via the inner sync hub's ShutDown phase.
///
/// <para><b>The defect (#1613).</b> <c>SynchronizationStream.RegisterForDisposal</c> forwarded every
/// registrant to <c>Hub.RegisterForDisposal</c>, i.e. to the INNER <c>sync/</c> hub's composite,
/// which is walked in the ShutDown phase. But <c>MessageHub.Dispose()</c> disposes nothing
/// synchronously: it closes hosted-hub creation, cancels in-flight handlers, posts
/// <c>ShutdownRequest(Quiescing)</c> and returns. So the registrant that RELEASES the pending
/// callback — the <c>hub.Observe(SubscribeRequest)</c> subscription, whose disposal is what removes
/// the entry from <c>responseSubjects</c> (<c>MessageHub.WrapWithCancelOnDispose</c>) — was reached
/// only after the inner hub walked <c>Quiescing → DisposeHostedHubs → HostedHubsDisposed →
/// ShutDown</c>. Several posted messages, bounded by nothing, on a host that is itself tearing
/// down.</para>
///
/// <para><b>Why it read as a flake.</b> Normally the callback is not closed by disposal at all — the
/// owner answers in milliseconds and <c>.Take(1)</c> closes it the ordinary way. The leak is only
/// VISIBLE when the owner never answers, which in CI is the loaded case where the target hub is
/// still <c>Starting</c> and the delivery sits <c>DEFERRED gates=[DataContextInit,Initialize]</c>.
/// The leak was present in every run; only its visibility was load-dependent. It surfaced as
/// <c>RenderAreaOperationTest</c> failing in TEARDOWN — *"left Observe subscriptions pending past the
/// Quiescing budget … 1 pending callback(s) after 0.50s: SubscribeRequest@…"* — on branches whose
/// diff had no path to RenderArea at all, and it turned <b>main</b> red at least twice.</para>
///
/// <para>Any consumer that disposes a remote stream while its subscribe is in flight hits the same
/// window: a Blazor circuit dropping a layout area, a cancelled SSR render, an agent's
/// <c>RenderArea</c> giving up on its budget.</para>
/// </summary>
public class RemoteStreamDisposeCancelsSubscribeTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Signals each <see cref="SubscribeRequest"/> the owner swallows — instance state, never
    /// static, so nothing bleeds across tests.
    /// </summary>
    private readonly ReplaySubject<string> subscribeReceived = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            // The owner ACCEPTS the SubscribeRequest and never answers it: no ack, no
            // DataChangedEvent, no failure. That is the shape of a target hub still working
            // through its init gates — the delivery is received and deferred, and no reply is
            // ever sent. The client's pending callback therefore has exactly one possible
            // closer left: the stream's disposal.
            .WithHandler<SubscribeRequest>((_, delivery) =>
            {
                subscribeReceived.OnNext(delivery.Message.StreamId);
                return delivery.Processed();
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddData();

    [HubFact]
    public async Task DisposingTheStream_ReleasesThePendingSubscribeCallback_Synchronously()
    {
        GetHost(); // activate the swallowing owner
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        var stream = workspace.GetRemoteStream<EntityStore, CollectionsReference>(
            CreateHostAddress(), new CollectionsReference("ghost"));

        // Wait on the ACTUAL condition — the owner holds the SubscribeRequest, so the client's
        // pending callback exists. No sleeps.
        await subscribeReceived
            .Where(id => id == stream.StreamId)
            .FirstAsync()
            .Timeout(10.Seconds())
            .Await();

        // Precondition that makes the seam live. Without it a "released" assertion after Dispose
        // would pass against a hub that never had the callback in the first place.
        client.GetPendingRequestDiagnostics().Should().Contain(nameof(SubscribeRequest),
            "the owner accepted the subscribe and never answered, so the client must be holding a "
            + "pending callback for it — otherwise this test proves nothing");

        // THE CONTRACT: one synchronous call, then the callback is gone. No polling, no budget —
        // a bounded wait here would pass against the unfixed code the moment the budget exceeded
        // the inner hub's shutdown walk, which is precisely the load-dependence that made this
        // read as a flake.
        stream.Dispose();

        var afterDispose = client.GetPendingRequestDiagnostics();
        afterDispose.Should().NotContain(nameof(SubscribeRequest),
            "disposing a remote stream must cancel its in-flight SubscribeRequest THERE AND THEN. "
            + "Routed through the inner sync hub's ShutDown phase instead, the release takes several "
            + "posted messages and action-block turns — long enough for the teardown leak check to "
            + "fire, and for the callback to sit in responseSubjects until the ~30 s RequestTimeout. "
            + "Diagnostics: " + afterDispose);
    }

    /// <summary>
    /// The same contract, stated on the general seam rather than on one registrant: anything
    /// coupled to the stream with <c>RegisterForDisposal</c> is released by
    /// <c>stream.Dispose()</c> itself, not by the hub's later ShutDown phase.
    /// </summary>
    [HubFact]
    public async Task RegisterForDisposal_IsReleasedByTheStreamsOwnDispose()
    {
        GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        var stream = workspace.GetRemoteStream<EntityStore, CollectionsReference>(
            CreateHostAddress(), new CollectionsReference("ghost2"));

        await subscribeReceived
            .Where(id => id == stream.StreamId)
            .FirstAsync()
            .Timeout(10.Seconds())
            .Await();

        var released = false;
        stream.RegisterForDisposal(System.Reactive.Disposables.Disposable.Create(() => released = true));
        released.Should().BeFalse("registration must not dispose eagerly");

        stream.Dispose();

        released.Should().BeTrue(
            "RegisterForDisposal documents 'disposed when the stream is disposed' — before #1613 it "
            + "meant 'disposed when the stream's inner hub reaches its ShutDown phase', which is an "
            + "unbounded number of action-block turns later");
    }
}
