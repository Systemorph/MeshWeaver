using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Consuming a durable stream INSIDE a hub — declared once in the hub's configuration, started on
/// every activation, run on the hub's own message loop (Doc/Architecture/DurableStreams).
///
/// <code>
/// config.WithDurableStreamConsumer&lt;DocumentLogAppend&gt;(
///     "DocumentLog",
///     hub =&gt; hub.Address.Path,                         // the owner whose stream this hub consumes
///     (hub, item) =&gt; hub.GetMeshNodeStream(hub.Address.Path)
///         .Update(node =&gt; Append(node, item.Payload))     // runs on the hub's loop
///         .Select(_ =&gt; Unit.Default));
/// </code>
///
/// <para><b>The lease IS the subscription.</b> On activation the consumer CLAIMS the stream from the
/// stream node's hub. Granted, it reads the items strictly after the stream's acknowledged
/// <see cref="DurableStreamState.Checkpoint"/> and processes them one at a time, in order; after an
/// item's handler observable COMPLETED it acknowledges the sequence (fenced by its lease epoch) and
/// only then takes the next. Refused — another live hub holds the lease — it waits for the orphan
/// event (the stream node's <see cref="DurableStreamState.Subscriber"/> clearing), for a wake, or
/// for <see cref="ReclaimInterval"/>, and claims again; a holder that can no longer be reached is
/// taken over then. On dispose it releases the lease once the item in flight is done.</para>
///
/// <para><b>Guarantees.</b> Each item is handed to the handler ON THE HUB'S ACTION BLOCK — as a
/// request the hub posts to itself, so a recycle's quiesce phase waits for the item in flight (its
/// handler AND its acknowledgement) instead of cutting it. Across recycles, takeovers and races,
/// every item is processed exactly once RELATIVE TO ACKNOWLEDGED ITEMS: an acknowledged item is
/// never handed over again, and of two would-be subscribers only the one the stream's hub granted
/// processes anything. The window that remains is a process that dies between a handler completing
/// and its acknowledgement landing (or a handler outliving the quiesce budget during a recycle):
/// that one item is handed over again. A handler whose effect must be exactly-once even then
/// records <see cref="DurableStreamItem{T}.Sequence"/> in the same write as its effect.</para>
///
/// <para><b>Failures are loud and stop at the item.</b> A handler that errors, a payload that cannot
/// be read, a refused acknowledgement: the consumer logs it naming the stream and sequence, does not
/// acknowledge, and starts over from the claim after a backoff (1 s doubling to 60 s) — the item is
/// retried, never skipped. A handler observable that never completes stalls the consumer; that is a
/// handler defect.</para>
/// </summary>
public static class DurableStreamConsumerExtensions
{
    /// <summary>How often a refused consumer claims again on its own — the latency of taking over from
    /// a holder that died without releasing (it is asked for a sign of life on each claim).</summary>
    public static readonly TimeSpan ReclaimInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Every activation of this hub consumes the durable stream
    /// <c>(<paramref name="namespace"/>, <paramref name="streamKey"/>(hub))</c> — its own
    /// <c>_DurableStreamItem</c> nodes — with <paramref name="handler"/>.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="configuration">The hub configuration.</param>
    /// <param name="namespace">The stream family.</param>
    /// <param name="streamKey">The owning node's path of the stream this hub consumes — typically
    /// <c>hub =&gt; hub.Address.Path</c>.</param>
    /// <param name="handler">Processes one item ON THE HUB'S LOOP; the returned observable must
    /// complete when the item's effect is done (its emissions are ignored).</param>
    /// <returns>The configuration, for chaining.</returns>
    public static MessageHubConfiguration WithDurableStreamConsumer<T>(
        this MessageHubConfiguration configuration,
        string @namespace,
        Func<IMessageHub, string> streamKey,
        Func<IMessageHub, DurableStreamItem<T>, IObservable<Unit>> handler)
        => configuration.WithDurableStreamConsumer(
            @namespace,
            streamKey,
            hub => DurableStreamSource<T>.ItemsOf(new DurableStreamId(@namespace, streamKey(hub))),
            handler);

