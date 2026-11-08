using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="EventSubscription.TriggerPath"/>: a <see cref="EventTriggerType.NodeChange"/> subscription
/// that fires on the <see cref="MeshChangeKind.Deleted"/> of ONE exact path — with no node type (a delete
/// event carries none) and without reading the deleted node back. Pinned here: the exact path delivers;
/// a sibling, a string-prefix and a descendant do not; a delete that happened while no runner was
/// running still delivers when one starts; and a path that still exists never delivers.
/// </summary>
public class EventSubscriptionExactPathDeletionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The receiver's content: it declares the delivery field and nothing else the platform knows.</summary>
    public record ReceiverContent
    {
        public string Label { get; init; } = "";
        public EventDelivery? EventDelivery { get; init; }
    }

    private const string Records = "Records";
    private const string Receivers = "Receivers";
    /// <summary>A node the read validator below hides from every reader, System included.</summary>
    private const string HiddenRecord = Records + "/hidden";

    /// <summary>A custom read validator hiding <see cref="HiddenRecord"/> — the shape a policy filter has:
    /// it runs on the owner over a node that EXISTS, for System as for anyone.</summary>
    private sealed class HidingValidator : INodeValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];
        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => Observable.Return(string.Equals(context.Node?.Path, HiddenRecord, StringComparison.Ordinal)
                ? NodeValidationResult.Invalid("hidden by the test policy")
                : NodeValidationResult.Valid());
    }

    /// <summary>A change feed that never delivers — so only the change-feed-INDEPENDENT paths can fire.</summary>
    private sealed class SilentChangeFeed : IMeshChangeFeed
    {
        public void Publish(MeshChangeEvent change) { }
        public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
            => System.Reactive.Disposables.Disposable.Empty;
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureServices(services => services.AddSingleton<INodeValidator>(new HidingValidator()))
            .AddMeshNodes(new MeshNode(Records) { Name = "Records", NodeType = "Markdown" })
            .AddMeshNodes(new MeshNode(Receivers) { Name = "Receivers", NodeType = "Markdown" })
            .AddMeshNodes(new MeshNode("DeletionReceiver")
            {
                Name = "Deletion receiver",
                HubConfiguration = c => c.AddMeshDataSource(s => s.WithContentType<ReceiverContent>()),
            });

    /// <summary>
    /// Deleting the exact path delivers to the receiver, with the deleted node's id as the subject, and
    /// the subscription ends Fired. Fails without the exact-path arm of <c>OnChange</c>: the delete event
    /// carries no node type, so the type match never selects the subscription and it stays Pending.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task DeletingTheExactPath_Delivers()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRecord("sub-1", ct);
        await CreateReceiver("owner-1", ct);

        using var runner = NewRunner();
        await runner.StartAsync(default);

        var subscription = await Subscribe($"{Records}/sub-1", $"{Receivers}/owner-1", ct);
        await PendingSeen(runner, subscription.Id);

        await Delete($"{Records}/sub-1", ct);

        var final = await Settled(subscription.Id);
        Assert.True(final.Status == EventSubscriptionStatus.Fired, $"subscription ended {final.Status}: {final.LastError}");
        var delivery = await Delivered($"{Receivers}/owner-1");
        Assert.Equal("sub-1", delivery.SubjectId);
        Assert.Equal(subscription.Id, delivery.SubscriptionId);
        Assert.Equal("store-admin", delivery.CreatedBy);
    }

    /// <summary>
    /// Deleting a SIBLING, a string-PREFIX of the path, or a DESCENDANT of it does not fire the exact-path
    /// subscription. The positive settled signal is a second subscription on each of those paths: it
    /// fires off the very same delete event, so once it is Fired the runner has demonstrably evaluated
    /// that event against every pending subscription — and the guarded one is still Pending.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task DeletingASiblingPrefixOrDescendant_DoesNotDeliver()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRecord("keep", ct);
        await CreateRecord("kee", ct);                       // a string prefix of "keep"
        await CreateRecord("keep-old", ct);                  // "keep" is a string prefix of it
        await CreateRecord("other", ct);                     // a sibling
        await CreateRecord("child", ct, $"{Records}/keep");  // a descendant
        await CreateReceiver("guarded", ct);
        await CreateReceiver("witness", ct);

        using var runner = NewRunner();
        await runner.StartAsync(default);

        var guarded = await Subscribe($"{Records}/keep", $"{Receivers}/guarded", ct);
        await PendingSeen(runner, guarded.Id);

        foreach (var path in new[] { $"{Records}/kee", $"{Records}/keep-old", $"{Records}/other", $"{Records}/keep/child" })
        {
            var witness = await Subscribe(path, $"{Receivers}/witness", ct);
            await PendingSeen(runner, witness.Id);
            await Delete(path, ct);
            var settled = await Settled(witness.Id);
            Assert.True(settled.Status == EventSubscriptionStatus.Fired,
                $"witness on {path} ended {settled.Status}: {settled.LastError}");
            Assert.Equal(EventSubscriptionStatus.Pending, (await Current(guarded.Id)).Status);
        }

        var receiver = await Mesh.GetWorkspace().GetMeshNodeStream($"{Receivers}/guarded")
            .Select(n => n?.ContentAs<ReceiverContent>(Mesh.JsonSerializerOptions))
            .Where(c => c is not null)
            .FirstAsync().Timeout(10.Seconds()).Await();
        Assert.Null(receiver!.EventDelivery);
    }

    /// <summary>
    /// The restart case: the record is deleted while NO runner is running, so nobody hears the delete
    /// event. A runner that starts afterwards finds the path gone in its startup reconcile and delivers.
    /// Fails without <c>ReconcileDeletedPaths</c> on startup: the only evidence of the delete left is
    /// that the path is gone, and nothing else looks.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ADeleteWhileNoRunnerRan_DeliversWhenOneStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRecord("sub-2", ct);
        await CreateReceiver("owner-2", ct);
        var subscription = await Subscribe($"{Records}/sub-2", $"{Receivers}/owner-2", ct);

        await Delete($"{Records}/sub-2", ct);   // no runner listening

        using var runner = NewRunner();
        await runner.StartAsync(default);

        var final = await Settled(subscription.Id);
        Assert.True(final.Status == EventSubscriptionStatus.Fired, $"subscription ended {final.Status}: {final.LastError}");
        Assert.Equal("sub-2", (await Delivered($"{Receivers}/owner-2")).SubjectId);
    }

    /// <summary>
    /// The negative control for the startup reconcile: the record still EXISTS, so a starting runner
    /// must not fire. The settled signal is a second subscription, written in the same pass, whose path
    /// IS gone — the startup reconcile fires it, so by then the guarded one has been checked too.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task APathThatStillExists_NeverDelivers()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRecord("alive", ct);
        await CreateRecord("gone", ct);
        await CreateReceiver("owner-3", ct);
        await CreateReceiver("witness-3", ct);
        var guarded = await Subscribe($"{Records}/alive", $"{Receivers}/owner-3", ct);
        var witness = await Subscribe($"{Records}/gone", $"{Receivers}/witness-3", ct);
        await Delete($"{Records}/gone", ct);

        using var runner = NewRunner();
        await runner.StartAsync(default);

        var settled = await Settled(witness.Id);
        Assert.True(settled.Status == EventSubscriptionStatus.Fired, $"witness ended {settled.Status}: {settled.LastError}");
        // The witness and the guarded subscription are checked in the same pass, but the guarded one's
        // terminal write (were it to fire) can land just after the witness's — so the guarded one must
        // stay Pending over a window, not merely at one read.
        await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(guarded.Id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .Should().NotEmit(5.Seconds(), "the record still exists", ct);
    }

    /// <summary>A path with a kind other than Deleted is not read as a type trigger with no type: it ends
    /// Failed, naming why.</summary>
    [Fact(Timeout = 60000)]
    public async Task APathWithAnotherKind_FailsNamingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.NodeChange,
            TriggerKind = MeshChangeKind.Created,
            TriggerPath = $"{Records}/whatever",
            ContinuationType = EventContinuationType.DeliverToNode,
            DeliverTo = $"{Receivers}/nobody",
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit(cancellationToken: ct);

        using var runner = NewRunner();
        await runner.StartAsync(default);

        var final = await Settled(subscription.Id);
        Assert.Equal(EventSubscriptionStatus.Failed, final.Status);
        Assert.Contains("honoured for Deleted only", final.LastError ?? "");
    }

    /// <summary>
    /// A node a read validator HIDES is not a deleted node. A Deleted event for it (a stale or spurious
    /// hint) hands it to the authoritative read, which answers Absent — that is what hidden reads as —
    /// but with the validator's verdict on <see cref="NodeReadOutcome.HiddenBy"/>. Fails without the
    /// verdict check: the read said Absent, so the subscription fired for a node that still exists.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AHiddenButExistingPath_NeverDelivers()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRecord("hidden", ct);
        await CreateReceiver("owner-h", ct);

        using var runner = NewRunner();
        await runner.StartAsync(default);
        var guarded = await Subscribe(HiddenRecord, $"{Receivers}/owner-h", ct);
        await PendingSeen(runner, guarded.Id);

        Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>().Publish(MeshChangeEvent.Deleted(HiddenRecord));

        await StaysPending(guarded.Id, "the record exists — a read validator only hid it", ct);
    }

    /// <summary>
    /// Several proposals for ONE deletion — the real delete's feed event, two more Deleted events for the
    /// path and the parent watch's Removed — deliver exactly once, even when the later reads would answer
    /// only after the first firing's Fired write has round-tripped and pruned the in-flight reservation.
    /// The reads are held by the test seam: the first is released, the subscription is seen Fired and out
    /// of the pending set, then the rest are released and every one is seen to answer before the
    /// deliveries are counted. Fails without the per-subscription confirm
    /// reservation and the fire-time re-read: each held read answers Absent and delivers again.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task OverlappingDeleteProposals_DeliverExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRecord("twice", ct);
        await CreateReceiver("owner-t", ct);
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var first = new AsyncSubject<Unit>();
        var rest = new AsyncSubject<Unit>();
        var calls = 0;
        var answered = 0;

        using var runner = NewRunner();
        runner.ReadPathOverride = path => Observable.Defer(() =>
            (Interlocked.Increment(ref calls) == 1 ? first : rest)
                .SelectMany(_ => RealRead(path))
                .Do(_ => Interlocked.Increment(ref answered)));
        await runner.StartAsync(default);
        var subscription = await Subscribe($"{Records}/twice", $"{Receivers}/owner-t", ct);
        await PendingSeen(runner, subscription.Id);

        await Delete($"{Records}/twice", ct);
        feed.Publish(MeshChangeEvent.Deleted($"{Records}/twice"));
        feed.Publish(MeshChangeEvent.Deleted($"{Records}/twice"));
        await Until(() => Volatile.Read(ref calls) >= 1);

        Release(first);
        var final = await Settled(subscription.Id);
        Assert.True(final.Status == EventSubscriptionStatus.Fired, $"subscription ended {final.Status}: {final.LastError}");
        await Until(() => runner.PendingSnapshot.All(s => s.Id != subscription.Id));
        var firstDelivery = await Delivered($"{Receivers}/owner-t");

        // Collect from BEFORE the held reads are released: the stream replays only the latest value, so
        // a second delivery that landed before a later subscription would be counted as one.
        var deliveries = Mesh.GetWorkspace().GetMeshNodeStream($"{Receivers}/owner-t")
            .Select(n => n?.ContentAs<ReceiverContent>(Mesh.JsonSerializerOptions)?.EventDelivery?.FiredAt)
            .Where(at => at is not null)
            .Distinct()
            .TakeUntil(Observable.Timer(10.Seconds()))
            .ToList()
            .Replay();
        using var collecting = deliveries.Connect();

        Release(rest);
        // Every read the runner issued has answered — so whatever it was going to deliver off them has
        // been handed to Execute — before the window closes.
        await Until(() => Volatile.Read(ref answered) >= Volatile.Read(ref calls));

        var seen = await deliveries.Await();
        Assert.True(seen.Count == 1 && seen[0] == firstDelivery.FiredAt,
            $"saw {seen.Count} distinct delivery time(s), the first being {firstDelivery.FiredAt:O}; {Volatile.Read(ref calls)} read(s) issued");
    }

    /// <summary>
    /// A runner stopped while its confirmation read is in flight delivers nothing when that read later
    /// answers Absent. Fails while the read's subscription is discarded rather than owned: the reply
    /// still reaches the callback, and Execute (whose reservation the stop cleared) delivers.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task StoppingTheRunnerDuringTheRead_DeliversNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRecord("stopped", ct);
        await CreateReceiver("owner-s", ct);
        var held = new AsyncSubject<Unit>();
        var calls = 0;

        var runner = NewRunner();
        runner.ReadPathOverride = path => Observable.Defer(() =>
        {
            Interlocked.Increment(ref calls);
            return held.SelectMany(_ => RealRead(path));
        });
        await runner.StartAsync(default);
        var subscription = await Subscribe($"{Records}/stopped", $"{Receivers}/owner-s", ct);
        await PendingSeen(runner, subscription.Id);

        await Delete($"{Records}/stopped", ct);
        await Until(() => Volatile.Read(ref calls) >= 1);
        await runner.StopAsync(default);
        Release(held);

        await StaysPending(subscription.Id, "the runner was stopped before its read answered", ct);
        var receiver = await Mesh.GetWorkspace().GetMeshNodeStream($"{Receivers}/owner-s")
            .Select(n => n?.ContentAs<ReceiverContent>(Mesh.JsonSerializerOptions))
            .Where(c => c is not null)
            .FirstAsync().Timeout(10.Seconds()).Await();
        Assert.Null(receiver!.EventDelivery);
    }

    /// <summary>
    /// A TOP-LEVEL path (no parent to list) whose delete the change feed drops still delivers: it is
    /// watched by an anchored exact-scope listing of the path itself. The catch-up has demonstrably seen
    /// the path exist before the delete (its listing is observed through the seam), so only the live
    /// watch can fire it. Fails without that watch: nothing hears the delete until a restart.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ATopLevelPath_DeliversWhenTheFeedDropsTheDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var top = $"esdtop{Guid.NewGuid():N}"[..14];
        await CreateRecord(top, ct, ns: "");
        await CreateReceiver("owner-top", ct);

        using var runner = NewRunner(new SilentChangeFeed());
        var listed = CatchUpListed(runner, top, gateWatch: null);
        await runner.StartAsync(default);
        var subscription = await Subscribe(top, $"{Receivers}/owner-top", ct);
        await PendingSeen(runner, subscription.Id);
        await listed.FirstAsync().Timeout(20.Seconds()).Await();

        await Delete(top, ct);

        var final = await Settled(subscription.Id);
        Assert.True(final.Status == EventSubscriptionStatus.Fired, $"subscription ended {final.Status}: {final.LastError}");
        Assert.Equal(top, (await Delivered($"{Receivers}/owner-top")).SubjectId);
    }

    /// <summary>
    /// A delete that lands AFTER the catch-up saw the path but BEFORE the live watch opened emits no
    /// Removed — the watch's first snapshot simply lacks it — and the change feed drops it. The watch
    /// must read its Initial snapshot: a pending path missing from it is gone. The seam holds the
    /// watch's query until the delete has committed. Fails while the watch reacts to Removed only.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ADeleteBeforeTheWatchOpens_StillDelivers()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRecord("early", ct);
        await CreateReceiver("owner-e", ct);
        var watchGate = new AsyncSubject<Unit>();

        using var runner = NewRunner(new SilentChangeFeed());
        var listed = CatchUpListed(runner, $"{Records}/early", watchGate);
        await runner.StartAsync(default);
        var subscription = await Subscribe($"{Records}/early", $"{Receivers}/owner-e", ct);
        await PendingSeen(runner, subscription.Id);
        await listed.FirstAsync().Timeout(20.Seconds()).Await();

        await Delete($"{Records}/early", ct);
        Release(watchGate);

        var final = await Settled(subscription.Id);
        Assert.True(final.Status == EventSubscriptionStatus.Fired, $"subscription ended {final.Status}: {final.LastError}");
        Assert.Equal("early", (await Delivered($"{Receivers}/owner-e")).SubjectId);
    }

    /// <summary>
    /// A parent path with a SPACE in it is written into the scope listing as a quoted literal, so the
    /// listing reads that parent and not a text search for the second word. With the feed silent, the
    /// listing is what sees the path exist (catch-up) and then go (watch). Fails with the parent spliced
    /// in raw: the listing never shows the path, so its catch-up signal never comes.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AParentWithASpace_IsListedAsOnePath()
    {
        var ct = TestContext.Current.CancellationToken;
        const string parent = Records + "/with space";
        await CreateRecord("spaced", ct, ns: parent);
        await CreateReceiver("owner-sp", ct);

        using var runner = NewRunner(new SilentChangeFeed());
        var listed = CatchUpListed(runner, $"{parent}/spaced", gateWatch: null);
        await runner.StartAsync(default);
        var subscription = await Subscribe($"{parent}/spaced", $"{Receivers}/owner-sp", ct);
        await PendingSeen(runner, subscription.Id);
        await listed.FirstAsync().Timeout(20.Seconds()).Await();

        await Delete($"{parent}/spaced", ct);

        var final = await Settled(subscription.Id);
        Assert.True(final.Status == EventSubscriptionStatus.Fired, $"subscription ended {final.Status}: {final.LastError}");
    }

    /// <summary>A TriggerPath that is empty once normalised (whitespace, only slashes) is not read as a
    /// type trigger with no type: it ends Failed, naming why.</summary>
    [Fact(Timeout = 60000)]
    public async Task APathThatNormalisesToNothing_FailsNamingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.NodeChange,
            TriggerKind = MeshChangeKind.Deleted,
            TriggerPath = " // ",
            ContinuationType = EventContinuationType.DeliverToNode,
            DeliverTo = $"{Receivers}/nobody",
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit(cancellationToken: ct);

        using var runner = NewRunner();
        await runner.StartAsync(default);

        var final = await Settled(subscription.Id);
        Assert.Equal(EventSubscriptionStatus.Failed, final.Status);
        Assert.Contains("not a node path", final.LastError ?? "");
    }

    /// <summary>The real authoritative read the seam forwards to, as System (what the runner does).</summary>
    private IObservable<NodeReadOutcome> RealRead(string path)
        => Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeOutcome(path, TimeSpan.FromSeconds(10), ReadTimeoutBehavior.EmitNull));

    /// <summary>
    /// Routes the runner's deletion listings through the real query, and returns a signal that fires once
    /// a CATCH-UP listing has shown <paramref name="path"/> — the point after which the catch-up has seen
    /// it exist and issues no read for it. When <paramref name="gateWatch"/> is given, the live WATCH's
    /// query is not opened until it is released.
    /// </summary>
    private IObservable<Unit> CatchUpListed(EventSubscriptionRunner runner, string path, AsyncSubject<Unit>? gateWatch)
    {
        var seen = new ReplaySubject<Unit>(1);
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        IObservable<QueryResultChange<MeshNode>> Real(string query)
            => access.RunAsSystem(() => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query)));
        runner.OpenMeshQueryOverride = (purpose, query) => purpose == EventSubscriptionRunner.CatchUpPurpose
            ? Real(query).Do(c =>
            {
                if (c.ChangeType == QueryChangeType.Initial && (c.Items ?? []).Any(n => n.Path == path))
                    seen.OnNext(Unit.Default);
            })
            : gateWatch is null ? Real(query) : gateWatch.SelectMany(_ => Real(query));
        return seen;
    }

    private static void Release(AsyncSubject<Unit> gate)
    {
        gate.OnNext(Unit.Default);
        gate.OnCompleted();
    }

    private static async Task Until(Func<bool> condition)
        => await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Where(_ => condition())
            .FirstAsync().Timeout(20.Seconds()).Await();

    /// <summary>The subscription stays Pending over a window — not merely at one read, because a firing's
    /// terminal write can land a moment after any single look.</summary>
    private async Task StaysPending(string id, string because, CancellationToken ct)
        => await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .Should().NotEmit(5.Seconds(), because, ct);

    private async Task CreateRecord(string id, CancellationToken ct, string ns = Records)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(id, ns) { NodeType = "Markdown", Name = id })
                .Should().Emit(cancellationToken: ct);
    }

    private async Task CreateReceiver(string id, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(id, Receivers)
            {
                NodeType = "DeletionReceiver",
                Name = id,
                Content = new ReceiverContent { Label = id },
            }).Should().Emit(cancellationToken: ct);
    }

    private async Task Delete(string path, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.ImpersonateAsSystem())
            await meshService.DeleteNode(path).Should().Emit(cancellationToken: ct);
    }

    private async Task<EventSubscription> Subscribe(string triggerPath, string deliverTo, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.NodeChange,
            TriggerKind = MeshChangeKind.Deleted,
            TriggerPath = triggerPath,
            ContinuationType = EventContinuationType.DeliverToNode,
            DeliverTo = deliverTo,
            CreatedBy = "store-admin",
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit(cancellationToken: ct);
        return subscription;
    }

    /// <summary>Waits until the runner's live pending set lists the subscription — the point after which
    /// a delete event is evaluated against it.</summary>
    private static async Task PendingSeen(EventSubscriptionRunner runner, string id)
        => await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Where(_ => runner.PendingSnapshot.Any(s => s.Id == id))
            .FirstAsync().Timeout(20.Seconds()).Await();

    private EventSubscriptionRunner NewRunner(IMeshChangeFeed? feed = null) => new(
        Mesh,
        feed ?? Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>(),
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh.ServiceProvider.GetRequiredService<AccessService>(),
        Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());

    private async Task<EventSubscription> Settled(string id) =>
        (await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds()).Await())!;

    private async Task<EventSubscription> Current(string id) =>
        (await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null)
            .FirstAsync().Timeout(10.Seconds()).Await())!;

    private async Task<EventDelivery> Delivered(string receiverPath) =>
        (await Mesh.GetWorkspace().GetMeshNodeStream(receiverPath)
            .Select(n => n?.ContentAs<ReceiverContent>(Mesh.JsonSerializerOptions))
            .Where(c => c?.EventDelivery is not null)
            .FirstAsync().Timeout(20.Seconds()).Await())!.EventDelivery!;
}
