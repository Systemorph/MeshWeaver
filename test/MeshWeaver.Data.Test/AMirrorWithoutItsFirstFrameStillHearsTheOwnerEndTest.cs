using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// MeshWeaver#8433 — the owner's <see cref="StreamEndedEvent"/> must pass a mirror's
/// <c>SynchronizationGate</c>.
///
/// <para>A mirror whose INITIAL frame was lost (the owner took the SubscribeRequest and never
/// delivered a Full) is the one the heartbeat's named-stream answer exists to repair (#6047): the
/// owner answers <see cref="StreamEndedEvent"/> and the mirror re-asks. The gate only let frames
/// and errors through, so the answer was deferred behind the very frame it was meant to replace —
/// measured on a live instance as two such events every 45 s for hours, none ever handled, until the
/// hub's deferred backlog hit its cap and logged <c>GATE-STUCK</c>.</para>
///
/// <para>The owner is scripted at its delivery pipeline: the FIRST <see cref="SubscribeRequest"/> is
/// swallowed (no ack, no frame), later ones are served.</para>
/// </summary>
public class AMirrorWithoutItsFirstFrameStillHearsTheOwnerEndTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record LostInitialItem(string Id);

    private int subscribes;
    private IMessageHub? owner;
    private (string StreamId, Address Subscriber)? stream;
    private readonly ReplaySubject<int> secondAsk = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddDeliveryPipeline(p => p.AddPipeline((d, ct, next) =>
            {
                if (d.Message is not SubscribeRequest subscribe)
                    return next.Invoke(d, ct);
                var n = Interlocked.Increment(ref subscribes);
                stream ??= (subscribe.StreamId, d.Sender);
                if (n == 1)
                    return Observable.Return(d.Processed()); // the lost initial: no ack, no Full
                secondAsk.OnNext(n);
                return next.Invoke(d, ct);
            }))
            .AddData(data => data.AddSource(src => src
                .WithType<LostInitialItem>(t => t.WithInitialData(
                    _ => Observable.Return(new[] { new LostInitialItem("one") }.AsEnumerable())))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(LostInitialItem))
            .AddData();

    /// <summary>
    /// 🚨 THE PIN. After the lost initial the owner says it does not serve the stream; the mirror
    /// must re-ask, and the re-ask's frame must reach the reader.
    ///
    /// <para><b>Fail-without:</b> the announcement waits behind <c>SynchronizationGate</c>, the
    /// mirror never re-asks, and the reader sees no value.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnOwnerEnd_BeforeTheFirstFrame_MakesTheMirrorReAsk_AndTheReaderGetsItsData()
    {
        var ct = TestContext.Current.CancellationToken;
        owner = GetHost();
        var client = GetClient();
        var values = new ReplaySubject<int>();
        var count = 0;
        using var held = client.GetWorkspace()
            .GetRemoteStream<InstanceCollection, CollectionReference>(
                CreateHostAddress(), new CollectionReference(nameof(LostInitialItem)))
            .Subscribe(_ => values.OnNext(Interlocked.Increment(ref count)));

        // The first ask has been swallowed, so the mirror is waiting for a frame that never comes.
        await Observable.Interval(TimeSpan.FromMilliseconds(20))
            .Where(_ => stream is not null)
            .Take(1)
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        Volatile.Read(ref count).Should().Be(0, "the owner swallowed the initial subscribe");

        var (streamId, subscriber) = stream!.Value;
        owner.Post(new StreamEndedEvent(streamId), o => o.WithTarget(subscriber));

        await secondAsk.Should().Within(TestTimeouts.Convergence)
            .Emit("the mirror must re-ask once its owner says it does not serve the stream", ct);
        await values.Should().Within(TestTimeouts.Convergence)
            .Emit("the re-ask's first frame reaches the reader and the gate opens", ct);
    }
}