    /// <summary>
    /// <see cref="WithDurableStreamConsumer{T}(MessageHubConfiguration, string, Func{IMessageHub, string}, Func{IMessageHub, DurableStreamItem{T}, IObservable{Unit}})"/>
    /// over another ordered child set — e.g. a document's <c>_DocumentPart</c> nodes (see
    /// <see cref="DurableStreamSource{T}"/>). The lease and the checkpoint still live on the stream
    /// node <c>{streamKey}/_DurableStream/{namespace}</c>; the items are read where they already are.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="configuration">The hub configuration.</param>
    /// <param name="namespace">The stream family.</param>
    /// <param name="streamKey">The owning node's path.</param>
    /// <param name="source">Where this hub's items are.</param>
    /// <param name="handler">Processes one item on the hub's loop.</param>
    /// <returns>The configuration, for chaining.</returns>
    public static MessageHubConfiguration WithDurableStreamConsumer<T>(
        this MessageHubConfiguration configuration,
        string @namespace,
        Func<IMessageHub, string> streamKey,
        Func<IMessageHub, DurableStreamSource<T>> source,
        Func<IMessageHub, DurableStreamItem<T>, IObservable<Unit>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentNullException.ThrowIfNull(streamKey);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        return configuration
            .WithType(typeof(DurableStreamTurn), nameof(DurableStreamTurn))
            .WithType(typeof(DurableStreamTurnDone), nameof(DurableStreamTurnDone))
            .WithHandler<DurableStreamTurn>(
                (hub, turn) => HandleTurn(hub, turn, handler),
                (hub, delivery) => delivery.Message is DurableStreamTurn t
                    && t.Namespace == @namespace
                    && (delivery.Target is null || (delivery.Target with { Host = null }).Equals(hub.Address)))
            .WithHandler<DurableStreamWake>(
                (hub, wake) =>
                {
                    // Arriving activated this hub (that alone started the consumer); a consumer that
                    // was waiting for the lease claims again now instead of at its next interval.
                    hub.Get<Consumer<T>>(@namespace)?.Wake();
                    return wake.Processed();
                },
                (_, delivery) => delivery.Message is DurableStreamWake w && w.Namespace == @namespace)
            .WithInitialization(hub =>
            {
                var stream = new DurableStreamId(@namespace, streamKey(hub)).Validated();
                var consumer = new Consumer<T>(hub, stream, source(hub));
                hub.Set(consumer, @namespace);
                hub.RegisterForDisposal(consumer);
                consumer.Start();
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>
    /// The turn, ON THE HUB'S ACTION BLOCK: runs the handler, waits for its observable to complete,
    /// acknowledges the sequence to the stream's hub, then replies. The reply is the consumer's
    /// signal to hand over the next item.
    /// </summary>
    private static IMessageDelivery HandleTurn<T>(
        IMessageHub hub,
        IMessageDelivery<DurableStreamTurn> turn,
        Func<IMessageHub, DurableStreamItem<T>, IObservable<Unit>> handler)
    {
        void Reply(string? error, bool fenced = false) => hub.Post(
            new DurableStreamTurnDone(turn.Message.Namespace, turn.Message.Sequence, error, fenced),
            o => o.ResponseFor(turn));

        if (turn.Message.Item is not DurableStreamItem<T> item)
        {
            // Only this process posts turns, to itself, with the item attached; a turn without it
            // was serialized on the way — say so rather than process nothing.
            Reply($"durable stream turn {turn.Message.Namespace}#{turn.Message.Sequence} arrived without its item");
            return turn.Processed();
        }

        IObservable<Unit> work;
        try
        {
            work = handler(hub, item);
        }
        catch (Exception ex)
        {
            Reply(ex.ToString());
            return turn.Processed();
        }

        work.IgnoreElements()
            .Select(_ => (AckDurableStreamResponse?)null)
            .Concat(Observable.Defer(() => hub.ToStreamHub<AckDurableStreamResponse>(
                item.Stream, new AckDurableStreamRequest(hub.Address.ToString(), turn.Message.Epoch, item.Sequence))))
            .LastAsync()
            .Subscribe(
                ack => Reply(ack is { Accepted: true } ? null : ack?.Reason ?? "the acknowledgement was not answered",
                    fenced: ack is { Accepted: false }),
                ex => Reply(ex.ToString()));
        return turn.Processed();
    }

    /// <summary>One activation's consumer of one stream. Disposed with the hub.</summary>
    private sealed class Consumer<T> : IDisposable
    {
        private readonly IMessageHub hub;
        private readonly DurableStreamId stream;
        private readonly DurableStreamSource<T> source;
        private readonly ILogger logger;
        private readonly Subject<Unit> wakes = new();
        private readonly BehaviorSubject<bool> idle = new(true);
        private readonly System.Reactive.Disposables.CompositeDisposable subscriptions = new();
        private long epoch;

        public Consumer(IMessageHub hub, DurableStreamId stream, DurableStreamSource<T> source)
        {
            this.hub = hub;
            this.stream = stream;
            this.source = source;
            logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DurableStreamConsumerExtensions));
        }

        public void Wake() => wakes.OnNext(Unit.Default);

        public void Start()
        {
            logger.LogDebug("Durable stream consumer {Hub} starting on {Stream}", hub.Address, stream);

            subscriptions.Add(Observable.Defer(Lifecycle)
                .RetryWhen(failures => failures
                    .Select((failure, attempt) => (failure, attempt))
                    .TakeWhile(_ => !hub.IsShuttingDown)
                    .SelectMany(f =>
                    {
                        if (f.failure is DurableStreamLeaseLostException)
                        {
                            logger.LogWarning(f.failure,
                                "Durable stream consumer {Hub} lost the lease of {Stream}; claiming again", hub.Address, stream);
                            return Observable.Return(0L);
                        }
                        var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(f.attempt, 6))));
                        logger.LogError(f.failure,
                            "Durable stream consumer {Hub} stopped on {Stream}; the item was NOT acknowledged and is "
                            + "retried from the checkpoint in {Delay}", hub.Address, stream, delay);
                        return Observable.Timer(delay);
                    }))
                .Subscribe(
                    _ => { },
                    ex => logger.LogError(ex, "Durable stream consumer {Hub} on {Stream} ended with an error", hub.Address, stream)));

