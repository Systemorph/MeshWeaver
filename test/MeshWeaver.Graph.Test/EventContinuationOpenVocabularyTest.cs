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
/// <see cref="EventContinuationType"/> is an OPEN vocabulary (policy
/// <c>open-vocabulary-string-constants</c>): a module claims a continuation of its OWN with no change
/// to the platform, and a value nobody claims stays what it was and fails naming itself.
///
/// <para>The module here declares its constant the way a module would, and owns its continuation the
/// way in-mesh source must: through <see cref="EventSubscription.DeliverTo"/>, a node of its own
/// type whose hub receives the <see cref="EventDelivery"/>. In-mesh source cannot register a
/// root-DI <see cref="IEventContinuationHandler"/>, so before <c>DeliverTo</c> it could not own a
/// continuation at all.</para>
/// </summary>
public class EventContinuationOpenVocabularyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A module's own continuation constants — declared outside the platform.</summary>
    private static class AcmeContinuationType
    {
        public const string ActivatePlan = "AcmeActivatePlan";
    }

    /// <summary>The module's request node content: it declares the delivery field and nothing else
    /// the platform knows about.</summary>
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
    /// A module-owned continuation with a <c>DeliverTo</c> node: when the matching user signs up, the
    /// subscription fires and the module's node receives the new user's id, the continuation and the
    /// requester — and keeps every field it already had.
    ///
    /// <para>Fails without <c>DeliverTo</c>: the runner knew only DI handlers, found none for
    /// <c>AcmeActivatePlan</c>, and failed the subscription.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AModuleContinuation_IsDeliveredToItsNode_WithTheNewUsersId()
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

        using var runner = StartRunner();
        await runner.StartAsync(default);

        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.NodeChange,
            TriggerNodeType = "User",
            TriggerKind = MeshChangeKind.Created,
            MatchField = "email",
            MatchValue = InviteeEmail,
            ContinuationType = AcmeContinuationType.ActivatePlan,
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
        Assert.Equal(AcmeContinuationType.ActivatePlan, request.EventDelivery.ContinuationType);
        Assert.Equal(subscription.Id, request.EventDelivery.SubscriptionId);
        Assert.Equal("inviter", request.EventDelivery.CreatedBy);
        Assert.Equal("personal", request.Plan);
    }

    /// <summary>
    /// The negative control: the same module value with NO <c>DeliverTo</c> and no handler is not
    /// coerced into anything the platform knows. The subscription fails, and the reason names the
    /// value.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnUnclaimedValue_StaysUnknown_AndFailsNamingItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            ContinuationType = AcmeContinuationType.ActivatePlan,
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit(cancellationToken: ct);

        using var runner = StartRunner();
        await runner.StartAsync(default);

        var final = await Settled(subscription.Id);
        Assert.Equal(EventSubscriptionStatus.Failed, final.Status);
        Assert.Contains(AcmeContinuationType.ActivatePlan, final.LastError ?? "");
        Assert.Equal(AcmeContinuationType.ActivatePlan, final.ContinuationType);
    }

    /// <summary>
    /// Stored subscriptions keep reading. The enum was written as its NAME, which is the constant's
    /// value; a value stored as the enum's NUMBER reads as the name it stood for; and a number the
    /// enum never had stays its digits instead of becoming the old zero member.
    /// </summary>
    [Fact]
    public void StoredValues_ReadAsTheirNames_AndAnUnknownNumberStaysUnknown()
    {
        var options = Mesh.JsonSerializerOptions;
        EventSubscription Read(string value) =>
            JsonSerializer.Deserialize<EventSubscription>($$"""{"id":"s","continuationType":{{value}}}""", options)!;

        Assert.Equal(EventContinuationType.AddToGroup, Read("\"AddToGroup\"").ContinuationType);
        Assert.Equal(EventContinuationType.AddToGroup, Read("2").ContinuationType);
        Assert.Equal(EventContinuationType.GrantSpaceAccess, Read("0").ContinuationType);
        Assert.Equal("7", Read("7").ContinuationType);
        Assert.Equal(AcmeContinuationType.ActivatePlan, Read($"\"{AcmeContinuationType.ActivatePlan}\"").ContinuationType);

        var written = JsonSerializer.Serialize(new EventSubscription { Id = "s", ContinuationType = EventContinuationType.RunScript }, options);
        Assert.Contains("\"RunScript\"", written);
    }

    private EventSubscriptionRunner StartRunner() => new(
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
}
