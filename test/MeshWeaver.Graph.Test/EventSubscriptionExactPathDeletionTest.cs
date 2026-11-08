using System.Reactive.Linq;
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

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
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
        Assert.Equal(EventSubscriptionStatus.Pending, (await Current(guarded.Id)).Status);
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

    private EventSubscriptionRunner NewRunner() => new(
        Mesh,
        Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>(),
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
