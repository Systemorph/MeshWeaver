using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The hub of a durable stream's STREAM node — the one place a stream's order and ownership are
/// decided (Doc/Architecture/DurableStreams). Every append, claim, acknowledgement and release is a
/// request to this hub, and all of them run through ONE serial chain (<c>Subject</c> +
/// <c>Concat</c>), so their pooled I/O never interleaves: an append that is still creating its item
/// node holds the next append back, which is what makes sequences contiguous and the lease a
/// single owner.
///
/// <list type="bullet">
/// <item><b>Append</b> — creates the item node <c>{stream}/_DurableStreamItem/{seq:D12}</c>, then
/// records <see cref="DurableStreamState.LastSequence"/>, then replies with the sequence. The item
/// is a saved mesh node before the producer hears "durable". A stream nobody holds is ORPHANED:
/// the append also posts a <see cref="DurableStreamWake"/> to the stream's owner, whose activation
/// starts its consumer.</item>
/// <item><b>Claim</b> — grants the lease to the sender when the stream is free or the sender
/// already holds it; otherwise asks the holder for a sign of life (<see cref="PingRequest"/>) and
/// grants it to the sender only when the holder cannot be reached (a takeover). Every grant starts
/// a NEW epoch.</item>
/// <item><b>Ack</b> — advances <see cref="DurableStreamState.Checkpoint"/> only for the current
/// holder at the current epoch; a stale holder is FENCED.</item>
/// <item><b>Release</b> — orphans the stream (<see cref="DurableStreamState.OrphanedAt"/>): the
/// stream node changing is the orphan event every would-be subscriber watches.</item>
/// </list>
/// </summary>
public static class DurableStreamHub
{
    /// <summary>How long a claim waits for the current holder to answer before taking over.</summary>
    public static readonly TimeSpan HolderProbeBudget = TimeSpan.FromSeconds(5);

    /// <summary>Configures a stream node's hub. Applied by the <c>DurableStream</c> NodeType.</summary>
    /// <param name="configuration">The stream node's hub configuration.</param>
    /// <returns>The configuration.</returns>
    public static MessageHubConfiguration ConfigureDurableStreamHub(this MessageHubConfiguration configuration)
        => configuration
            .WithHandler<AppendDurableStreamItemRequest>((hub, request) => Enqueue(hub, request, Append))
            .WithHandler<ClaimDurableStreamRequest>((hub, request) => Enqueue(hub, request, Claim))
            .WithHandler<AckDurableStreamRequest>((hub, request) => Enqueue(hub, request, Ack))
            .WithHandler<ReleaseDurableStreamRequest>((hub, request) => Enqueue(hub, request, Release))
            .WithInitialization(hub =>
            {
                var runtime = new Runtime(hub);
                hub.Set(runtime);
                hub.RegisterForDisposal(runtime);
                return Observable.Return(Unit.Default);
            });

    private static IMessageDelivery Enqueue<TRequest>(
        IMessageHub hub, IMessageDelivery<TRequest> request,
        Func<IMessageHub, Runtime, IMessageDelivery<TRequest>, IObservable<Unit>> operation)
    {
        var runtime = hub.Get<Runtime>();
        if (runtime is null)
        {
            // Initialization registers the runtime before the hub takes any message; a request
            // without it means this hub was configured without ConfigureDurableStreamHub's init.
            hub.Post(new DeliveryFailure(request, $"{hub.Address} is not a durable stream hub") { ErrorType = ErrorType.Failed },
                o => o.ResponseFor(request));
            return request.Processed();
        }
        runtime.Enqueue(() => operation(hub, runtime, request));
        return request.Processed();
    }

    // ── Append ────────────────────────────────────────────────────────────────────────────────

    private static IObservable<Unit> Append(IMessageHub hub, Runtime runtime, IMessageDelivery<AppendDurableStreamItemRequest> request)
        => runtime.State()
            .SelectMany(state => runtime.NextSequence(state).Select(sequence => (state, sequence)))
            .SelectMany(x => runtime.CreateItem(x.state, x.sequence, request.Message.PayloadJson)
                .SelectMany(_ => runtime.Save(x.state with { LastSequence = x.sequence }))
                .Select(saved => (saved, x.sequence)))
            .Do(x =>
            {
                hub.Post(new AppendDurableStreamItemResponse(x.sequence, null), o => o.ResponseFor(request));
                if (x.saved.Subscriber is null)
                    runtime.WakeOwner(x.saved);
            })
            .Select(_ => Unit.Default)
            .Catch<Unit, Exception>(ex =>
            {
                // The sequence is re-derived from the stored items on the next append, so a create
                // that half-happened (node written, LastSequence not) can never be handed out twice.
                runtime.ForgetSequence();
                runtime.Logger.LogWarning(ex, "Durable stream {Stream}: append failed", hub.Address);
                hub.Post(new AppendDurableStreamItemResponse(0, ex.Message), o => o.ResponseFor(request));
                return Observable.Return(Unit.Default);
            });

