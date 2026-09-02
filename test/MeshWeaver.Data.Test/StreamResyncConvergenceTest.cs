using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// THE RESYNC MUST CONVERGE — Systemorph/MeshWeaver#2654.
///
/// <para><see cref="StreamFrameLossResyncTest"/> proves the mirror DETECTS a lost frame and asks for
/// a fresh snapshot. This class proves what happens when that ask does not work out, which is the
/// half that was missing: the re-ask travels the very leg that just demonstrated it loses frames,
/// and <c>SynchronizationStream.RequestFreshSnapshot</c> posted it FIRE-AND-FORGET — no NACK arm,
/// no <c>OnError</c>, no terminal of any kind — behind a gate (<c>resyncInFlight</c>) whose only
/// release was a <see cref="ChangeType.Full"/> landing.</para>
///
/// <para>So every way the answer could fail to arrive latched the mirror SHUT for the rest of its
/// life. Each later Patch took the "Patch before base Full" branch, <c>RequestFreshSnapshot</c>
/// no-opped on the gate, and the frame was dropped at Debug: no error, no warning, no recovery —
/// a layout area on its placeholder forever while the breadcrumb, banner and menus around it
/// rendered fine. That silence is why 112 <c>Frame loss detected</c> events across 40 streams
/// produced a test timeout rather than a failure.</para>
///
/// <para>Three ways the answer fails, one test each — and each one is a REAL, deliverable verdict of
/// the layers involved, injected with the same deterministic wire-loss pipeline
/// <see cref="StreamFrameLossResyncTest"/> uses:</para>
/// <list type="number">
/// <item>the fresh snapshot is lost in transport, exactly like the frame that started the resync;</item>
/// <item>the fresh snapshot arrives but carries a RESET frame clock (the owner had to rebuild the
/// server-side stream), so the mirror's own monotonicity guard throws it away;</item>
/// <item>the re-ask is refused terminally, with the router's "nothing serves that address".</item>
/// </list>
/// </summary>
public class StreamResyncConvergenceTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>1 after the pipeline ate its one mid-burst Patch. Instance field — per-test lifetime.</summary>
    private int droppedPatches;

    /// <summary>
    /// Patch frames the owner has emitted on the mirror's stream. The loss is injected on the
    /// SECOND one, deliberately: the first must LAND so the mirror's <c>Current</c> advances past
    /// the base Full's version. Without that, the mirror sits at v0, nothing can be stamped below
    /// it, and the rebased-clock test asserts nothing.
    /// </summary>
    private int patchFramesSeen;

    /// <summary>1 after the pipeline ate the fresh snapshot answering the resync.</summary>
    private int droppedFreshSnapshots;

    /// <summary>1 after the pipeline rebased the fresh snapshot's frame clock.</summary>
    private int rebasedFreshSnapshots;

    /// <summary>1 after the pipeline refused the resync's <see cref="SubscribeRequest"/>.</summary>
    private int refusedResyncRequests;

    /// <summary>
    /// Fires when the injected failure has actually happened, so the test can post the NEXT write
    /// without racing it. A <see cref="ReplaySubject{T}"/> because the injection runs on the hub's
    /// post pipeline and can complete before the test subscribes.
    /// </summary>
    private readonly ReplaySubject<Unit> failureInjected = new(1);

    /// <summary>
    /// The stream carrying the mirror this test is about, learned from its INITIAL Full. Both hubs
    /// run a data source here, so more than one sync stream is alive; every injection below is
    /// scoped to this id so the test can never eat a frame of somebody else's stream.
    /// </summary>
    private volatile string? mirrorStreamId;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))))
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
            {
                if (d.Message is not DataChangedEvent evt)
                    return next.Invoke(d);

                // Learn the mirror's stream from its initial Full — the first frame it ever gets.
                if (evt.ChangeType == ChangeType.Full && mirrorStreamId is null)
                {
                    mirrorStreamId = evt.StreamId;
                    return next.Invoke(d);
                }
                if (evt.StreamId != mirrorStreamId)
                    return next.Invoke(d);

                // THE WIRE LOSS that starts the resync: eat exactly one mid-burst Patch — an
                // Ignored post is dropped by ScheduleNotify, exactly the shape of the at-most-once
                // transport losing a published frame between owner and mirror. The SECOND one, so
                // the mirror has applied a real patch first (see patchFramesSeen).
                if (evt.ChangeType == ChangeType.Patch
                    && Interlocked.Increment(ref patchFramesSeen) == 2
                    && Interlocked.Exchange(ref droppedPatches, 1) == 0)
                    return d.Ignored();

                // …and then THE ANSWER FAILS. Only one of the two arms below is armed per test.
                if (evt.ChangeType == ChangeType.Full)
                {
                    if (eatTheFreshSnapshot
                        && Interlocked.Exchange(ref droppedFreshSnapshots, 1) == 0)
                    {
                        // Signalling HERE is ORDERED, not hopeful — and since #3058 the ordering
                        // runs the other way round. The owner posts its SubscribeAck from the
                        // re-assert's own update turn, immediately AFTER this frame was handed to
                        // hub.Post (CreateSynchronizationStream's Update(…, applied:)), so the ack
                        // is one enqueue behind the frame this pipeline is eating and is already on
                        // its way — far ahead of the write the test makes next, which cannot be
                        // posted until this signal has travelled back out to the test thread. That
                        // write therefore lands on an OPEN gate with still no base: the frame that
                        // has to earn a second re-ask.
                        failureInjected.OnNext(Unit.Default);
                        return d.Ignored();
                    }
                    if (rebaseTheFreshSnapshot
                        && Interlocked.Exchange(ref rebasedFreshSnapshots, 1) == 0)
                    {
                        // A RESET frame clock, which is what the owner really produces when it has
                        // to REBUILD the server-side stream to answer the re-ask (the subscriber
                        // was evicted on the router's TargetUnserved verdict #2620, or the owner
                        // grain recycled): the fresh stream's version counter starts from scratch,
                        // so its first Full is stamped BELOW what the mirror already holds.
                        var rebased = next.Invoke(d.WithMessage(evt with { Version = 1L }));
                        failureInjected.OnNext(Unit.Default);
                        return rebased;
                    }
                }
                return next.Invoke(d);
            }));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))))
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
            {
                // THE REFUSED RE-ASK. The initial subscribe goes through (mirrorStreamId is still
                // unknown when it is posted); the resync's re-ask carries the SAME stream id and is
                // answered with the router's TERMINAL "no hub anywhere serves that address" — the
                // exact verdict RoutingGrain.RefuseNoSubscriber produces for a stream-routed
                // delivery the cluster-wide subscription registry says nobody is serving
                // (#2426/#2546): ErrorType.NotFound carrying the authoritative TargetUnserved stamp.
                //
                // 🚨 NotFound, not ShuttingDown, and the difference is the whole classification:
                // the router stamps TargetUnserved on BOTH of its verdicts, and its transient one
                // (AnswerPodHubNotHere, #2745 — a silo whose pod-hub claim has not landed during a
                // rolling deploy) carries ErrorType.ShuttingDown precisely so a sync stream RIDES IT
                // OUT. Keying the tear-down on the stamp would fault every mirror in that overlap
                // window, so the stream keys on the ErrorType, exactly as its own DeliveryFailure
                // handler always has.
                if (refuseTheResyncRequestAs is { } verdict
                    && d.Message is SubscribeRequest sr
                    && sr.StreamId == mirrorStreamId
                    && Interlocked.Exchange(ref refusedResyncRequests, 1) == 0)
                {
                    client!.Post(
                        new DeliveryFailure(d)
                        {
                            ErrorType = verdict,
                            // The stamp is the SAME on both verdicts, on purpose — it is the
                            // owner-side eviction gate, not a sender-side classification.
                            TargetUnserved = true,
                            Message = "no live subscriber for the owner address",
                        },
                        o => o.ResponseFor(d));
                    failureInjected.OnNext(Unit.Default);
                    return d.Ignored();
                }
                return next.Invoke(d);
            }));

    /// <summary>Which failure this test arms; set before the mirror is opened.</summary>
    private volatile bool eatTheFreshSnapshot;
    private volatile bool rebaseTheFreshSnapshot;

    /// <summary>
    /// The verdict the resync's re-ask is refused with, or <c>null</c> to let it through. Both
    /// values the router really produces are exercised, and they must be treated DIFFERENTLY
    /// despite carrying the identical <see cref="DeliveryFailure.TargetUnserved"/> stamp.
    /// </summary>
    private ErrorType? refuseTheResyncRequestAs;

    /// <summary>The client hub, so the refusal arm can answer its own outgoing request.</summary>
    private volatile IMessageHub? client;

    /// <summary>
    /// THE FRESH SNAPSHOT IS LOST TOO — the plain case of #2654, and the one the production log
    /// shows: a leg that drops a frame drops the answer to the re-ask just as readily.
    ///
    /// <para>🚨 The frame chain CANNOT catch this, which is why the recovery had to come from the
    /// request's round trip instead. <c>BuildReassertFrame</c> stamps the re-assert with the version
    /// of the STATE it re-asserts (#945), not a new one, so the Full shares a version with the frame
    /// before it and the chain reads <c>v4 → Full v4 → v5(basedOn 4)</c> exactly like
    /// <c>v4 → v5(basedOn 4)</c>. An earlier draft of this fix watched the seen-chain through the
    /// resync window and this test is what proved it blind.</para>
    ///
    /// <para>What DOES recover it: the owner ACKs the re-subscribe, which releases the gate, and the
    /// next write's Patch then finds the mirror still without a base and earns exactly one new
    /// re-ask — an event, never a timer. Pre-fix the gate could only be released by a Full, so the
    /// Full's loss shut it permanently: every later Patch was swallowed at Debug and the mirror
    /// never converged.</para>
    /// </summary>
    [HubFact]
    public async Task AFreshSnapshotLostInTransport_StillConverges()
    {
        eatTheFreshSnapshot = true;
        await RunConvergenceScenario();
        Volatile.Read(ref droppedFreshSnapshots).Should().Be(1,
            "the pipeline must have eaten the fresh snapshot answering the resync — without that "
            + "loss the mirror converges on the first re-ask and this test proves nothing");
    }

    /// <summary>
    /// THE FRESH SNAPSHOT ARRIVES ON A RESET CLOCK. When the owner cannot match the re-ask to a
    /// live server-side stream it BUILDS one, and that stream's frame counter starts from scratch —
    /// so the snapshot the mirror asked for is stamped below the version it already holds and the
    /// mirror's own monotonicity guard drops it. The guard is right in general (it exists to stop a
    /// stale point-in-time Full overwriting newer state) and wrong here: <c>RequestFreshSnapshot</c>
    /// has already DISCARDED the local snapshot, so there is nothing newer to protect — the mirror
    /// cannot apply anything at all until a Full lands. Pre-fix the guard threw the answer away and
    /// the gate stayed shut forever.
    /// </summary>
    [HubFact]
    public async Task AFreshSnapshotOnARebasedClock_IsAcceptedAndConverges()
    {
        rebaseTheFreshSnapshot = true;
        await RunConvergenceScenario();
        Volatile.Read(ref rebasedFreshSnapshots).Should().Be(1,
            "the pipeline must have rebased the fresh snapshot's frame clock — without that the "
            + "monotonicity guard is never exercised and this test proves nothing");
    }

    /// <summary>
    /// THE RE-ASK IS REFUSED TERMINALLY. The router asked the cluster-wide subscription registry and
    /// was told nobody serves the owner address (<c>RoutingGrain.RefuseNoSubscriber</c>,
    /// #2426/#2546) — the mirror is never getting its snapshot. Pre-fix nothing was listening: the
    /// re-ask was posted fire-and-forget, so the verdict resolved no callback, reached no arm and
    /// was simply dropped, and the mirror sat silent behind a gate that could no longer open. A
    /// subscriber must SEE that failure instead of holding a placeholder for a snapshot that is
    /// never coming.
    /// </summary>
    [HubFact]
    public async Task ARefusedReAsk_FaultsTheMirrorInsteadOfLatchingItSilently()
    {
        refuseTheResyncRequestAs = ErrorType.NotFound;
        var host = GetHost();
        client = GetClient();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var clientStream = client.GetWorkspace()
            .GetRemoteStream<EntityStore>(CreateHostAddress(), new CollectionsReference(collectionName));

        await clientStream.Should().Within(8.Seconds()).Emit();

        for (var i = 1; i <= 3; i++)
            host.Post(
                new DataChangeRequest().WithUpdates(new MyData($"doc-{i}", $"value-{i}")),
                o => o.WithAccessContext(accessService.Context!));

        // The mirror must FAULT. Materialize turns the terminal OnError into a value, so the wait
        // has a positive signal to hold; pre-fix nothing terminal ever reaches the subscriber and
        // this times out — the silent latch, made loud.
        await clientStream
            .Materialize()
            .Where(n => n.Kind == System.Reactive.NotificationKind.OnError)
            .Take(1)
            .Should().Within(15.Seconds())
            .Emit("a re-ask the router refused because nothing serves the owner is terminal: the "
                + "mirror must surface the failure, not hold a gate that can no longer open");

        Volatile.Read(ref droppedPatches).Should().Be(1,
            "the wire loss is what triggers the resync — without it there is no re-ask to refuse");
        Volatile.Read(ref refusedResyncRequests).Should().Be(1,
            "the resync's SubscribeRequest must have been refused — otherwise this test asserts nothing");
    }

    /// <summary>
    /// THE RE-ASK IS REFUSED **TRANSIENTLY** — and must NOT fault the mirror.
    ///
    /// <para>The router stamps <see cref="DeliveryFailure.TargetUnserved"/> on BOTH of its "nobody
    /// serves that address" verdicts. The one this test injects is
    /// <c>RoutingGrain.AnswerPodHubNotHere</c> (#2745): a silo-hosted owner whose pod-hub claim has
    /// not landed yet — the ordinary overlap window of a rolling deploy — answered
    /// <see cref="ErrorType.ShuttingDown"/> precisely so that consumers with recovery machinery ride
    /// it out. Classifying on the STAMP instead of the ErrorType would fault every mirror in that
    /// window, which is a worse outcome than the bug this ticket fixes.</para>
    ///
    /// <para>So: keep the stream, re-open the gate, and let the next frame that proves the mirror
    /// still has no base earn one new re-ask. Convergence IS the assertion — a faulted stream
    /// replays its error to every later subscriber, so the wait below would report the fault rather
    /// than time out.</para>
    /// </summary>
    [HubFact]
    public async Task ATransientlyRefusedReAsk_IsRiddenOutAndStillConverges()
    {
        refuseTheResyncRequestAs = ErrorType.ShuttingDown;
        await RunConvergenceScenario();
        Volatile.Read(ref refusedResyncRequests).Should().Be(1,
            "the resync's SubscribeRequest must have been refused transiently — without the refusal "
            + "this test does not distinguish riding it out from never being refused at all");
    }

    /// <summary>
    /// Drives the shared shape: a live mirror, a burst whose first Patch the wire eats, the armed
    /// failure of the resync's answer, and then ONE more write — the frame that proves the mirror
    /// is still without a base and must earn exactly one new re-ask. Convergence to all four
    /// documents is the assertion; pre-fix it never happens.
    /// </summary>
    private async Task RunConvergenceScenario()
    {
        var host = GetHost();
        client = GetClient();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var clientStream = client.GetWorkspace()
            .GetRemoteStream<EntityStore>(CreateHostAddress(), new CollectionsReference(collectionName));

        // Base Full applied — the mirror is live, and mirrorStreamId is now known.
        await clientStream.Should().Within(8.Seconds()).Emit();

        for (var i = 1; i <= 3; i++)
            host.Post(
                new DataChangeRequest().WithUpdates(new MyData($"doc-{i}", $"value-{i}")),
                o => o.WithAccessContext(accessService.Context!));

        // Wait for the injected failure itself — the event, never a delay — so the write below is
        // guaranteed to be a frame the owner sends AFTER the resync's answer went wrong.
        await failureInjected.Take(1).Should().Within(8.Seconds())
            .Emit("the resync's answer must actually have been broken before the next write");

        host.Post(
            new DataChangeRequest().WithUpdates(new MyData("doc-4", "value-4")),
            o => o.WithAccessContext(accessService.Context!));

        await clientStream
            .Where(ci => ci.Value?.Collections.GetValueOrDefault(collectionName) is { } coll
                && coll.Instances.Values.OfType<MyData>()
                    .Count(d => d.Text?.StartsWith("value-") == true) == 4)
            .Take(1)
            .Should().Within(10.Seconds())
            .Emit("a resync whose answer failed must still converge: the next frame proves the "
                + "mirror has no base, and that proof — not a timer — earns one new re-ask");

        Volatile.Read(ref droppedPatches).Should().Be(1,
            "the delivery pipeline must have dropped exactly one mid-burst Patch frame — without "
            + "the loss there is no resync and this test would assert nothing");
    }
}
