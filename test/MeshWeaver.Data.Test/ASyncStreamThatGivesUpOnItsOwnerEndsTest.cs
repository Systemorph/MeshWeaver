using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// #6602 / #5011 — what a reader of a sync stream observes when the stream GIVES UP on its owner.
///
/// <para>The owner here is scripted at its delivery pipeline: once <see cref="refusing"/> is set it
/// answers every <see cref="SubscribeRequest"/> with the <see cref="ErrorType.ShuttingDown"/> NACK a
/// recycling owner sends, tagged with the activation the case chooses. The trigger is a
/// <see cref="StreamEndedEvent"/> from the owner, which is how a recycle reaches a subscriber.</para>
///
/// <list type="bullet">
///   <item><b>Distinct activations past the budget.</b> Each re-ask is refused by a NEW activation, so
///     the stream exceeds its activation budget and gives up. The reader must see the stream END with
///     <see cref="SyncStreamOwnerRefusedException"/>. Before the fix it saw nothing at all: the stream
///     stayed alive on its last value, a frozen reading that looks like a live one.</item>
///   <item><b>One activation, two carriers.</b> One teardown is reported both by its NACK and by its
///     announcement, each tagged with that activation. That is ONE activation and must not exhaust the
///     budget. Before the fix the announcement carried no identity, so each one was charged as a new
///     activation, and two of them plus two NACKs gave up on a teardown that was merely settling.</item>
/// </list>
/// </summary>
public class ASyncStreamThatGivesUpOnItsOwnerEndsTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>The owner's data.</summary>
    public record GiveUpItem(string Id);

    private volatile bool refusing;
    private int refusalsLeft = int.MaxValue;
    private int refusals;
    private int subscribes;
    private Func<int, string> activationForRefusal = n => $"{n:X8}";
    private bool alsoAnnounceEachRefusal;
    private IMessageHub? owner;
    private (string StreamId, Address Subscriber)? stream;
    private readonly ReplaySubject<int> acceptedAfterRefusals = new();
    private Exception? terminal;

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddDeliveryPipeline(p => p.AddPipeline((d, ct, next) =>
            {
                if (d.Message is not SubscribeRequest subscribe)
                    return next.Invoke(d, ct);
                Interlocked.Increment(ref subscribes);
                stream ??= (subscribe.StreamId, d.Sender);
                if (!refusing || Interlocked.Decrement(ref refusalsLeft) < 0)
                {
                    if (refusing)
                        acceptedAfterRefusals.OnNext(Volatile.Read(ref refusals));
                    return next.Invoke(d, ct);
                }

                var n = Interlocked.Increment(ref refusals);
                var activation = activationForRefusal(n);
                owner!.Post(
                    new DeliveryFailure(d,
                        $"Hub {owner.Address} is shutting down ({ShutdownNack.ActivationMarker}{activation}) — "
                        + $"cannot process SubscribeRequest; {ShutdownNack.ReactivationPromise}. Rejecting now.")
                    { ErrorType = ErrorType.ShuttingDown },
                    o => o.ResponseFor(d));
                if (alsoAnnounceEachRefusal)
                    owner.Post(new StreamEndedEvent(subscribe.StreamId) { Activation = activation },
                        o => o.WithTarget(d.Sender));
                return Observable.Return(d.Processed());
            }))
            .AddData(data => data.AddSource(src => src
                .WithType<GiveUpItem>(t => t.WithInitialData(
                    _ => Observable.Return(new[] { new GiveUpItem("one") }.AsEnumerable())))));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(GiveUpItem))
            .WithServices(services => services.Configure<SyncStreamOptions>(o =>
                o.RecycleReAskPace = TimeSpan.FromMilliseconds(20)))
            .AddData();

    private (IDisposable Held, ReplaySubject<Exception> Ended, ReplaySubject<int> Values) Open()
    {
        owner = GetHost();
        var client = GetClient();
        var ended = new ReplaySubject<Exception>();
        var values = new ReplaySubject<int>();
        var count = 0;
        var held = client.GetWorkspace()
            .GetRemoteStream<InstanceCollection, CollectionReference>(
                CreateHostAddress(), new CollectionReference(nameof(GiveUpItem)))
            .Subscribe(
                _ => values.OnNext(Interlocked.Increment(ref count)),
                ex => { Volatile.Write(ref terminal, ex); ended.OnNext(ex); ended.OnCompleted(); },
                () => { ended.OnNext(new InvalidOperationException("completed")); ended.OnCompleted(); });
        return (held, ended, values);
    }

    /// <summary>
    /// Every re-ask is refused by a new activation: the stream gives up, and the reader is TOLD —
    /// the stream ends with the typed terminal naming the owner.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RefusedByMoreActivationsThanTheBudget_TheReaderSeesTheStreamEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var (held, ended, values) = Open();
        using var _ = held;
        await values.Should().Within(TestTimeouts.Convergence).Emit("the stream is established", ct);

        refusing = true;
        var (streamId, subscriber) = stream!.Value;
        owner!.Post(new StreamEndedEvent(streamId), o => o.WithTarget(subscriber));

        var refused = (SyncStreamOwnerRefusedException)await ended.Should().Within(TestTimeouts.Convergence).Match(
            ex => ex is SyncStreamOwnerRefusedException,
            "a stream that has stopped re-asking its owner must END, so its reader's recovery runs — "
            + "held alive on its last value it is a frozen reading that looks live (#6602, #5011)", ct);
        Output.WriteLine(refused.Message);
        refused.Owner.Should().Be(CreateHostAddress().ToString());
        refused.Message.Should().Contain("DISTINCT owner activations");
        Volatile.Read(ref refusals).Should().BeGreaterThanOrEqualTo(3,
            "the give-up happened after the activation budget was spent, not on the first refusal");
    }

    /// <summary>
    /// One activation refuses several re-asks and also announces each end. That is ONE teardown being
    /// ridden out, so the stream must not give up, and it re-establishes once the owner answers.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OneActivationsNackAndAnnouncement_AreOneActivation_AndTheStreamRecovers()
    {
        var ct = TestContext.Current.CancellationToken;
        activationForRefusal = _ => "0A0A0A0A";
        alsoAnnounceEachRefusal = true;
        refusalsLeft = 4;
        var (held, ended, values) = Open();
        using var _ = held;
        await values.Should().Within(TestTimeouts.Convergence).Emit("the stream is established", ct);

        refusing = true;
        var (streamId, subscriber) = stream!.Value;
        owner!.Post(new StreamEndedEvent(streamId) { Activation = "0A0A0A0A" }, o => o.WithTarget(subscriber));

        await acceptedAfterRefusals.Should().Within(TestTimeouts.Convergence).Emit(
            "the stream rides one activation's teardown out and re-asks until the owner answers — a NACK "
            + "and an announcement from the same activation are one activation, not two (#6602)", ct);
        Volatile.Read(ref refusals).Should().Be(4);
        // The give-up can only happen BEFORE a re-ask is accepted, so by now it either has or never will.
        Volatile.Read(ref terminal).Should().BeNull(
            "one activation's NACK and announcement must not be charged as distinct activations");
    }
}
