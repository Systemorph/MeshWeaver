using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the teardown terminal state of <see cref="SynchronizationStream{TStream}"/>'s store
/// (issues #1170/#1171): disposal COMPLETES the underlying subject — it never DISPOSES it.
///
/// <para><b>The production sequence</b> (one event, logged twice — fingerprints
/// <c>4f955f0319f9bea9</c> / <c>0857ac252fa6c3e9</c>, pod memex-portal, 2026-08-10 12:55:52Z):
/// during MessageHub shutdown, sync hubs dispose in parallel, each disposal action on its own
/// action block. Thread 1 runs a stream's <c>Dispose()</c> → <c>Store.OnCompleted()</c>, and
/// that completion drains synchronously through the <c>Synchronize()</c> chain that
/// <c>CreateReducedStream</c> wires (<c>WorkspaceStreams.cs</c>,
/// <c>.Subscribe(reducedStream)</c>) into a SIBLING stream's <c>OnCompleted()</c>. Thread 2 is
/// concurrently running that sibling's own <c>Dispose()</c>, which used to call
/// <c>Store.Dispose()</c>. The sibling's <c>OnCompleted</c> guard (<c>!Store.IsDisposed</c>)
/// was check-then-act: thread 2's <c>Store.Dispose()</c> landing between the check and the
/// <c>Store.OnCompleted()</c> call turned the benign completion into
/// <c>ObjectDisposedException</c>, which rode the chain back into thread 1's hub disposal
/// action and was logged as "Error during shutdown of hub sync/…" (MessageHub, #1170) and
/// "Hub sync/… disposal faulted" (HostedHubsCollection, #1171).</para>
///
/// <para><b>The fix</b>: correct terminal state instead of a wider guard. <c>Dispose()</c>
/// completes the store and never disposes it. A COMPLETED subject ignores any further
/// OnNext/OnError/OnCompleted per the Rx grammar, so an in-flight teardown delivery lands as a
/// no-op no matter how the threads interleave — there is no window left to guard.</para>
/// </summary>
public class SyncStreamDisposalCompletionTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Empty;

    /// <summary>
    /// Exposes the two constituent operations of the pre-fix <c>OnCompleted()</c> — the guard
    /// check and the subject call — so the test can interleave <c>Dispose()</c> BETWEEN them,
    /// rendering the production thread interleaving deterministically.
    /// </summary>
    private sealed record ProbeStream : SynchronizationStream<Empty>
    {
        public ProbeStream(IMessageHub host) : base(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("X", "Y"),
            new ReduceManager<Empty>(host),
            null)
        { }

        /// <summary>The delivering thread's guard check (the pre-fix <c>!Store.IsDisposed</c>):
        /// is the completion delivery approved?</summary>
        public bool CompletionDeliveryApproved => !Store.IsDisposed;

        /// <summary>The delivery the guard approved — what the Rx chain executes next.</summary>
        public void DeliverApprovedCompletion() => Store.OnCompleted();
    }

    /// <summary>
    /// The production race, rendered deterministically: the upstream teardown cascade
    /// (thread 1) has already passed the OnCompleted guard when this stream's own disposal
    /// (thread 2) runs; the approved delivery then executes. RED before the fix
    /// (<c>Store.Dispose()</c> in <c>Dispose()</c> made the delivery throw
    /// ObjectDisposedException); GREEN after (the completed-not-disposed store ignores it).
    /// </summary>
    [HubFact]
    public void CompletionDelivery_InterleavedWithDisposal_IsANoOp_NeverThrows()
    {
        var stream = new ProbeStream(GetHost());

        // Thread 1 (a sibling stream's completion draining through the Synchronize chain)
        // evaluates the guard — the stream is alive, the delivery is approved:
        stream.CompletionDeliveryApproved.Should().BeTrue("the stream has not been disposed yet");

        // Thread 2 (this stream's own hub disposal action, a different action block) disposes
        // exactly inside the check-then-act window:
        stream.Dispose();

        // Thread 1 proceeds with the delivery it already approved:
        var act = stream.DeliverApprovedCompletion;
        act.Should().NotThrow(
            "a terminal notification draining out of a concurrently-disposing upstream chain is a "
            + "recognized shutdown outcome — the disposed stream's store must be COMPLETED (which "
            + "ignores it, Rx grammar), never DISPOSED (which throws the ObjectDisposedException "
            + "logged as 'Error during shutdown of hub sync/…' in #1170/#1171)");
    }

    /// <summary>
    /// Hub-disposal shape of the same contract, plus its behavioral consequence: disposing the
    /// HOST hub (which disposes the stream via its registered disposal action, as every
    /// production creator wires it) completes active subscribers gracefully — and a LATE
    /// subscriber still observes completion instead of silence. RED before the fix: the disposed
    /// store made <c>Subscribe</c> throw ObjectDisposedException, which was swallowed into a
    /// no-op subscription — the late subscriber never heard anything, forever.
    /// </summary>
    [HubFact]
    public async Task HubDisposal_CompletesActiveSubscribers_AndLateSubscribersStillSeeCompletion()
    {
        var host = GetHost();
        var stream = new SynchronizationStream<Empty>(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("X", "Y"),
            new ReduceManager<Empty>(host),
            null);
        host.RegisterForDisposal(stream);

        var activeSubscriberCompleted = false;
        stream.Subscribe(_ => { }, _ => { }, () => activeSubscriberCompleted = true);

        host.Dispose();
        await host.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .Timeout(10.Seconds())
            .ToTask(TestContext.Current.CancellationToken);

        activeSubscriberCompleted.Should().BeTrue(
            "disposing the hub disposes the stream, which must complete its active subscribers");

        var lateSubscriberCompleted = false;
        stream.Subscribe(_ => { }, _ => { }, () => lateSubscriberCompleted = true);
        lateSubscriberCompleted.Should().BeTrue(
            "a subscriber attaching AFTER disposal must observe graceful completion immediately — "
            + "a DISPOSED store instead threw ObjectDisposedException out of Subscribe, which was "
            + "swallowed into a no-op subscription: the subscriber heard nothing, forever");
    }
}