    // ── Claim ─────────────────────────────────────────────────────────────────────────────────

    private static IObservable<Unit> Claim(IMessageHub hub, Runtime runtime, IMessageDelivery<ClaimDurableStreamRequest> request)
    {
        var claimant = request.Message.Subscriber;
        return runtime.State()
            .SelectMany(state =>
                state.Subscriber is null || state.Subscriber == claimant
                    ? Grant(hub, runtime, request, state, claimant, tookOverFrom: null)
                    : HolderIsAlive(hub, state.Subscriber)
                        .SelectMany(alive => alive
                            ? Refuse(hub, request, state)
                            : Grant(hub, runtime, request, state, claimant, tookOverFrom: state.Subscriber)))
            .Catch<Unit, Exception>(ex => Fail(hub, request, ex));
    }

    private static IObservable<Unit> Grant(
        IMessageHub hub, Runtime runtime, IMessageDelivery<ClaimDurableStreamRequest> request,
        DurableStreamState state, string claimant, string? tookOverFrom)
        => runtime.Save(state with
            {
                Subscriber = claimant,
                LeaseEpoch = state.LeaseEpoch + 1,
                SubscribedAt = DateTimeOffset.UtcNow,
                OrphanedAt = null,
            })
            .Do(saved =>
            {
                if (tookOverFrom is not null)
                    runtime.Logger.LogWarning(
                        "[DurableStreams] {Stream}: {Claimant} TOOK OVER the lease from {Holder}, which did not answer within {Budget}; "
                        + "resuming after acknowledged sequence {Checkpoint} (epoch {Epoch})",
                        hub.Address, claimant, tookOverFrom, HolderProbeBudget, saved.Checkpoint, saved.LeaseEpoch);
                else
                    runtime.Logger.LogInformation(
                        "[DurableStreams] {Stream}: lease granted to {Claimant} (epoch {Epoch}, checkpoint {Checkpoint}, last {Last})",
                        hub.Address, claimant, saved.LeaseEpoch, saved.Checkpoint, saved.LastSequence);
                hub.Post(new ClaimDurableStreamResponse(true, saved.LeaseEpoch, saved.Checkpoint, null, tookOverFrom),
                    o => o.ResponseFor(request));
            })
            .Select(_ => Unit.Default);

    private static IObservable<Unit> Refuse(IMessageHub hub, IMessageDelivery<ClaimDurableStreamRequest> request, DurableStreamState state)
    {
        hub.Post(new ClaimDurableStreamResponse(false, state.LeaseEpoch, state.Checkpoint, state.Subscriber, null),
            o => o.ResponseFor(request));
        return Observable.Return(Unit.Default);
    }

    /// <summary>
    /// A sign of life from the holder. Reaching a NODE address activates its hub, so a node that
    /// owns a lease always answers — ownership stays with the address; only an address no silo can
    /// serve any more (a gone portal or worker) loses it.
    /// </summary>
    private static IObservable<bool> HolderIsAlive(IMessageHub hub, string holder)
        => hub.NodeOperationIssuingHub().Observe(new PingRequest(), o => o.WithTarget(new Address(holder)))
            .Take(1)
            .Select(_ => true)
            .Timeout(HolderProbeBudget)
            .Catch<bool, Exception>(_ => Observable.Return(false));

    // ── Ack ───────────────────────────────────────────────────────────────────────────────────

    private static IObservable<Unit> Ack(IMessageHub hub, Runtime runtime, IMessageDelivery<AckDurableStreamRequest> request)
    {
        var sender = request.Message.Subscriber;
        return runtime.State()
            .SelectMany(state =>
            {
                if (state.Subscriber != sender || state.LeaseEpoch != request.Message.Epoch)
                {
                    hub.Post(new AckDurableStreamResponse(false,
                            $"fenced: the lease is held by {state.Subscriber ?? "nobody"} at epoch {state.LeaseEpoch}, "
                            + $"not by {sender} at epoch {request.Message.Epoch}"),
                        o => o.ResponseFor(request));
                    return Observable.Return(Unit.Default);
                }
                if (request.Message.Sequence <= state.Checkpoint)
                {
                    hub.Post(new AckDurableStreamResponse(true, null), o => o.ResponseFor(request));
                    return Observable.Return(Unit.Default);
                }
                return runtime.Save(state with { Checkpoint = request.Message.Sequence, CheckpointAt = DateTimeOffset.UtcNow })
                    .Do(_ => hub.Post(new AckDurableStreamResponse(true, null), o => o.ResponseFor(request)))
                    .Select(_ => Unit.Default);
            })
            .Catch<Unit, Exception>(ex => Fail(hub, request, ex));
    }

