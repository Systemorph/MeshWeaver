using System;
using System.Reactive.Linq;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A FAULT IS DEAD THE MOMENT IT IS DELIVERED — Systemorph/MeshWeaver#4180 / #4244.
///
/// <para><b>The contract this class pins, in one sentence:</b> from the instant a subscriber can
/// SEE the terminal, every cache must already refuse to serve that stream, and every reader that
/// finds it refused must still be able to obtain the terminal. Those are two directions of one
/// fact, and until #4180 the code could only satisfy one of them at a time.</para>
///
/// <para><b>How it got that way.</b> <c>FaultStore</c> raised a liveness FLAG and errored the
/// store, and the two orderings each opened a window:</para>
/// <list type="bullet">
/// <item><description><b>Flag first</b> (before MeshWeaver#4151) — a reader arriving between the
/// flag and the store found the stream "dead" with an OPEN store, was answered <i>completed</i>,
/// and the <c>OnError</c> that landed a moment later reached nobody (Plugins#1715: a layout area
/// whose node was gone rendered NOTHING where its card belongs, because the view's error branch
/// was never entered).</description></item>
/// <item><description><b>Store first</b> (MeshWeaver#4151, 72c2ee2a56) — the flag now trailed the
/// terminal, so the whole SYNCHRONOUS delivery ran inside the window. Every consumer reacting to
/// the fault — which is the documented recovery path, <c>StreamLiveness.IsUsable</c> refusing the
/// corpse so "the next natural caller opens a fresh one" (#2387) — was handed the corpse instead.
/// That is #4180/#4244: <c>StreamResyncGivesUpTest</c> failed with <i>"a faulted mirror must be
/// evicted from the stream cache, not replayed to the next caller, but found True"</i> on five
/// pull requests in one evening, at roughly 1 run in 46.</description></item>
/// </list>
///
/// <para><b>Why the 2% version is the same defect as the 100% version below.</b> The assertion in
/// <c>StreamResyncGivesUpTest</c> can only fail by <c>StreamLiveness.IsUsable</c> answering TRUE at
/// the cache's consult — every other route through
/// <c>Workspace.GetExternalClientSynchronizationStream</c> mints a new instance — and once the
/// terminal has been delivered the only way the flag still reads live is the store-first window. The
/// test reaches it because its assertion helper settles a <c>TaskCompletionSource</c> with
/// <c>RunContinuationsAsynchronously</c> from INSIDE that delivery: the awaiting test thread is
/// queued there and then races the producer thread's remaining few microseconds of unwinding to the
/// <c>finally</c>. The producer usually wins, which is what makes it look like a flake. The tests
/// here make the same race deterministic by reacting to the fault on the delivering thread — the
/// other production shape, and the one every <c>.Subscribe(onNext, onError)</c> consumer has.</para>
/// </summary>
public class FaultedStreamEvictionOrderingTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))));

    private string CollectionName =>
        GetHost().GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;

    /// <summary>
    /// 🚨 THE #4180 REPRODUCTION, deterministic. A consumer learns of the fault the only way a
    /// consumer can — its <c>OnError</c> arm — and does the one thing the fault exists to make
    /// possible: ask the workspace for the stream again. It must get a NEW one.
    ///
    /// <para>Against the store-before-flag ordering this fails on every run with the same words
    /// <c>StreamResyncGivesUpTest</c> failed with under load, because the arm runs inside
    /// <c>Store.OnError</c> and the flag is not written until that call returns.</para>
    /// </summary>
    [HubFact]
    public async Task AConsumerReactingToTheFault_IsHandedAFreshStream_NotTheCorpse()
    {
        var clientWorkspace = GetClient().GetWorkspace();
        var collectionName = CollectionName;
        var reference = new CollectionsReference(collectionName);
        var clientStream = clientWorkspace.GetRemoteStream<EntityStore>(CreateHostAddress(), reference);

        await clientStream.Should().Within(10.Seconds()).Emit(
            "precondition: the mirror is live before it is faulted");

        bool? usableInsideTheDelivery = null;
        ISynchronizationStream<EntityStore>? reResolved = null;
        using var consumer = clientStream.Subscribe(
            _ => { },
            _ =>
            {
                usableInsideTheDelivery = clientStream.IsUsable();
                reResolved = clientWorkspace.GetRemoteStream<EntityStore>(
                    CreateHostAddress(), new CollectionsReference(collectionName));
            });

        clientStream.OnError(new StreamNotConvergingException(
            "the owner acknowledged every re-ask and answered none — the #1384 shape"));

        usableInsideTheDelivery.Should().BeFalse(
            "a stream whose terminal a subscriber is RECEIVING is already dead — the liveness flag "
            + "that every cache reads must not trail the notification it describes");
        (reResolved is null).Should().BeFalse(
            "the consumer's error arm must have run — without it this test asserts nothing");
        ReferenceEquals(reResolved, clientStream).Should().BeFalse(
            "a faulted mirror must be evicted from the stream cache, not replayed to the next "
            + "caller: re-resolving is the ONLY recovery a subscriber has (#2387), and handing it "
            + "the corpse turns one transient failure into a permanent one");
    }

    /// <summary>
    /// 🚨 THE OTHER DIRECTION, which is why #4151 chose the ordering it chose and why the fix is
    /// NOT simply to swap it back. A reader that finds the stream refused must still be able to say
    /// HOW it ended — otherwise Plugins#1715 returns: a completion where a fault belongs, and a view
    /// with no error branch to enter.
    ///
    /// <para>The terminal is therefore RECORDED, not inferred from the store's state: it is
    /// published in the same write that makes the stream read as dead, so "refused" and "here is the
    /// fault" can never disagree in either direction. Read from inside the delivery, where the two
    /// used to contradict each other.</para>
    /// </summary>
    [HubFact]
    public async Task TheRefusalAndTheTerminal_AreOneFact_ReadableAtTheSameInstant()
    {
        var clientWorkspace = GetClient().GetWorkspace();
        var clientStream = clientWorkspace.GetRemoteStream<EntityStore>(
            CreateHostAddress(), new CollectionsReference(CollectionName));

        await clientStream.Should().Within(10.Seconds()).Emit(
            "precondition: the mirror is live before it is faulted");

        var boom = new StreamNotConvergingException("owner never converged");
        Exception? recordedInsideTheDelivery = null;
        bool? usableInsideTheDelivery = null;
        using var consumer = clientStream.Subscribe(
            _ => { },
            _ =>
            {
                usableInsideTheDelivery = clientStream.IsUsable();
                recordedInsideTheDelivery = clientStream.TerminalFault();
            });

        clientStream.OnError(boom);

        usableInsideTheDelivery.Should().BeFalse("the stream is dead from the delivery onwards");
        recordedInsideTheDelivery.Should().BeSameAs(boom,
            "…and at that same instant the terminal is already readable — a reader that is refused "
            + "the stream can forward the stream's OWN fault instead of manufacturing a completion "
            + "(Plugins#1715), which is what makes refusing it safe");

        clientStream.TerminalFault().Should().BeSameAs(boom,
            "the record outlives the delivery: a LATE reader gets the same answer");
        clientStream.IsUsable().Should().BeFalse("…and the same refusal");
    }

    /// <summary>
    /// A fault is FOREVER and it is the FIRST one: the store keeps the first terminal under the Rx
    /// grammar, so the record must too, or a reader forwarding the record would report a different
    /// exception than a subscriber replaying the store.
    /// </summary>
    [HubFact]
    public async Task TheRecordedTerminal_IsTheFirstOne_JustLikeTheStore()
    {
        var clientWorkspace = GetClient().GetWorkspace();
        var clientStream = clientWorkspace.GetRemoteStream<EntityStore>(
            CreateHostAddress(), new CollectionsReference(CollectionName));

        await clientStream.Should().Within(10.Seconds()).Emit(
            "precondition: the mirror is live before it is faulted");

        var first = new StreamNotConvergingException("first");
        clientStream.OnError(first);
        clientStream.OnError(new InvalidOperationException("second"));

        clientStream.TerminalFault().Should().BeSameAs(first,
            "a ReplaySubject keeps its first terminal and replays THAT to every later subscriber, "
            + "so the record a refused reader forwards must be the same instance");
    }
}
