using System.Reactive.Linq;
using System.Text.Json;
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
/// <see cref="EventContinuationType.DeliverToNode"/>: the ONE generic continuation through which a
/// module — in-mesh source included, which cannot register a root-DI
/// <see cref="IEventContinuationHandler"/> — owns what a firing does. The runner writes an
/// <see cref="EventDelivery"/> onto the node named by <see cref="EventSubscription.DeliverTo"/>, and
/// that node's own hub acts on it.
///
/// <para>Also pinned: the member was APPENDED (every earlier member keeps its number, so a module
/// compiled against an earlier platform binds unchanged), and a value this build does not know stays
/// unknown — it fails naming itself, it is never read as one of the continuations that mean
/// something.</para>
/// </summary>
public class EventContinuationDeliverToNodeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A module's request node content: it declares the delivery field and nothing else the
    /// platform knows about.</summary>
    public record AcmeRequestContent
    {
        public string Plan { get; init; } = "";
        public EventDelivery? EventDelivery { get; init; }
    }

    private const string RequestPath = "AcmeRequests/plan-for-newcomer";
    private const string InviteeEmail = "planned@acme.com";
    private const string InviteeId = "planned";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode("AcmeRequests") { Name = "Acme requests", NodeType = "Markdown" })
            .AddMeshNodes(new MeshNode("AcmeRequest")
            {
                Name = "Acme request",
                HubConfiguration = c => c.AddMeshDataSource(s => s.WithContentType<AcmeRequestContent>()),
            });

    /// <summary>
    /// When the matching user signs up, a <c>DeliverToNode</c> subscription fires and the module's
    /// node receives the new user's id and the requester, keeping every field it already had.
    /// Fails without the runner's <c>DeliverToNode</c> arm: the value falls to the handler lookup,
    /// finds none, and the subscription ends Failed.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task DeliverToNode_WritesTheNewUsersId_OntoTheModulesNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode("plan-for-newcomer", "AcmeRequests")
            {
                NodeType = "AcmeRequest",
                Name = "Plan for a newcomer",
                Content = new AcmeRequestContent { Plan = "personal" },
            }).Should().Emit(cancellationToken: ct);

        using var runner = NewRunner();
        await runner.StartAsync(default);

        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.NodeChange,
            TriggerNodeType = "User",
            TriggerKind = MeshChangeKind.Created,
            MatchField = "email",
            MatchValue = InviteeEmail,
            ContinuationType = EventContinuationType.DeliverToNode,
            DeliverTo = RequestPath,
            CreatedBy = "inviter",
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit(cancellationToken: ct);

        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(InviteeId)
            {
                NodeType = "User",
                Name = "Planned",
                Content = new User { Email = InviteeEmail, FullName = "Planned" },
            }).Should().Emit(cancellationToken: ct);

        var final = await Settled(subscription.Id);
        Assert.True(final.Status == EventSubscriptionStatus.Fired, $"subscription ended {final.Status}: {final.LastError}");

        var request = (await Mesh.GetWorkspace().GetMeshNodeStream(RequestPath)
            .Select(n => n?.ContentAs<AcmeRequestContent>(Mesh.JsonSerializerOptions))
            .Where(c => c?.EventDelivery is not null)
            .FirstAsync().Timeout(20.Seconds()).Await())!;
        Assert.Equal(InviteeId, request.EventDelivery!.SubjectId);
        Assert.Equal(subscription.Id, request.EventDelivery.SubscriptionId);
        Assert.Equal("inviter", request.EventDelivery.CreatedBy);
        Assert.Equal("personal", request.Plan);
    }

    /// <summary><c>DeliverToNode</c> with no node to deliver to fails, naming why — never Fired.</summary>
    [Fact(Timeout = 60000)]
    public async Task DeliverToNode_WithoutANode_FailsNamingIt()
    {
        var subscription = await ArmDueTimer(EventContinuationType.DeliverToNode, TestContext.Current.CancellationToken);
        var final = await Settled(subscription.Id);
        Assert.Equal(EventSubscriptionStatus.Failed, final.Status);
        Assert.Contains("names no DeliverTo node", final.LastError ?? "");
    }

    /// <summary>
    /// The negative control for "unknown stays unknown": a continuation number this build has never
    /// heard of (a newer peer wrote it) is not run as any continuation that means something. The
    /// subscription fails, and the reason names the value.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnUnknownValue_FailsNamingItself()
    {
        var subscription = await ArmDueTimer((EventContinuationType)99, TestContext.Current.CancellationToken);
        var final = await Settled(subscription.Id);
        Assert.Equal(EventSubscriptionStatus.Failed, final.Status);
        Assert.Contains("99", final.LastError ?? "");
    }

    /// <summary>
    /// The member is APPENDED: every earlier member keeps its number, so a module compiled against an
    /// earlier platform — which bakes the numbers into its IL — binds unchanged, and stored values
    /// read back as they were written.
    /// </summary>
    [Fact]
    public void TheMemberIsAppended_EveryEarlierNumberIsUnchanged()
    {
        Assert.Equal(0, (int)EventContinuationType.GrantSpaceAccess);
        Assert.Equal(1, (int)EventContinuationType.PostThreadMessage);
        Assert.Equal(2, (int)EventContinuationType.AddToGroup);
        Assert.Equal(3, (int)EventContinuationType.PublishSocialPost);
        Assert.Equal(4, (int)EventContinuationType.RunScript);
        Assert.Equal(5, (int)EventContinuationType.DeliverToNode);

        var written = JsonSerializer.Serialize(
            new EventSubscription { Id = "s", ContinuationType = EventContinuationType.DeliverToNode }, Mesh.JsonSerializerOptions);
        Assert.Contains("\"DeliverToNode\"", written);
        var read = JsonSerializer.Deserialize<EventSubscription>(written, Mesh.JsonSerializerOptions)!;
        Assert.Equal(EventContinuationType.DeliverToNode, read.ContinuationType);
    }

    /// <summary>
    /// A delivery never REPLACES content it cannot extend: a scalar or an array is refused (the
    /// subscription then fails naming why) instead of being overwritten by an object holding only the
    /// delivery. An object keeps every field; no content starts empty.
    /// </summary>
    [Fact]
    public void WithDelivery_KeepsAnObject_AndRefusesContentItWouldReplace()
    {
        var options = Mesh.JsonSerializerOptions;
        var delivery = new EventDelivery { SubscriptionId = "s", SubjectId = "u" };
        var field = EventSubscriptionOps.DeliveryField(options);

        var kept = EventSubscriptionOps.WithDelivery(JsonDocument.Parse("""{"plan":"personal"}""").RootElement, delivery, options);
        Assert.Equal("personal", (string?)kept["plan"]);
        Assert.NotNull(kept[field]);
        Assert.NotNull(EventSubscriptionOps.WithDelivery(null, delivery, options)[field]);

        foreach (var raw in new[] { "\"text\"", "42", "[1,2]" })
        {
            var refused = Assert.Throws<InvalidOperationException>(() =>
                EventSubscriptionOps.WithDelivery(JsonDocument.Parse(raw).RootElement, delivery, options));
            Assert.Contains("not an object", refused.Message);
        }
    }

    /// <summary>A whitespace-only <c>DeliverTo</c> names no node: it fails like a missing one instead
    /// of trimming to an empty path, which the stream API would read as the runner's own hub.</summary>
    [Fact(Timeout = 60000)]
    public async Task DeliverToNode_WithAWhitespacePath_FailsNamingIt()
    {
        var subscription = await ArmDueTimer(EventContinuationType.DeliverToNode, TestContext.Current.CancellationToken, deliverTo: "   ");
        var final = await Settled(subscription.Id);
        Assert.Equal(EventSubscriptionStatus.Failed, final.Status);
        Assert.Contains("names no DeliverTo node", final.LastError ?? "");
    }

    private async Task<EventSubscription> ArmDueTimer(EventContinuationType continuation, CancellationToken ct, string? deliverTo = null)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            ContinuationType = continuation,
            DeliverTo = deliverTo,
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription)
            .Should().Emit(cancellationToken: ct);
        var runner = NewRunner();
        runners.Add(runner);
        await runner.StartAsync(default);
        return subscription;
    }

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

    private readonly List<EventSubscriptionRunner> runners = [];

    public override async ValueTask DisposeAsync()
    {
        foreach (var runner in runners)
            runner.Dispose();
        await base.DisposeAsync();
    }
}
