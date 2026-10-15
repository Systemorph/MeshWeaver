using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>A request the owner answers on its own turn: once it is answered, everything the
/// client posted before it has been handled by the owner.</summary>
public record ReassertBarrierRequest : IRequest<ReassertBarrierResponse>;

/// <summary>The answer to <see cref="ReassertBarrierRequest"/>.</summary>
public record ReassertBarrierResponse;

/// <summary>
/// 🚨 <b>A RE-ASK THE OWNER ACCEPTED MUST BE ANSWERED EVEN WHEN THE OWNER RECYCLES BEFORE IT HAS
/// ANYTHING TO SEND</b> (Systemorph/MeshWeaver#6759).
///
/// <para><b>The defect.</b> A re-subscribe that reaches an owner already serving the stream is a
/// RE-ASSERT: the owner re-sends the stream's current state and acknowledges. When the stream has
/// no state yet, the owner holds its answer until the stream first emits
/// (<c>AnswerWhenTheStreamFirstEmits</c>). That hold was registered on the stream's disposal and
/// did nothing else there, so an owner recycled before its stream ever emitted dropped the answer
/// silently: no acknowledgement, no refusal. On the subscriber the re-ask stays in flight, and its
/// in-flight guard swallows every later re-ask, the owner's own end-of-stream announcement
/// included. The read then emits nothing until the request timeout ends the stranded re-ask.</para>
///
/// <para><b>The interleaving, forced.</b> The owner's node store starts EMPTY, so its stream has
/// nothing to re-assert. A change-feed event on the owner's path makes the subscriber re-ask while
/// it is still behind; the owner holds that answer. The owner is then recycled by a self-posted
/// <see cref="DisposeRequest"/> (the overlay self-heal's shape) and its next activation loads the
/// node. Every ordering is a positive signal (a counted <see cref="SubscribeRequest"/>, a barrier
/// the owner answers on its own turn, the owner's <c>DisposalCompleted</c>); nothing sleeps.</para>
///
/// <para>The client's request timeout is set far beyond the convergence wait, so nothing but an
/// answer from the owner can release the stranded re-ask inside the test. Before the fix the read
/// never converges; after it the owner refuses the held re-ask with a transient
/// <see cref="ErrorType.ShuttingDown"/> NACK as its stream is disposed, the subscriber rides the
/// recycle out, and its next re-ask lands on the fresh activation.</para>
/// </summary>
public class UnansweredReassertOnRecycleTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly MeshNode Node = new("reassert", "ns") { Name = "loaded" };

    /// <summary>Long, so no heartbeat ever reaches the owner during the test.</summary>
    private static readonly TimeSpan NoHeartbeat = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Far beyond the convergence wait: the request timeout is the only other thing that ends a
    /// stranded re-ask, and it must not be what the assertion measures.
    /// </summary>
    private static readonly TimeSpan LongRequestTimeout = TimeSpan.FromMinutes(10);

    // Instance state — never static, so nothing bleeds between tests.
    private int subscribeRequests;
    private int seeded;

    private sealed class TestMeshChangeFeed : IMeshChangeFeed, IDisposable
    {
        private readonly Subject<MeshChangeEvent> subject = new();

        public void Publish(MeshChangeEvent change) => subject.OnNext(change);

        public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
            => filter is null
                ? subject.Subscribe(handler)
                : subject.Subscribe(e => { if (e.Kind == filter.Value) handler(e); });

        public void Dispose() => subject.Dispose();
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        base.ConfigureHost(configuration)
            .WithTypes(typeof(ReassertBarrierRequest), typeof(ReassertBarrierResponse), typeof(MeshNodeReference))
            .WithHandler<ReassertBarrierRequest>((hub, request) =>
            {
                hub.Post(new ReassertBarrierResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            // Passive counter: returns the delivery unprocessed so the data layer's own handler
            // serves it in the same turn.
            .WithHandler<SubscribeRequest>((_, delivery) =>
            {
                Interlocked.Increment(ref subscribeRequests);
                return delivery;
            })
            .AddData(data =>
            {
                data.Workspace.Hub.TypeRegistry.WithType(typeof(MeshNode), nameof(MeshNode));
                return data
                    .Configure(reduce => reduce
                        .ForReducedStream<InstanceCollection>(reduced => reduced
                            .AddWorkspaceReference<MeshNodeReference, MeshNode>((current, _, _) =>
                                new ChangeItem<MeshNode>(
                                    current.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault()!,
                                    current.StreamId, current.Version)))
                        .AddWorkspaceReferenceStream<MeshNode>((workspace, reference, config) =>
                            reference is MeshNodeReference meshReference
                                ? workspace.DataContext.GetDataSourceForType(typeof(MeshNode))
                                    ?.GetStreamForPartition(null)
                                    ?.ReduceShared<InstanceCollection>(new CollectionReference(nameof(MeshNode)))
                                    ?.Reduce((WorkspaceReference<MeshNode>)meshReference, config)
                                : null))
                    // The first activation's store is EMPTY — its stream has nothing to re-assert.
                    // Every later activation loads the node, as a recycled per-node hub would.
                    .AddSource(source => source.WithType<MeshNode>(type => type
                        .WithKey(n => n.Id)
                        .WithInitialData(() => Observable.Defer(() => Observable.Return<IEnumerable<MeshNode>>(
                            Volatile.Read(ref seeded) == 1 ? [Node] : [])))));
            });

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .WithTypes(typeof(ReassertBarrierRequest), typeof(ReassertBarrierResponse), typeof(MeshNodeReference))
            .WithRequestTimeout(LongRequestTimeout)
            .AddData(data => data)
            .WithServices(services => services
                .AddSingleton<IMeshChangeFeed, TestMeshChangeFeed>()
                .Configure<SyncStreamOptions>(o =>
                {
                    o.HeartbeatInterval = NoHeartbeat;
                    o.FirstHeartbeat = NoHeartbeat;
                    o.ChangeFeedResubscribeWindow = TimeSpan.FromMilliseconds(100);
                    o.ChangeFeedStalenessGrace = TimeSpan.FromMilliseconds(100);
                    o.RecycleReAskPace = TimeSpan.FromMilliseconds(100);
                }));

    /// <summary>
    /// The owner is recycled while it holds the answer to a re-ask it accepted. The read must
    /// converge on the next activation instead of waiting for the request timeout.
    /// </summary>
    [HubFact]
    public async Task OwnerRecycledWhileHoldingAReassert_TheReadStillConverges()
    {
        var ct = TestContext.Current.CancellationToken;
        var hostAddress = CreateHostAddress();
        var host = GetHost();
        var client = GetClient();
        var workspace = (Workspace)client.GetWorkspace();
        var changeFeed = client.ServiceProvider.GetRequiredService<IMeshChangeFeed>();

        // 1. Open the read. The first activation's store is empty, so the stream has no node.
        var stream = workspace.GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
            hostAddress, new MeshNodeReference());
        var nodes = stream.Where(c => c.Value is not null).Select(c => c.Value!).Replay(1);
        using var connection = nodes.Connect();

        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref subscribeRequests))
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n >= 1, "the initial SubscribeRequest reaches the owner");
        await client.Observe<ReassertBarrierResponse>(new ReassertBarrierRequest(), o => o.WithTarget(hostAddress))
            .Should().Within(TestTimeouts.Convergence).Emit("the owner has served the initial subscribe");

        // 2. The change feed announces a version the read has not received: the subscriber re-asks.
        //    The owner already serves this stream and has nothing to re-assert, so it HOLDS the answer.
        changeFeed.Publish(new MeshChangeEvent(
            Namespace: HostType, Id: "1", Path: hostAddress.Path, Kind: MeshChangeKind.Updated,
            NodeType: null, Version: 5, Timestamp: DateTimeOffset.UtcNow));
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref subscribeRequests))
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n >= 2, "the change feed makes the subscriber re-ask the owner");

        // 3. The next activation will load the node; recycle this one by a self-posted
        //    DisposeRequest, queued behind the re-ask it has already handled.
        Volatile.Write(ref seeded, 1);
        var teardown = host.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.CrossSilo).Await(ct);
        host.Post(new DisposeRequest { Reason = "test: recycle while a re-assert is held" },
            o => o.WithTarget(hostAddress));
        await teardown;
        Output.WriteLine($"DIAG SubscribeRequests before the recycle completed: {Volatile.Read(ref subscribeRequests)}");

        // 4. The read must converge on the fresh activation. Before the fix nothing ever answers the
        //    held re-ask, so its in-flight guard drops every later re-ask and nothing arrives.
        var node = await nodes
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the owner was recycled while holding the answer to a re-ask it accepted. It must "
                  + "refuse that re-ask as its stream is disposed (transient ShuttingDown), so the "
                  + "subscriber re-asks the fresh activation. Timing out here is #6759: the held "
                  + "answer was dropped silently and the stranded re-ask blocks every later one");
        node.Name.Should().Be("loaded");
        Output.WriteLine($"DIAG SubscribeRequests in total: {Volatile.Read(ref subscribeRequests)}");
    }

    /// <summary>
    /// The whole tree is torn down while the owner holds the answer to a re-ask (#6805 review). The
    /// owner AND its parent are past <see cref="MessageHubRunLevel.DisposeHostedHubs"/> when the
    /// owner's stream is disposed, so neither can route the refusal, while the subscriber — a sibling
    /// in the same process — is <see cref="MessageHubRunLevel.Quiescing"/> on exactly that callback.
    /// The refusal must still reach it, in-process (<c>MessageHub.TryDeliverNackInProcess</c>, #4072),
    /// so its teardown completes instead of waiting out its quiesce budget. Before the fix the owner
    /// only logged that it had no route left.
    /// </summary>
    [HubFact]
    public async Task WholeTreeTornDownWhileHoldingAReassert_TheQuiescingSubscriberIsAnswered()
    {
        var ct = TestContext.Current.CancellationToken;
        var hostAddress = CreateHostAddress();
        var host = GetHost();
        // A quiesce budget far beyond the convergence wait: the subscriber's teardown completing
        // inside it is the refusal's arrival, never its own budget expiring.
        var client = GetClient(c => ConfigureClient(c)
            .WithPostingIdentity(PostingIdentity.System)
            .WithQuiesceTimeout(TimeSpan.FromMinutes(5)));
        var workspace = (Workspace)client.GetWorkspace();
        var changeFeed = client.ServiceProvider.GetRequiredService<IMeshChangeFeed>();

        // 1. Open the read against an owner whose store is empty, and let it serve the subscribe.
        var stream = workspace.GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
            hostAddress, new MeshNodeReference());
        using var reading = stream.Subscribe(_ => { }, _ => { });
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref subscribeRequests))
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n >= 1, "the initial SubscribeRequest reaches the owner");
        await client.Observe<ReassertBarrierResponse>(new ReassertBarrierRequest(), o => o.WithTarget(hostAddress))
            .Should().Within(TestTimeouts.Convergence).Emit("the owner has served the initial subscribe");

        // 2. The subscriber re-asks; the owner has nothing to re-assert and HOLDS the answer.
        changeFeed.Publish(new MeshChangeEvent(
            Namespace: HostType, Id: "1", Path: hostAddress.Path, Kind: MeshChangeKind.Updated,
            NodeType: null, Version: 5, Timestamp: DateTimeOffset.UtcNow));
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref subscribeRequests))
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n >= 2, "the change feed makes the subscriber re-ask the owner");
        await client.Observe<ReassertBarrierResponse>(new ReassertBarrierRequest(), o => o.WithTarget(hostAddress))
            .Should().Within(TestTimeouts.Convergence).Emit("the owner has taken the re-ask and holds its answer");

        // 3. Tear the whole tree down: the parent disposes owner and subscriber together.
        var clientGone = client.DisposalCompleted.FirstAsync().Replay(1);
        using var clientGoneConnection = clientGone.Connect();
        Mesh.Dispose();

        // 4. The subscriber's teardown completes because the held re-ask was answered.
        await clientGone.Should().Within(TestTimeouts.Convergence).Emit(
            "the owner was torn down together with its parent while it held the answer to a re-ask, and "
            + "the subscriber is Quiescing on that very callback. The owner must still refuse it, through "
            + "the in-process NACK carrier; timing out here means the refusal was dropped for want of a route");
        Output.WriteLine($"DIAG after: owner {host.RunLevel}, parent {Mesh.RunLevel}, subscriber {client.RunLevel}");
    }
}
