using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Durable streams = saved mesh nodes, consumed inside hubs (Doc/Architecture/DurableStreams).
///
/// <para>Every case runs on a real Monolith mesh: the items are item NODES, the lease and the
/// checkpoint are fields of the stream NODE, and the consumer is a hub. The cases pin the contract
/// the platform builds on — exactly-once relative to acknowledged items across a recycle of the
/// consumer, across a recycle of the stream's own hub, across a takeover from a subscriber that
/// died, and between two subscribers racing for one stream — plus the orphan event.</para>
/// </summary>
public class DurableStreamConsumerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ConsumerType = "DurableStreamConsumerProbe";
    private const string Family = "ProbeStream";

    /// <summary>The probe payload.</summary>
    /// <param name="Value">The value published (equals the expected sequence).</param>
    public sealed record ProbeEvent(int Value);

    private readonly ProcessedLog processed = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddMeshNodes(new MeshNode(ConsumerType)
        {
            Name = "Durable stream consumer probe",
            HubConfiguration = config => config
                .AddMeshDataSource()
                .WithType(typeof(ProbeEvent), nameof(ProbeEvent))
                .WithDurableStreamConsumer<ProbeEvent>(
                    Family,
                    hub => hub.Address.Path,
                    (hub, item) => processed.Handle(hub.Address.Path, item)),
        });

    // ── The consumer is recycled mid-stream ─────────────────────────────────────────────────────

    /// <summary>
    /// THE REGRESSION CASE: publish 40, recycle the consuming node mid-stream, publish 20 more while
    /// it is gone — every item processed exactly once, in order. Nobody re-opens the node: the
    /// appends to the orphaned stream WAKE it.
    /// </summary>
    [Fact]
    public async Task RecycleOfTheConsumerMidStream_EveryItemIsProcessedExactlyOnceInOrder()
    {
        var owner = await Seed("probe-recycle", ConsumerType);
        await Activate(owner);
        await PublishRange(owner, 1, 40);
        await processed.Of(owner).Should().Within(TestTimeouts.Convergence)
            .Match(l => l.Count >= 12, "the consumer is part-way through the stream when the recycle hits");

        await RequestHub.RecycleNode(owner, reason: "DurableStreamConsumerTest: recycle the consumer mid-stream")
            .Should().Within(TestTimeouts.Convergence).Emit("the consumer recycles");
        await PublishRange(owner, 41, 60);

        var log = await QuietAfter(owner, 60);
        log.Should().Equal(Sequence(1, 60),
            "every item exactly once and in order across the recycle — a missing number is an item that was lost, "
            + "a repeated one is a resume that ignored the acknowledged checkpoint");
        processed.MaxConcurrent.Should().Be(1, "items are handed over one at a time");
    }

    /// <summary>The checkpoint is a field of the stream node; every item is a saved node.</summary>
    [Fact]
    public async Task TheStreamNodeCarriesTheCheckpoint_AndEveryItemIsANode()
    {
        var owner = await Seed("probe-nodes", ConsumerType);
        await Activate(owner);
        await PublishRange(owner, 1, 5);
        await QuietAfter(owner, 5);

        var stream = await StreamState(owner).Where(s => s?.Checkpoint == 5)
            .Should().Within(TestTimeouts.Convergence).Emit("the fifth acknowledgement is recorded on the stream node");
        stream!.LastSequence.Should().Be(5);
        stream.Subscriber.Should().NotBeNull("the consuming node holds the lease while it is active");

        var items = await Items(owner).Should().Within(TestTimeouts.Convergence)
            .Match(l => l.Count == 5, "each published item is an item node");
        items.Select(n => n.NodeType).Distinct().Should().Equal(DurableStreamNodeTypes.Item);
        items.Select(n => n.Id).OrderBy(id => id).Should().Equal(Enumerable.Range(1, 5).Select(i => DurableStreamPaths.ItemId(i)));
    }

    // ── The stream's own hub is recycled ───────────────────────────────────────────────────────

    /// <summary>
    /// The stream's ORDERING hub recycles between appends: its sequence is re-derived from the item
    /// nodes, so the next append continues the sequence — no gap, no reuse — and a reader from the
    /// start reads every item back from the nodes.
    /// </summary>
    [Fact]
    public async Task RecycleOfTheStreamHub_TheSequenceContinuesFromTheNodes()
    {
        var owner = await Seed("plain-owner", "Markdown");
        var stream = new DurableStreamId(Family, owner);
        await PublishRange(owner, 1, 10);
        await RequestHub.RecycleNode(DurableStreamPaths.StreamPath(stream), reason: "DurableStreamConsumerTest: recycle the stream hub")
            .Should().Within(TestTimeouts.Convergence).Emit("the stream hub recycles");
        await PublishRange(owner, 11, 20);

        var read = await RequestHub.ReadDurable<ProbeEvent>(stream, 0)
            .Take(20).ToList()
            .Should().Within(TestTimeouts.Convergence).Emit("a reader from the start reads all twenty items from the nodes");
        read.Select(i => i.Sequence).Should().Equal(Sequence(1, 20));
        read.Select(i => i.Payload.Value).Should().Equal(Enumerable.Range(1, 20));
    }

    // ── Takeover, fencing, races, orphan ────────────────────────────────────────────────────────

    /// <summary>
    /// A subscriber DIES without releasing (no goodbye — its address simply stops answering). A new
    /// subscriber claims: the stream's hub finds the holder unreachable, grants a NEW epoch, and the
    /// new subscriber resumes strictly after what the dead one ACKNOWLEDGED. An acknowledgement
    /// carrying the dead holder's epoch is fenced.
    /// </summary>
    [Fact]
    public async Task TakeoverAfterTheSubscriberDies_ResumesAfterItsAcknowledgedItems()
    {
        var owner = await Seed("job-takeover", "Markdown");
        var stream = new DurableStreamId(Family, owner);
        await PublishRange(owner, 1, 10);

        // The dying subscriber: claims, acknowledges 1..4, then is gone without a release.
        var ghost = GetClient();
        var ghostAddress = ghost.Address.ToString();
        var grant = await Claim(ghost, stream).Should().Within(TestTimeouts.Convergence).Emit("the first claimant is granted");
        grant.Granted.Should().BeTrue();
        for (var sequence = 1; sequence <= 4; sequence++)
            (await Ack(ghost, ghostAddress, stream, grant.Epoch, sequence).Should().Emit($"ack {sequence}")).Accepted.Should().BeTrue();
        await ghost.DisposeAndJoinAsync(Output.WriteLine);

        // The successor: an ordinary consumer hub.
        var successor = GetClient(c => ConfigureClient(c).WithDurableStreamConsumer<ProbeEvent>(
            Family, _ => owner, (_, item) => processed.Handle("successor", item)));
        await PublishRange(owner, 11, 15);
        var log = await QuietAfter("successor", 15);
        log.Should().Equal(Sequence(5, 15),
            "the successor resumes strictly after the dead subscriber's acknowledged item 4 — nothing acknowledged is "
            + "processed again, nothing unacknowledged is lost");

        var state = await StreamState(owner).Where(s => s?.Subscriber == successor.Address.ToString())
            .Should().Within(TestTimeouts.Convergence).Emit("the stream node names the successor");
        state!.LeaseEpoch.Should().BeGreaterThan(grant.Epoch, "a takeover starts a new epoch");

        // The dead holder's last word arriving late (a request still in flight when it died).
        var late = GetClient();
        (await Ack(late, ghostAddress, stream, grant.Epoch, 15).Should().Emit("a stale acknowledgement is answered"))
            .Accepted.Should().BeFalse("an acknowledgement under the dead holder's epoch is FENCED");
    }

    /// <summary>
    /// Two would-be subscribers race for one stream: the stream's hub grants exactly one, and
    /// between them every item is processed exactly once.
    /// </summary>
    [Fact]
    public async Task TwoRacingSubscribers_EveryItemIsProcessedExactlyOnce()
    {
        var owner = await Seed("job-race", "Markdown");
        await PublishRange(owner, 1, 10);

        var first = GetClient(c => ConfigureClient(c).WithDurableStreamConsumer<ProbeEvent>(
            Family, _ => owner, (_, item) => processed.Handle("race", item)));
        var second = GetClient(c => ConfigureClient(c).WithDurableStreamConsumer<ProbeEvent>(
            Family, _ => owner, (_, item) => processed.Handle("race", item)));
        await PublishRange(owner, 11, 20);

        var log = await QuietAfter("race", 20);
        log.Should().Equal(Sequence(1, 20), "whoever won, every item exactly once, in order");
        processed.MaxConcurrent.Should().Be(1, "the loser processed nothing alongside the winner");
        new[] { first.Address, second.Address }.Should().HaveCount(2);
    }

    /// <summary>
    /// The ORPHAN EVENT: a subscriber that leaves releases its lease once its item in flight is done,
    /// and the stream node says so — <see cref="DurableStreamState.Subscriber"/> clears,
    /// <see cref="DurableStreamState.OrphanedAt"/> is stamped, the last subscriber is named.
    /// </summary>
    [Fact]
    public async Task ASubscriberThatLeaves_OrphansTheStream()
    {
        var owner = await Seed("job-orphan", "Markdown");
        await PublishRange(owner, 1, 3);
        var subscriber = GetClient(c => ConfigureClient(c).WithDurableStreamConsumer<ProbeEvent>(
            Family, _ => owner, (_, item) => processed.Handle("orphan", item)));
        await QuietAfter("orphan", 3);
        var address = subscriber.Address.ToString();

        await subscriber.DisposeAndJoinAsync(Output.WriteLine);

        var orphaned = await StreamState(owner).Where(s => s is { Subscriber: null, OrphanedAt: not null })
            .Should().Within(TestTimeouts.Convergence).Emit("the release orphans the stream");
        orphaned!.LastSubscriber.Should().Be(address);
        orphaned.Checkpoint.Should().Be(3, "everything it processed was acknowledged before it let go");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<long> Sequence(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => (long)i);

    private async Task<string> Seed(string id, string nodeType)
    {
        await NodeFactory
            .CreateNode(new MeshNode(id, TestPartition) { Name = id, NodeType = nodeType })
            .Should().Within(TestTimeouts.Convergence).Emit("the owner node is created",
                cancellationToken: TestContext.Current.CancellationToken);
        return $"{TestPartition}/{id}";
    }

    private Task Activate(string path)
        => RequestHub.GetMeshNode(path).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit("the consumer hub activates");

    private async Task PublishRange(string owner, int from, int to)
    {
        var stream = new DurableStreamId(Family, owner);
        for (var i = from; i <= to; i++)
        {
            var sequence = await RequestHub.PublishDurable(stream, new ProbeEvent(i))
                .Should().Within(TestTimeouts.Convergence).Emit($"item {i} is durable");
            sequence.Should().Be(i, "the stream's hub assigns contiguous sequences from 1");
        }
    }

    /// <summary>The log for <paramref name="key"/> once it reached <paramref name="last"/> and then
    /// stayed quiet for two seconds — a duplicate or a replay would show up in that window.</summary>
    private async Task<ImmutableList<long>> QuietAfter(string key, int last)
    {
        await processed.Of(key).Should().Within(TestTimeouts.Convergence)
            .Match(l => l.Contains(last), $"the consumer reaches item {last}");
        return await processed.Of(key).Throttle(TimeSpan.FromSeconds(2))
            .Should().Within(TestTimeouts.Convergence).Emit("the consumer goes quiet");
    }

    private IObservable<DurableStreamState?> StreamState(string owner)
        => Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => RequestHub.GetMeshNode(DurableStreamPaths.StreamPath(new DurableStreamId(Family, owner))))
            .Select(node => node?.ContentAs<DurableStreamState>(RequestHub.JsonSerializerOptions));

    private IObservable<IReadOnlyList<MeshNode>> Items(string owner)
        => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{DurableStreamPaths.ItemsNamespace(new DurableStreamId(Family, owner))}").Complete())
            .Scan(ImmutableDictionary<string, MeshNode>.Empty, (all, change) => change.ChangeType switch
            {
                QueryChangeType.Initial or QueryChangeType.Reset => change.Items.ToImmutableDictionary(n => n.Path, n => n),
                QueryChangeType.Removed => all.RemoveRange(change.Items.Select(n => n.Path)),
                _ => all.SetItems(change.Items.Select(n => KeyValuePair.Create(n.Path, n))),
            })
            .Select(all => (IReadOnlyList<MeshNode>)all.Values.ToList());

    private static IObservable<ClaimDurableStreamResponse> Claim(IMessageHub hub, DurableStreamId stream)
        => hub.Observe(new ClaimDurableStreamRequest(hub.Address.ToString()), o => o.WithTarget(new Address(DurableStreamPaths.StreamPath(stream))))
            .Select(d => d.Message);

    private static IObservable<AckDurableStreamResponse> Ack(IMessageHub hub, string subscriber, DurableStreamId stream, long epoch, long sequence)
        => hub.Observe(new AckDurableStreamRequest(subscriber, epoch, sequence), o => o.WithTarget(new Address(DurableStreamPaths.StreamPath(stream))))
            .Select(d => d.Message);

    /// <summary>
    /// What the probe handlers saw, keyed by consumer. Instance state of the test (never static).
    /// </summary>
    private sealed class ProcessedLog
    {
        private ImmutableDictionary<string, ImmutableList<long>> entries =
            ImmutableDictionary<string, ImmutableList<long>>.Empty;
        private readonly ReplaySubject<string> changed = new();
        private int inFlight;
        private int maxConcurrent;

        public int MaxConcurrent => Volatile.Read(ref maxConcurrent);

        /// <summary>Record the item, then take a little while to "process" it, so a recycle reliably
        /// lands while an item is in flight.</summary>
        public IObservable<Unit> Handle(string key, DurableStreamItem<ProbeEvent> item)
        {
            var now = Interlocked.Increment(ref inFlight);
            int seen;
            while ((seen = Volatile.Read(ref maxConcurrent)) < now
                   && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen) { }

            item.Payload.Value.Should().Be((int)item.Sequence, "the payload round-trips with its own sequence");
            ImmutableInterlocked.Update(ref entries, d =>
                d.SetItem(key, d.GetValueOrDefault(key, ImmutableList<long>.Empty).Add(item.Sequence)));
            changed.OnNext(key);

            return Observable.Timer(TimeSpan.FromMilliseconds(15))
                .Select(_ => Unit.Default)
                .Finally(() => Interlocked.Decrement(ref inFlight));
        }

        public ImmutableList<long> Snapshot(string key)
            => Volatile.Read(ref entries).GetValueOrDefault(key, ImmutableList<long>.Empty);

        public IObservable<ImmutableList<long>> Of(string key)
            => changed.Where(k => k == key).Select(_ => Snapshot(key)).StartWith(Snapshot(key));
    }
}
