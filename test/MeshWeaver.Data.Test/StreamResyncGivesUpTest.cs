using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A RESYNC THAT NEVER CONVERGES MUST STOP BEING SILENT — Systemorph/MeshWeaver#1384.
///
/// <para>This is the third member of the frame-loss family, and it starts where the other two stop.
/// <see cref="StreamFrameLossResyncTest"/> proves the mirror DETECTS a lost frame and asks for a
/// fresh snapshot. <see cref="StreamResyncConvergenceTest"/> proves it still converges when that ONE
/// answer fails — the resync gate is released by the re-ask's round trip (#2654), so the next frame
/// that proves the gap earns one new re-ask. Neither of them TERMINATES when the owner→subscriber
/// leg keeps eating this stream's snapshots, and that is the state a live portal was measured
/// in.</para>
///
/// <para><b>The live incident.</b> memex-cloud, 2026-09-01, node
/// <c>Event/SavGeneralversammlung2026/Talk</c>: <c>[SYNC_STREAM] Frame loss detected … incoming
/// Patch v13 chains onto v12 but the last applied frame is v11</c>, then <c>Layout area 'Present' …
/// was torn down having never rendered — the subscriber only ever saw the "awaiting first data"
/// placeholder</c>. Plain node reads on the same path answered instantly and recycling the pod that
/// held the activation did not clear it: the owner was healthy, and the wedge lived entirely in a
/// subscriber nobody had told anything was wrong. Every re-ask was acknowledged, every
/// acknowledgement released the gate, every answering Full died on the same leg, and the mirror
/// asked again for the rest of the process's life — at Warning in a log, and at NOTHING AT ALL in
/// the API a consumer can act on.</para>
///
/// <para><b>What this test injects, and why it is deterministic.</b> Two losses on the owner's post
/// pipeline, both scoped to the one mirror stream under test: one mid-burst Patch (the wire loss
/// that starts the resync — the same injection the other two tests use), and then EVERY
/// <see cref="ChangeType.Full"/> the owner sends on that stream, which is what a leg that keeps
/// losing frames looks like from the mirror. The test posts the next write only after the loss it
/// depends on has actually happened — an event, never a delay — so the sequence of proven gaps is
/// fixed rather than raced.</para>
///
/// <para><b>The contract.</b> Past <c>MaxUnansweredResyncs</c> ACKNOWLEDGED, unanswered re-asks the
/// mirror stops asking and FAULTS. That is not a retry budget: an increment costs a full round trip
/// to the owner plus a subsequent frame proving the mirror still has no base, so the count is
/// evidence, not effort. And the fault is not the end of the story — it is the only signal a
/// subscriber can act on. <c>StreamLiveness.IsUsable</c> refuses to serve a faulted stream (#2387),
/// so the workspace cache evicts it and the next natural caller opens a fresh one that subscribes
/// from scratch; the second half of this test proves exactly that.</para>
/// </summary>
public class StreamResyncGivesUpTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// How many acknowledged-but-unanswered fresh-snapshot requests the mirror tolerates before it
    /// faults. Mirrors <c>SynchronizationStream.MaxUnansweredResyncs</c>, which is private — the
    /// coupling is deliberate and asserted by construction: the drive loop below spends exactly this
    /// many attempts and then expects the next proven gap to fault. If the production bound moves
    /// and this does not, the loop under-drives and the fault wait goes red, which is the right way
    /// round for a guard.
    /// </summary>
    private const int MaxUnansweredResyncs = 3;

    /// <summary>1 after the pipeline ate its one mid-burst Patch. Instance field — per-test lifetime.</summary>
    private int droppedPatches;

    /// <summary>
    /// Patch frames the owner has emitted on the mirror's stream. The loss is injected on the SECOND
    /// one so the mirror has applied a real patch first — a gap can only be PROVEN against a frame
    /// that landed.
    /// </summary>
    private int patchFramesSeen;

    /// <summary>
    /// How many fresh snapshots the pipeline has eaten. Every one of them is a re-ask the owner
    /// answered and the leg destroyed.
    /// </summary>
    private int droppedFreshSnapshots;

    /// <summary>
    /// Fires once per eaten fresh snapshot, carrying the running count. A
    /// <see cref="ReplaySubject{T}"/> because the injection runs on the owner's post pipeline and
    /// can complete before the test subscribes.
    /// </summary>
    private readonly ReplaySubject<int> freshSnapshotDropped = new(MaxUnansweredResyncs + 2);

    /// <summary>
    /// The stream carrying the mirror this test is about, learned from its INITIAL Full. Both hubs
    /// run a data source, so more than one sync stream is alive; every injection is scoped to this
    /// id so the test can never eat a frame belonging to somebody else's stream — including the
    /// FRESH stream the recovery half opens, whose id is different by construction.
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

                // THE WIRE LOSS that starts the resync: eat exactly one mid-burst Patch. An Ignored
                // post is dropped by ScheduleNotify, exactly the shape of the at-most-once transport
                // losing a published frame between owner and mirror.
                if (evt.ChangeType == ChangeType.Patch
                    && Interlocked.Increment(ref patchFramesSeen) == 2
                    && Interlocked.Exchange(ref droppedPatches, 1) == 0)
                    return d.Ignored();

                // …AND THE LEG KEEPS LOSING. Every fresh snapshot the owner sends in answer to a
                // re-ask dies exactly like the frame that started the resync. This is the ONE thing
                // this test adds to StreamResyncConvergenceTest, which eats a single answer and then
                // lets the mirror converge.
                if (evt.ChangeType == ChangeType.Full)
                {
                    // Signalling HERE is ORDERED, not hopeful: the owner posts its SubscribeAck from
                    // HandleSubscribeRequest the moment SubscribeToClient returns, while the
                    // re-assert runs later on the stream's own turn — so by the time this frame
                    // exists the ack is already on its way to the mirror's host hub, ahead of
                    // anything the test posts next. The write below therefore lands on an OPEN gate
                    // with still no base: the frame that earns the next re-ask.
                    freshSnapshotDropped.OnNext(Interlocked.Increment(ref droppedFreshSnapshots));
                    return d.Ignored();
                }
                return next.Invoke(d);
            }));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))));

    /// <summary>
    /// THE WEDGE BECOMES AN ERROR, AND THE ERROR BECOMES A RECOVERY.
    ///
    /// <para>Pre-fix this test hangs on its fault wait: the mirror re-asks forever, the log fills
    /// with "Resync has not converged", and the subscriber is handed neither a value nor a terminal
    /// — which is precisely why the production incident survived a pod recycle.</para>
    /// </summary>
    [HubFact]
    public async Task AResyncThatNeverConverges_FaultsTheMirror_AndAFreshSubscriberRecovers()
    {
        var host = GetHost();
        var client = GetClient();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var clientWorkspace = client.GetWorkspace();
        var clientStream = clientWorkspace
            .GetRemoteStream<EntityStore>(CreateHostAddress(), new CollectionsReference(collectionName));

        // The mirror is live and mirrorStreamId is known — every injection below is scoped to it.
        await clientStream.Should().Within(10.Seconds()).Emit();

        // Arm the terminal wait BEFORE driving. A store is a ReplaySubject, so subscribing after the
        // fault would still replay it; arming first means the assertion cannot be satisfied by
        // anything that happened before the drive, and it makes the pre-fix failure a TIMEOUT on the
        // thing that never happens rather than a race with the drive loop.
        var faulted = clientStream
            .Materialize()
            .Where(n => n.Kind == NotificationKind.OnError)
            .Select(n => n.Exception!)
            .Take(1)
            .Replay(1);
        using var faultedConnection = faulted.Connect();

        // A burst whose SECOND patch the wire eats — the third patch chains onto a version the
        // mirror never applied, which is proven gap #1 and earns re-ask #1.
        for (var i = 1; i <= 3; i++)
            host.Post(
                new DataChangeRequest().WithUpdates(new MyData($"doc-{i}", $"value-{i}")),
                o => o.WithAccessContext(accessService.Context!));

        // Then one write per further proven gap. Each iteration waits for the answer to the PREVIOUS
        // re-ask to have been destroyed — the event, never a delay — so the next write is guaranteed
        // to be a frame the mirror sees while it still holds no base.
        for (var attempt = 1; attempt <= MaxUnansweredResyncs; attempt++)
        {
            var expected = attempt;
            await freshSnapshotDropped
                .Where(count => count >= expected)
                .Take(1)
                .Should().Within(15.Seconds())
                .Emit($"the owner must have ANSWERED re-ask {expected} with a fresh snapshot for the "
                    + "leg to destroy — an unanswered re-ask would mean this test is measuring a "
                    + "refusal, which StreamResyncConvergenceTest already covers");

            host.Post(
                new DataChangeRequest().WithUpdates(new MyData($"doc-{3 + attempt}", $"value-{3 + attempt}")),
                o => o.WithAccessContext(accessService.Context!));
        }

        // THE ASSERTION. The gap proven by that last write must fault the mirror instead of asking a
        // fourth time into a leg that has now destroyed three acknowledged snapshots.
        var error = await faulted
            .Should().Within(20.Seconds())
            .Emit("a mirror whose fresh snapshots keep being lost must SURFACE that, not re-ask "
                + "forever: a subscriber that is never told cannot re-establish, which is how one "
                + "lost frame became a layout area that never rendered");

        error.Should().BeOfType<StreamNotConvergingException>(
            "the fault must name the non-convergence rather than arrive as some incidental "
            + "exception — a consumer classifies on the type");

        Volatile.Read(ref droppedPatches).Should().Be(1,
            "the wire loss is what starts the resync — without it there is no re-ask to leave "
            + "unanswered and this test asserts nothing");
        Volatile.Read(ref droppedFreshSnapshots).Should().BeGreaterThanOrEqualTo(MaxUnansweredResyncs,
            "every re-ask must have been ANSWERED and the answer destroyed — that is the shape this "
            + "test exists for, as opposed to an owner that simply went quiet or refused");

        // AND THE RECOVERY. The faulted stream is unusable, so the workspace cache must not hand it
        // back: a fresh subscriber gets a NEW stream (a new StreamId, therefore untouched by the
        // injections above) which subscribes from scratch and converges on the owner's state. This
        // is the whole point of faulting rather than waiting.
        var recovered = clientWorkspace
            .GetRemoteStream<EntityStore>(CreateHostAddress(), new CollectionsReference(collectionName));

        ReferenceEquals(recovered, clientStream).Should().BeFalse(
            "a faulted mirror must be evicted from the stream cache, not replayed to the next caller");

        await recovered
            .Where(ci => ci.Value?.Collections.GetValueOrDefault(collectionName) is { } coll
                && coll.Instances.Values.OfType<MyData>()
                    .Count(dd => dd.Text?.StartsWith("value-") == true) == 3 + MaxUnansweredResyncs)
            .Take(1)
            .Should().Within(20.Seconds())
            .Emit("the subscriber that learned of the failure must be able to re-establish and see "
                + "the owner's complete state — surfacing the wedge is only useful if recovery "
                + "follows from it");
    }
}