    // ── Release ───────────────────────────────────────────────────────────────────────────────

    private static IObservable<Unit> Release(IMessageHub hub, Runtime runtime, IMessageDelivery<ReleaseDurableStreamRequest> request)
    {
        var sender = request.Message.Subscriber;
        return runtime.State()
            .SelectMany(state =>
            {
                if (state.Subscriber != sender || state.LeaseEpoch != request.Message.Epoch)
                {
                    hub.Post(new ReleaseDurableStreamResponse(false), o => o.ResponseFor(request));
                    return Observable.Return(Unit.Default);
                }
                return runtime.Save(state with
                    {
                        Subscriber = null,
                        LastSubscriber = sender,
                        OrphanedAt = DateTimeOffset.UtcNow,
                    })
                    .Do(saved =>
                    {
                        runtime.Logger.LogInformation(
                            "[DurableStreams] {Stream} ORPHANED: {Subscriber} released the lease at checkpoint {Checkpoint} of {Last}",
                            hub.Address, sender, saved.Checkpoint, saved.LastSequence);
                        hub.Post(new ReleaseDurableStreamResponse(true), o => o.ResponseFor(request));
                        // Items the leaver did not acknowledge are waiting: bring the owner back for them.
                        if (saved.LastSequence > saved.Checkpoint)
                            runtime.WakeOwner(saved);
                    })
                    .Select(_ => Unit.Default);
            })
            .Catch<Unit, Exception>(ex => Fail(hub, request, ex));
    }

    private static IObservable<Unit> Fail(IMessageHub hub, IMessageDelivery request, Exception ex)
    {
        hub.Post(new DeliveryFailure(request, ex.Message) { ErrorType = ErrorType.Failed }, o => o.ResponseFor(request));
        return Observable.Return(Unit.Default);
    }

    /// <summary>
    /// Per-activation state of one stream hub: the serial chain every operation runs through, the
    /// cached stream state (this hub is its only writer), and the next sequence. Disposed with the
    /// hub; never shared between activations.
    /// </summary>
    private sealed class Runtime : IDisposable
    {
        private readonly IMessageHub hub;
        private readonly Subject<Func<IObservable<Unit>>> operations = new();
        private readonly ISubject<Func<IObservable<Unit>>> serialized;
        private readonly IDisposable chain;
        private DurableStreamState? state;
        private long? nextSequence;
        private int waking;

