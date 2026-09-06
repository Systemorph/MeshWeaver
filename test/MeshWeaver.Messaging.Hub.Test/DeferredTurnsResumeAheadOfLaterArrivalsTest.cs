using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic repro for MeshWeaver.Plugins#1394 — <b>a message parked behind an
/// initialization gate is processed LAST</b>, after messages that arrived while it was parked.
///
/// <para><b>The claim that does not hold.</b> <c>MessageService.OpenGate</c> drains the deferred
/// queue with <c>while (deferredQueue.Count > 0) mainQueue.Enqueue(deferredQueue.Dequeue())</c>,
/// under a comment promising the deferred turns "run before any message that arrives after the
/// gate opens". That is true only for arrivals AFTER the open. A message that arrived BEFORE the
/// open and is still sitting in <c>mainQueue</c> — enqueued but not yet turned, because the loop
/// was busy — is already ahead of the deferred turns being appended behind it. The parked message
/// then runs last.</para>
///
/// <para><b>Why the ordering is guaranteed to be wrong, not merely unlucky.</b> Deferral happens
/// at TURN time, not at arrival: a message is dequeued, found on-target with a gate closed, and
/// pushed onto <c>deferredQueue</c>. So everything in <c>deferredQueue</c> is strictly OLDER than
/// anything still waiting in <c>mainQueue</c> — the turn loop is FIFO, so a message that had not
/// yet been turned cannot have arrived first. Appending is therefore always the wrong end.</para>
///
/// <para><b>Field evidence.</b> <c>ActivationBacklogFifoTest</c> on Plugins <c>main</c> observed
/// <c>B, C, A</c> where <c>A</c> was posted first and released first. It reproduces only when the
/// hub's turn loop happens to be busy across the gate open, which is why it is load-sensitive on
/// CI and green in isolation.</para>
///
/// <para><b>How this test forces that window with no timing hope.</b> Two facts do all the work,
/// both structural: the turn loop is strictly FIFO, and "a handler that Posts to its own hub
/// enqueues behind the current turn". So <c>A</c> is posted first and is provably deferred by the
/// time the blocker's turn begins; the blocker then posts <c>B</c> and <c>C</c> from inside its
/// own turn, which places them in <c>mainQueue</c> behind it; the gate is opened while the blocker
/// still holds the loop; and only then is the blocker released. No delay, no polling, no
/// sleep — the queue states are entailed by the order of the steps.</para>
/// </summary>
public class DeferredTurnsResumeAheadOfLaterArrivalsTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string GateName = "test-gate";

    /// <summary>An ordinary message: does NOT pass the gate, so it defers.</summary>
    private record Tagged(string Tag);

    /// <summary>Passes the gate, so it can occupy the turn loop while the gate is still closed.</summary>
    private record Blocker;

    /// <summary>Released by the test to end the blocker's turn — never a delay or a semaphore.</summary>
    private readonly AsyncSubject<Unit> release = new();

    /// <summary>Fires once the blocker's turn has begun AND it has posted B and C.</summary>
    private readonly AsyncSubject<Unit> windowOpen = new();

    /// <summary>Fires once all three tagged messages have been handled.</summary>
    private readonly AsyncSubject<Unit> allHandled = new();

    private ImmutableList<string> processed = ImmutableList<string>.Empty;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(Tagged), typeof(Blocker))
            // Only the blocker may run while the gate is closed; everything else defers.
            .WithInitializationGate(GateName, d => d.Message is Blocker)
            .WithHandler<Tagged>((_, delivery) =>
            {
                // The turn loop is single-threaded, so this read-after-write is not a race.
                ImmutableInterlocked.Update(
                    ref processed, static (l, t) => l.Add(t), delivery.Message.Tag);
                if (processed.Count == 3)
                {
                    allHandled.OnNext(Unit.Default);
                    allHandled.OnCompleted();
                }
                return delivery.Processed();
            })
            .WithHandler<Blocker>(async (hub, delivery, _) =>
            {
                // Posting from inside our own turn enqueues behind it: B and C are now in
                // mainQueue, un-turned, while the gate is still closed and A is already deferred.
                hub.Post(new Tagged("B"), o => o.WithTarget(hub.Address));
                hub.Post(new Tagged("C"), o => o.WithTarget(hub.Address));
                windowOpen.OnNext(Unit.Default);
                windowOpen.OnCompleted();
                await release;                 // hold the turn until the test has opened the gate
                return delivery.Processed();
            });

    [Fact(Timeout = 30_000)]
    public async Task AMessageParkedBehindAGate_RunsBeforeMessagesThatArrivedWhileItWasParked()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();

        // A is posted first, so the FIFO turn loop reaches it first and defers it (it does not
        // pass the gate). The blocker is posted second, so when its turn starts — which is what
        // windowOpen reports — A is provably already in the deferred queue.
        host.Post(new Tagged("A"), o => o.WithTarget(host.Address));
        host.Post(new Blocker(), o => o.WithTarget(host.Address));

        await windowOpen.Timeout(TimeSpan.FromSeconds(20)).Await(ct);
        Output.WriteLine("A deferred; B and C queued behind the running blocker; gate still closed.");

        // The window: deferred=[A], mainQueue=[B,C], loop busy. Opening now is what appends A
        // behind B and C.
        host.OpenGate(GateName).Should().BeTrue("the gate must still have been closed");
        Output.WriteLine("Gate opened while the loop was busy.");

        release.OnNext(Unit.Default);
        release.OnCompleted();

        await allHandled.Timeout(TimeSpan.FromSeconds(20)).Await(ct);
        Output.WriteLine($"Processing order: {string.Join(", ", processed)}");

        processed.Should().Equal(new[] { "A", "B", "C" },
            "a message parked behind an initialization gate arrived BEFORE the messages that "
            + "queued while it was parked, so it must be processed before them — appending the "
            + "deferred queue to the back of the main queue puts it last (Plugins#1394)");
    }
}