            // Graceful leave: once the hub is shutting down and the item in flight is done (it is
            // acknowledged inside its turn), give the lease back — that is the orphan event.
            subscriptions.Add(hub.ShuttingDown.Take(1)
                .SelectMany(_ => idle.Where(isIdle => isIdle).Take(1))
                .Subscribe(_ =>
                {
                    var held = Interlocked.Read(ref epoch);
                    if (held > 0)
                        hub.ToStreamHub<ReleaseDurableStreamResponse>(stream, new ReleaseDurableStreamRequest(hub.Address.ToString(), held))
                            .Subscribe(
                                released => logger.LogDebug("Durable stream consumer {Hub} released {Stream}: {Released}", hub.Address, stream, released.Released),
                                ex => logger.LogWarning(ex,
                                    "Durable stream consumer {Hub} could not release {Stream}; the next claimant takes it over once "
                                    + "this address stops answering", hub.Address, stream));
                }));
        }

        private IObservable<Unit> Lifecycle()
            => hub.ToStreamHub<ClaimDurableStreamResponse>(stream, new ClaimDurableStreamRequest(hub.Address.ToString()))
                .SelectMany(claim => claim.Granted
                    ? Consume(claim)
                    : WaitToClaimAgain(claim).SelectMany(_ => Observable.Defer(Lifecycle)));

        private IObservable<Unit> Consume(ClaimDurableStreamResponse claim)
        {
            Interlocked.Exchange(ref epoch, claim.Epoch);
            logger.LogInformation(
                "Durable stream consumer {Hub} holds {Stream} (epoch {Epoch}{TakeOver}); resuming after acknowledged sequence {Checkpoint}",
                hub.Address, stream, claim.Epoch,
                claim.TookOverFrom is null ? "" : $", taken over from {claim.TookOverFrom}", claim.Checkpoint);
            return hub.ReadDurable(stream, source, claim.Checkpoint)
                .Select(item => Observable.Defer(() =>
                    // 🚨 The item BOUNDARY: once the hub is shutting down no new item starts. The one in
                    // flight is not interrupted — its turn is a pending request the quiesce phase waits
                    // for — so a recycle stops between items, never inside one.
                    hub.IsShuttingDown ? Observable.Empty<Unit>() : Process(item, claim.Epoch)))
                .Concat();
        }

        private IObservable<Unit> Process(DurableStreamItem<T> item, long grantedEpoch)
            => Observable.Defer(() =>
                {
                    idle.OnNext(false);
                    return hub.Observe(
                        new DurableStreamTurn(stream.Namespace, item.Sequence) { Item = item, Epoch = grantedEpoch },
                        o => o.WithTarget(hub.Address));
                })
                .Take(1)
                .Select(reply => reply.Message switch
                {
                    { Fenced: true } => throw new DurableStreamLeaseLostException(stream, item.Sequence, reply.Message.Error),
                    { Error: { } error } => throw new DurableStreamHandlerException(stream, item.Sequence, error),
                    _ => Unit.Default,
                })
                .Finally(() => idle.OnNext(true));

        /// <summary>Refused: wait for the orphan event, a wake, or the reclaim interval.</summary>
        private IObservable<Unit> WaitToClaimAgain(ClaimDurableStreamResponse refusal)
        {
            logger.LogInformation(
                "Durable stream consumer {Hub}: {Stream} is held by {Holder}; waiting for it to be released",
                hub.Address, stream, refusal.Holder);
            var access = hub.ServiceProvider.GetService<AccessService>();
            var orphaned = access.RunAsSystem(() => hub.GetMeshNodeStream(DurableStreamPaths.StreamPath(stream))
                    .Select(node => node.ContentAs<DurableStreamState>(hub.JsonSerializerOptions)))
                .Where(state => state is { Subscriber: null })
                .Select(_ => Unit.Default);
            return Observable.Merge(orphaned, wakes, Observable.Timer(ReclaimInterval).Select(_ => Unit.Default))
                .Take(1);
        }

        public void Dispose()
        {
            subscriptions.Dispose();
            wakes.OnCompleted();
        }
    }
}

