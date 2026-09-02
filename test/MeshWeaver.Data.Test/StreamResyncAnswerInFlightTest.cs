using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A GIVE-UP COUNTER MUST COUNT OUTSTANDING FAILURES, NEVER ATTEMPTS — Systemorph/MeshWeaver#3058.
///
/// <para><see cref="StreamResyncGivesUpTest"/> proves the mirror FAULTS when the owner→subscriber
/// leg keeps eating this stream's fresh snapshots (#1384). This class proves the other half, which
/// that bound got wrong the moment it shipped: a mirror whose answer is merely SLOW must not be
/// faulted, and it was — on roughly a third of all CI runs, on unmodified main.</para>
///
/// <para><b>The live shape.</b> During an ordinary bulk install the owner's re-assert is queued on
/// the stream's action block BEHIND the burst's own frames, so it takes milliseconds to come out.
/// Every one of those frames re-proves the mirror has no base and earns a new re-ask, each re-ask
/// is acknowledged in about a millisecond, and three acks landed inside 9 ms — measured,
/// <c>05:14:07.039 → .043 → .048</c> in run 33593426491 — while the first, entirely healthy Full
/// was still queued. The mirror then faulted itself mid-install with
/// <c>StreamNotConvergingException: 3 consecutive fresh-snapshot requests to owner 'FutuRe/AmountType'
/// were acknowledged and none produced a base snapshot</c>. Three asks, one outstanding request:
/// the counter measured the owner's QUEUE DEPTH and called it non-convergence.</para>
///
/// <para><b>The defect was the word "acknowledged".</b> <c>SubscribeAck</c> was posted from
/// <c>HandleSubscribeRequest</c> the instant the re-subscribe was received — before the re-assert
/// had even left the stream's action block — while the subscriber's give-up reads an ack as "the
/// owner answered and nothing followed". A promise was being counted as a result, so "acknowledged
/// and unanswered" was true of every ask the moment it was made. The owner now posts the ack from
/// the re-assert's own update turn, AFTER the answering Full is in its outbound queue, so an ack
/// with no snapshot behind it means what the exception says it means.</para>
///
/// <para><b>What this test injects.</b> The answer to a re-ask is HELD — the re-assert Full and the
/// <see cref="SubscribeAck"/> the owner posts once that frame is on its way, together, because in a
/// running mesh they leave the owner through the same queue. The burst's own patches keep flowing,
/// exactly as they do during an install. Held, not destroyed: a delayed answer is not a lost one,
/// and telling the two apart is the whole point — <see cref="StreamResyncGivesUpTest"/> owns the
/// destroyed case and still faults.</para>
///
/// <para>Pre-fix, the ack overtakes its own answer, escapes the hold, and the mirror re-asks once
/// per further frame until it faults. Post-fix the ack rides behind the frame, the resync gate
/// stays shut for exactly one outstanding re-ask, and the mirror waits — then converges the moment
/// the answer is released.</para>
/// </summary>
public class StreamResyncAnswerInFlightTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Mirrors <c>SynchronizationStream.MaxUnansweredResyncs</c>, which is private. The drive loop
    /// below spends MORE gap-proving frames than this while one answer is outstanding — that is
    /// precisely the burst the bound used to mistake for non-convergence.
    /// </summary>
    private const int MaxUnansweredResyncs = 3;

    /// <summary>1 after the pipeline ate its one mid-burst Patch — the wire loss that starts the resync.</summary>
    private int droppedPatches;

    /// <summary>
    /// Patch frames the owner has emitted on the mirror's stream. The loss is injected on the
    /// SECOND one so the mirror has applied a real patch first — a gap can only be PROVEN against a
    /// frame that landed.
    /// </summary>
    private int patchFramesSeen;

    /// <summary>Patch frames let THROUGH to the mirror after the loss — the burst that keeps re-proving the gap.</summary>
    private int patchesLetThroughAfterTheLoss;

    /// <summary>Fires per patch let through after the loss, carrying the running count.</summary>
    private readonly ReplaySubject<int> patchLetThrough = new(2 * MaxUnansweredResyncs + 4);

    /// <summary>
    /// Re-assert <see cref="ChangeType.Full"/> frames the owner has produced for the mirror — one
    /// per re-ask it decided to answer. THE discriminator: while a single answer is in flight this
    /// must stay at ONE, because a mirror with an outstanding re-ask has nothing to re-ask for.
    /// </summary>
    private int reassertFullsPosted;

    /// <summary>Fires per held answer, carrying <see cref="reassertFullsPosted"/>.</summary>
    private readonly ReplaySubject<int> answerHeld = new(2 * MaxUnansweredResyncs + 4);

    /// <summary>
    /// Acks the owner posted with no answering frame ahead of them. Pre-fix this is every re-ask's
    /// ack; post-fix it is only the initial subscribe's, which answers nothing and overtakes
    /// nothing.
    /// </summary>
    private int acksLetThrough;

    /// <summary>
    /// Acks held BECAUSE the owner had already posted the frame answering the same re-subscribe —
    /// the positive, deterministic signal that the acknowledgement no longer overtakes its answer.
    /// Zero on the pre-fix code, by construction.
    /// </summary>
    private int acksHeldBehindTheirAnswer;

    /// <summary>The held answer, in the order the owner posted it. Released FIFO, so the mirror sees the wire it would really have seen.</summary>
    private readonly ConcurrentQueue<IMessageDelivery> heldAnswer = new();

    /// <summary>Turned off before the release, so a re-delivered frame is not held a second time.</summary>
    private volatile bool holdAnswers = true;

    /// <summary>
    /// The stream carrying the mirror this test is about, learned from its INITIAL Full. Both hubs
    /// run a data source, so more than one sync stream is alive; every injection is scoped to this
    /// id so the test can never touch somebody else's stream.
    /// </summary>
    private volatile string? mirrorStreamId;

    /// <summary>The subscriber every injection is scoped to — set from the client hub before it subscribes.</summary>
    private volatile Address? subscriber;

    /// <summary>The mirror's terminal error, if it ever produced one. Must stay null.</summary>
    private volatile Exception? mirrorFault;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))))
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
            {
                if (subscriber is null || !Equals(d.Target, subscriber))
                    return next.Invoke(d);

                // ── THE ACK ARM ────────────────────────────────────────────────────────────────
                // An ack the owner posts AFTER the frame answering the same re-subscribe is part of
                // that answer and is held with it: on a real leg both leave through the same queue,
                // so a delay that hides the snapshot hides the acknowledgement too. An ack posted
                // with NO answer ahead of it is not part of one and travels — which is every
                // re-ask's ack on the pre-fix code, and is what let the mirror mistake a queued
                // answer for a lost one.
                //
                // Counting only starts once the resync does (droppedPatches == 1): the INITIAL
                // subscribe's ack answers no re-ask and must not consume a pairing slot.
                if (d.Message is SubscribeAck)
                {
                    if (Volatile.Read(ref droppedPatches) == 0)
                        return next.Invoke(d);
                    if (holdAnswers
                        && Volatile.Read(ref acksLetThrough) < Volatile.Read(ref reassertFullsPosted))
                    {
                        Interlocked.Increment(ref acksHeldBehindTheirAnswer);
                        heldAnswer.Enqueue(d);
                        return d.Ignored();
                    }
                    Interlocked.Increment(ref acksLetThrough);
                    return next.Invoke(d);
                }

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

                // ── THE ANSWER, HELD ───────────────────────────────────────────────────────────
                // Every re-assert Full is captured rather than delivered. This is the owner's
                // answer sitting in a queue — the install's own frames overtake it, which is the
                // condition the whole test exists for.
                if (evt.ChangeType == ChangeType.Full && holdAnswers)
                {
                    var held = Interlocked.Increment(ref reassertFullsPosted);
                    heldAnswer.Enqueue(d);
                    answerHeld.OnNext(held);
                    return d.Ignored();
                }

                // Patches keep flowing — that is what a bulk install looks like, and each one
                // re-proves to the mirror that it still has no base.
                if (evt.ChangeType == ChangeType.Patch && Volatile.Read(ref droppedPatches) == 1)
                    patchLetThrough.OnNext(Interlocked.Increment(ref patchesLetThroughAfterTheLoss));
                return next.Invoke(d);
            }));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))));

    /// <summary>
    /// A BURST OF ASKS AGAINST ONE OUTSTANDING ANSWER IS NOT A BURST OF FAILURES.
    ///
    /// <para>Pre-fix this test fails twice over, and both failures are the same defect: no ack is
    /// ever held (the acknowledgement overtakes its own answer), and the mirror spends a re-ask per
    /// gap-proving frame until <c>MaxUnansweredResyncs</c> faults it — the exception that reddened
    /// the samples content gate on a third of all runs.</para>
    /// </summary>
    [HubFact]
    public async Task AnAnswerStillInFlight_DoesNotCountAsUnanswered_AndTheMirrorConverges()
    {
        var host = GetHost();
        var client = GetClient();
        subscriber = client.Address;
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var clientWorkspace = client.GetWorkspace();
        var clientStream = clientWorkspace
            .GetRemoteStream<EntityStore>(CreateHostAddress(), new CollectionsReference(collectionName));

        // The mirror is live and mirrorStreamId is known — every injection below is scoped to it.
        await clientStream.Should().Within(10.Seconds()).Emit();

        // A fault is the pre-fix outcome, so observe it explicitly rather than letting it surface
        // as an incidental timeout: every wait below then reports what actually went wrong.
        using var faultWatch = clientStream.Subscribe(_ => { }, ex => mirrorFault = ex);

        // A burst whose SECOND patch the wire eats — the third patch chains onto a version the
        // mirror never applied, which is the proven gap that earns the one and only re-ask.
        for (var i = 1; i <= 3; i++)
            host.Post(
                new DataChangeRequest().WithUpdates(new MyData($"doc-{i}", $"value-{i}")),
                o => o.WithAccessContext(accessService.Context!));

        await answerHeld
            .Where(count => count >= 1)
            .Take(1)
            .Should().Within(15.Seconds())
            .Emit("the owner must have ANSWERED the re-ask with a re-assert snapshot — this test is "
                + "about an answer that is slow, so there has to be one");

        // THE BURST. More gap-proving frames than the give-up bound tolerates, every one of them
        // arriving while that single answer is still in flight. Each is posted only after the
        // previous one has actually reached the wire, so the sequence is fixed rather than raced.
        var writes = 3;
        for (var i = 1; i <= MaxUnansweredResyncs + 1; i++)
        {
            host.Post(
                new DataChangeRequest().WithUpdates(new MyData($"doc-{++writes}", $"value-{writes}")),
                o => o.WithAccessContext(accessService.Context!));
            var expected = i;
            await patchLetThrough
                .Where(count => count >= expected)
                .Take(1)
                .Should().Within(15.Seconds())
                .Emit($"gap-proving frame {expected} must reach the mirror — a burst the mirror "
                    + "never sees proves nothing about how it counts");
        }

        mirrorFault.Should().BeNull(
            "a mirror whose one outstanding re-ask is still being answered has observed no failure "
            + "at all: it asked once, the owner is answering, and every frame since has merely "
            + "re-stated that the answer has not arrived yet");

        // ── RELEASE, IN THE ORDER THE OWNER POSTED IT ──────────────────────────────────────────
        holdAnswers = false;
        var released = 0;
        while (heldAnswer.TryDequeue(out var delivery))
        {
            ((MessageHub)client).DeliverMessage(delivery);
            released++;
        }
        released.Should().BeGreaterThan(0, "the answer this test held has to be given back");

        // The released snapshot carries the two documents the mirror could never have obtained
        // otherwise: the one whose patch the wire ate, and the one whose patch proved the gap.
        await clientStream
            .Where(ci => ci.Value?.Collections.GetValueOrDefault(collectionName) is { } coll
                && coll.Instances.Values.OfType<MyData>().Any(x => x.Id == "doc-2")
                && coll.Instances.Values.OfType<MyData>().Any(x => x.Id == "doc-3"))
            .Take(1)
            .Should().Within(20.Seconds())
            .Emit("the held answer must land and re-base the mirror once the leg delivers it — a "
                + "resync that is merely slow still converges");

        // ── THE DISCRIMINATOR ──────────────────────────────────────────────────────────────────
        // Measured only now: the mirror has applied the released Full, and its sync hub is FIFO, so
        // every gap-proving frame above was processed before it. Whatever re-asks the burst was
        // going to provoke have provoked their re-asserts by now.
        Volatile.Read(ref reassertFullsPosted).Should().Be(1,
            $"the mirror had ONE outstanding re-ask, so it must have asked ONCE — {MaxUnansweredResyncs + 1} "
            + "further frames proving it still has no base are not "
            + $"{MaxUnansweredResyncs + 1} failures, they are the same request re-proved. Asking per "
            + "frame is what drove the give-up counter to its bound inside 9 ms during a bulk "
            + "install and faulted a perfectly healthy mirror");

        Volatile.Read(ref acksHeldBehindTheirAnswer).Should().BeGreaterThan(0,
            "the owner must acknowledge a re-subscribe only AFTER posting the snapshot that answers "
            + "it — an ack that overtakes its own answer makes 'acknowledged and unanswered' true of "
            + "every ask the moment it is made, which is exactly what the give-up then counts");

        Volatile.Read(ref patchesLetThroughAfterTheLoss).Should().BeGreaterThanOrEqualTo(
            MaxUnansweredResyncs + 1,
            "the burst has to have been big enough to trip the bound had every frame earned its own "
            + "re-ask — otherwise this test would pass on the code it exists to fail");

        mirrorFault.Should().BeNull(
            "a delayed answer must never fault the mirror: StreamLiveness.IsUsable then refuses to "
            + "serve it and every subscriber is torn down and re-established mid-install");

        // AND IT IS STILL A LIVE MIRROR, not merely an unfaulted one: the next write converges.
        host.Post(
            new DataChangeRequest().WithUpdates(new MyData($"doc-{++writes}", $"value-{writes}")),
            o => o.WithAccessContext(accessService.Context!));

        var total = writes;
        await clientStream
            .Where(ci => ci.Value?.Collections.GetValueOrDefault(collectionName) is { } coll
                && coll.Instances.Values.OfType<MyData>()
                    .Count(dd => dd.Text?.StartsWith("value-") == true) == total)
            .Take(1)
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit("the mirror must end up holding the owner's COMPLETE state — surviving the burst "
                + "is only worth anything if the stream still tracks its owner afterwards");
    }
}
