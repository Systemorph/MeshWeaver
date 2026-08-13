using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins that <see cref="EventSubscription"/> — the content of the built-in <c>EventSubscription</c>
/// NodeType — is resolvable on the hub that READS it, and that
/// <see cref="EventSubscriptionRunner"/> keeps working when it is not.
///
/// <para><b>The production defect (issue #1392).</b> Every boot of the <c>memex</c> portal logged a
/// dozen lines of <c>"MeshNodeStreamCache.GetQuery: Content for
/// Admin/EventSubscription/grant_… stayed an untyped JsonElement after deserialization (TypeRegistry
/// lacks the $type discriminator)"</c>. The stored JSON was perfectly well-formed and carried a
/// correct <c>"$type":"EventSubscription"</c> — the reading hub simply had no registration for it,
/// because <c>WithGraphTypes</c> listed every OTHER built-in content type and not this one.</para>
///
/// <para><b>Why it was not merely log noise.</b> The consumer is a background service, so the
/// failure had no visible surface at all. <see cref="EventSubscriptionRunner"/> tracks its pending
/// set through <c>workspace.GetQuery</c> and folded each node with
/// <c>n.Content as EventSubscription</c> — a soft-cast that yields a silent null on the degraded
/// shape. The pending set therefore came back <b>empty</b>, and that set is the candidate source for
/// EVERY firing path: the change feed, the change-feed-independent trigger-node watch,
/// <c>ScheduleTimer</c> and <c>WatchNodeStatus</c>. So a durable access grant existed in storage and
/// did nothing. Only the cold-start reconcile survived (it read the node tolerantly), which is why
/// most prod subscriptions eventually show <c>Fired</c> — after a restart. Between restarts an
/// invited user who signed up got no access, and Timer / NodeStatus subscriptions — which the
/// cold-start path does not cover at all — could never fire.</para>
///
/// <para>Two halves, both pinned below: the <b>registration</b> (the root cause — the type must
/// resolve where the read happens) and the <b>tolerant read</b> (the runner must not go silent on a
/// content shape it can still understand).</para>
/// </summary>
public class EventSubscriptionTypeRegistrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "GrantSpace";
    private const string InviteeEmail = "invitee@acme.com";
    private const string InviteeId = "invitee";

    /// <summary>
    /// A verbatim copy of a real degraded payload from the production log (2026-08-13,
    /// <c>Admin/EventSubscription/grant_rbuergi_systemorph_com_uwdeepfield</c>), with the identity
    /// swapped for the test's. This is the exact JSON the storage layer hands the read path.
    /// </summary>
    private static string StoredJson(string id) =>
        $$"""
        {"$type":"EventSubscription","id":"{{id}}","role":"Editor","createdAt":"2026-07-11T09:36:45.1517307+00:00",
         "createdBy":"rsalzmann","matchField":"email","matchValue":"{{InviteeEmail}}","targetPath":"{{Space}}",
         "restingValues":[],"triggerNodeType":"User"}
        """;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Space) { Name = "Grant Space", NodeType = "Space" });

    /// <summary>
    /// The root cause, at the seam that failed. A hub carrying exactly the mesh hub's graph
    /// registration — and which has never itself WRITTEN an EventSubscription, so nothing was
    /// auto-registered on it, the cold-boot condition — must resolve the stored payload to a typed
    /// instance. <c>MeshNodeStreamCache.DeserializeContent</c> makes precisely this call
    /// (<c>JsonSerializer.Deserialize&lt;object&gt;</c> through the reader's options) and warns
    /// "stayed an untyped JsonElement" when it degrades.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void AHubThatOnlyReadsEventSubscriptions_ResolvesTheStoredDiscriminator()
    {
        var reader = Mesh.GetHostedHub(
            new Address("client", Guid.NewGuid().ToString("N")[..12]),
            c => c.AddData().WithGraphTypes());

        reader.ServiceProvider.GetRequiredService<ITypeRegistry>()
            .TryGetType(nameof(EventSubscription), out var definition).Should().BeTrue(
                "EventSubscription is the content type of a built-in NodeType, so WithGraphTypes must "
                + "register it like every other built-in content type — its absence is what degraded a "
                + "dozen Admin/EventSubscription grant nodes on every production boot (#1392)");
        definition!.Type.Should().Be(typeof(EventSubscription));

        var content = JsonSerializer.Deserialize<object>(StoredJson("grant-read"), reader.JsonSerializerOptions);

        content.Should().NotBeOfType<JsonElement>(
            "a payload whose $type the reader cannot resolve DEGRADES to a raw JsonElement instead of "
            + "throwing — the silent trap-door every downstream 'Content is X' then falls through");
        var subscription = content.Should().BeOfType<EventSubscription>().Subject;
        subscription.TargetPath.Should().Be(Space);
        subscription.Role.Should().Be("Editor");
        subscription.Status.Should().Be(EventSubscriptionStatus.Pending,
            "an absent status field means Pending — the state the runner's pending-set filter selects on");
    }

    /// <summary>
    /// The consumer half, end to end: a grant stored in the shape the read path can hand back must
    /// actually GRANT. The subscription node is written with raw JSON content — never a typed CLR
    /// instance — so nothing auto-registers the type along the write path and the runner reads
    /// exactly what production storage holds. The runner is started BEFORE the triggering write, so
    /// this exercises the LIVE paths (change feed + trigger-node watch) that draw their candidates
    /// from the pending set, not the cold-start reconcile that masked the defect in production.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AGrantStoredAsRawJson_LandsTheAccessAssignment()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var runnerLogger = Mesh.ServiceProvider
            .GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>();

        const string subscriptionId = "grant-raw-json";
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(subscriptionId, EventSubscriptionNodeType.Namespace)
            {
                NodeType = EventSubscriptionNodeType.NodeType,
                Name = "NodeChange → GrantSpaceAccess",
                Content = JsonSerializer.Deserialize<JsonElement>(StoredJson(subscriptionId)),
            }).Should().Emit();

        using var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService, runnerLogger);
        await runner.StartAsync(default);

        // The invitee onboards — the trigger the subscription is waiting for.
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(InviteeId)
            {
                NodeType = "User",
                Name = "Invitee",
                Content = new User { Email = InviteeEmail, FullName = "Invitee" },
            }).Should().Emit();

        // Wait for the subscription to reach a TERMINAL state FIRST — race-free, because that node
        // already exists so the stream waits for its update, whereas opening a stream on a path that
        // does not exist yet fails fast rather than waiting. A Failed status surfaces its error here.
        // Before the fix this is where the test stops: the pending set folds the raw-JSON node to
        // null, no firing path ever has a candidate, and the subscription stays Pending forever.
        var final = await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscriptionId))
            .Select(n => n.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds());
        Assert.True(final!.Status == EventSubscriptionStatus.Fired,
            $"subscription ended {final.Status}: {final.LastError}");

        // The grant itself landed: {space}/_Access/{user}_Access carries the Editor role.
        var granted = await Mesh.GetWorkspace().GetMeshNodeStream($"{Space}/_Access/{InviteeId}_Access")
            .Where(n => n?.Content is AccessAssignment a
                        && a.Roles.Any(r => r.Role == "Editor" && !r.Denied))
            .FirstAsync().Timeout(20.Seconds());
        Assert.NotNull(granted);
    }
}