        public Runtime(IMessageHub hub)
        {
            this.hub = hub;
            Logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DurableStreamHub));
            serialized = Subject.Synchronize(operations);
            chain = serialized
                .Select(op => Observable.Defer(op).Catch<Unit, Exception>(ex =>
                {
                    Logger.LogError(ex, "Durable stream {Stream}: an operation faulted outside its own handler", hub.Address);
                    return Observable.Empty<Unit>();
                }))
                .Concat()
                .Subscribe(_ => { }, ex => Logger.LogError(ex, "Durable stream {Stream}: the serial chain faulted", hub.Address));
        }

        public ILogger Logger { get; }

        public void Enqueue(Func<IObservable<Unit>> operation) => serialized.OnNext(operation);

        public void ForgetSequence() => nextSequence = null;

        /// <summary>The stream node's content — read once per activation, then kept (single writer).</summary>
        public IObservable<DurableStreamState> State()
            => state is { } cached
                ? Observable.Return(cached)
                : hub.GetMeshNodeStream(hub.Address.Path)
                    .Take(1)
                    .Select(node => node.ContentAs<DurableStreamState>(hub.JsonSerializerOptions) ?? FreshState(node))
                    .Do(read => state = read);

        private DurableStreamState FreshState(MeshNode node)
        {
            // A stream node created without content (by hand): derive the id from its path.
            var key = node.Namespace?.EndsWith("/" + DurableStreamPaths.StreamSegment, StringComparison.Ordinal) == true
                ? node.Namespace[..^(DurableStreamPaths.StreamSegment.Length + 1)]
                : node.Namespace ?? "";
            return new DurableStreamState { Namespace = node.Id, Key = key };
        }

        public IObservable<DurableStreamState> Save(DurableStreamState next)
            => hub.GetMeshNodeStream(hub.Address.Path)
                .Update(node => node with { Content = next })
                .Take(1)
                .Select(_ => next)
                .Do(saved => state = saved);

        /// <summary>
        /// The next sequence. On the first append of an activation it is derived from BOTH the
        /// recorded <see cref="DurableStreamState.LastSequence"/> and the item nodes that exist — an
        /// activation that died between creating an item and recording it must not hand that
        /// sequence out again.
        /// </summary>
        public IObservable<long> NextSequence(DurableStreamState current)
        {
            if (nextSequence is { } known)
                return Observable.Return(known);
            var stream = new DurableStreamId(current.Namespace, current.Key);
            return HighestStoredSequence(stream)
                .Select(stored => Math.Max(stored, current.LastSequence) + 1)
                .Do(next => nextSequence = next);
        }

        private IObservable<long> HighestStoredSequence(DurableStreamId stream)
        {
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var access = hub.ServiceProvider.GetService<AccessService>();
            return access.RunAsSystem(() => meshService.Query<MeshNode>(
                        MeshQueryRequest.FromQuery($"namespace:{DurableStreamPaths.ItemsNamespace(stream)}").Complete())
                    .Where(change => change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                    .Take(1))
                .Select(change => change.Items
                    .Select(item => DurableStreamPaths.SequenceOf(item.Id) ?? 0L)
                    .DefaultIfEmpty(0L)
                    .Max());
        }

        public IObservable<Unit> CreateItem(DurableStreamState current, long sequence, string payloadJson)
        {
            var stream = new DurableStreamId(current.Namespace, current.Key);
            var item = new MeshNode(DurableStreamPaths.ItemId(sequence), DurableStreamPaths.ItemsNamespace(stream))
            {
                NodeType = DurableStreamNodeTypes.Item,
                Name = DurableStreamPaths.ItemId(sequence),
                MainNode = current.Key,
                State = MeshNodeState.Active,
                Content = new DurableStreamItemContent
                {
                    Sequence = sequence,
                    PayloadJson = payloadJson,
                    AppendedAt = DateTimeOffset.UtcNow,
                },
            };
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var access = hub.ServiceProvider.GetService<AccessService>();
            return access.RunAsSystem(() => meshService.CreateNode(item))
                .Take(1)
                .Do(_ => nextSequence = sequence + 1)
                .Select(_ => Unit.Default);
        }

        /// <summary>
        /// Wakes the stream's owner: an item is waiting and nobody holds the lease.
        ///
        /// <para>🚨 The wake is a READ of the owner node, not a message. A recycled owner is still
        /// QUIESCING for a moment after it released — waiting for its last item — and refuses every
        /// new message then; a fire-and-forget wake landing in that window is simply lost, and with
        /// it the only thing that would have brought the consumer back. The mesh read re-probes a
        /// <c>ShuttingDown</c> refusal until a FRESH activation answers, and an activation is what
        /// starts the owner's consumer. Then a <see cref="DurableStreamWake"/> nudges a consumer that
        /// was already up but waiting for the lease. One wake in flight per stream at a time.</para>
        /// </summary>
        public void WakeOwner(DurableStreamState current)
        {
            if (Interlocked.Exchange(ref waking, 1) == 1)
                return;
            var access = hub.ServiceProvider.GetService<AccessService>();
            access.RunAsSystem(() => hub.GetMeshNodeOutcome(current.Key))
                .Finally(() => Interlocked.Exchange(ref waking, 0))
                .Subscribe(
                    outcome =>
                    {
                        if (outcome.Status == NodeReadStatus.Present)
                            hub.NodeOperationIssuingHub().Post(new DurableStreamWake(current.Namespace, current.Key), o => o.WithTarget(new Address(current.Key)));
                        else
                            Logger.LogWarning(
                                "[DurableStreams] {Stream}: items are waiting but the owner {Owner} could not be woken ({Status})",
                                hub.Address, current.Key, outcome.Status);
                    },
                    ex => Logger.LogWarning(ex,
                        "[DurableStreams] {Stream}: items are waiting but waking the owner {Owner} failed", hub.Address, current.Key));
        }

        public void Dispose()
        {
            serialized.OnCompleted();
            chain.Dispose();
        }
    }
}