/// <summary>
/// One item handed to a consuming hub — a request the hub posts TO ITSELF so its quiesce phase waits
/// for the item (see <see cref="DurableStreamConsumerExtensions"/>). Plumbing: never constructed
/// outside the consumer, never sent across a process.
/// </summary>
/// <param name="Namespace">The stream family — selects which consumer of the hub handles it.</param>
/// <param name="Sequence">The item's sequence.</param>
public sealed record DurableStreamTurn(string Namespace, long Sequence) : IRequest<DurableStreamTurnDone>
{
    /// <summary>The item, attached in-process; not part of the wire shape.</summary>
    [JsonIgnore]
    public object? Item { get; init; }

    /// <summary>The lease epoch the acknowledgement carries.</summary>
    public long Epoch { get; init; }
}

/// <summary>The reply to a <see cref="DurableStreamTurn"/>: processed and acknowledged, or why not.</summary>
/// <param name="Namespace">The stream family.</param>
/// <param name="Sequence">The item's sequence.</param>
/// <param name="Error">Null on success.</param>
/// <param name="Fenced">True when the acknowledgement was refused because the lease moved on.</param>
public sealed record DurableStreamTurnDone(string Namespace, long Sequence, string? Error, bool Fenced = false);

/// <summary>A consumer's handler failed for an item. The item is not acknowledged and is retried.</summary>
/// <param name="stream">The stream.</param>
/// <param name="sequence">The item that failed.</param>
/// <param name="error">What failed.</param>
public sealed class DurableStreamHandlerException(DurableStreamId stream, long sequence, string error)
    : Exception($"Durable stream {stream} item {sequence} failed: {error}")
{
    /// <summary>The stream.</summary>
    public DurableStreamId Stream { get; } = stream;

    /// <summary>The item that failed.</summary>
    public long Sequence { get; } = sequence;
}

/// <summary>
/// The stream's hub refused an acknowledgement: another subscriber holds the lease now (this one was
/// taken over, or released). The consumer stops processing and claims again.
/// </summary>
/// <param name="stream">The stream.</param>
/// <param name="sequence">The item whose acknowledgement was refused.</param>
/// <param name="reason">The stream hub's reason.</param>
public sealed class DurableStreamLeaseLostException(DurableStreamId stream, long sequence, string? reason)
    : Exception($"Durable stream {stream}: the acknowledgement of item {sequence} was refused — {reason}")
{
    /// <summary>The stream.</summary>
    public DurableStreamId Stream { get; } = stream;
}
