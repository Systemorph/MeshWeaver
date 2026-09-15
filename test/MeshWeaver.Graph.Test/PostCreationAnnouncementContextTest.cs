using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The post-creation announcement of an additional node declares its OWN access context</b>
/// (#4061, finding 1).
///
/// <para><c>MeshExtensions.RunPostCreationHandlersObs</c> persists the nodes a post-creation
/// handler returns from <c>GetAdditionalNodes</c> — for a Space that is the
/// <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/> — and then ANNOUNCES each one to
/// its own address with a <see cref="DataChangeRequest"/>. That message is
/// <c>[RequiresPermission(Permission.Update)]</c>, so the receiving hub's access gate decides it
/// against whatever identity the delivery carries. The post used to carry none of its own, which
/// means it carried whatever the ambient <c>AccessContext</c> happened to be at that instant —
/// and that is a property of what the persistence layer last did, not of this write.</para>
///
/// <para><b>The hub credential does NOT close it, measured.</b> The obvious spelling —
/// <c>o.ImpersonateAsHub(hub.Address)</c> — still fails: <c>PermissionEvaluator</c>'s
/// hub-credential early return grants <see cref="Permission.Read"/> on the hub's OWN path and its
/// ANCESTOR scopes and nothing else (<c>IsHubReadableScope</c>), so an Update on some other node's
/// path is not in it, and the gate answered
/// <c>Access denied: user 'portal/nodeops-…' lacks Update permission on 'Admin/Announcements/…'</c>.
/// An ambient <c>ImpersonateAsSystem()</c> scope does not close it either: an AsyncLocal scope only
/// covers what runs synchronously inside it, and this <c>.Do</c> runs from a previous Rx stage's
/// completion — which is how the ambient came to be the caller's here in the first place. The
/// identity has to be carried as a VALUE, which is what <c>WellKnownUsers.SystemContext</c> is
/// documented for.</para>
///
/// <para><b>Measured on main, in ONE run of this very fixture</b> (Debug logging on
/// <c>MeshWeaver.AccessContext</c>): the Space's own announcement went out as the interactive
/// caller — <c>PostPipeline: message=DataChangeRequest, user=Roland (context=Roland)</c> → the gate
/// answered <c>Update on Admin/Partition/announcecontrol was denied by the standard check</c> —
/// while THIS test's announcement, 41 ms later in the same test, went out as
/// <c>user=system-security (context=system-security, circuit=Roland)</c> and was allowed. Same code
/// path, same run, two different identities, neither of them declared. That is the intermittency
/// #4061 reports from CI, and it is why the property asserted here is the DECLARATION, not the
/// absence of a warning: an announcement that is allowed today because a sibling handler's
/// <c>ImpersonateAsSystem</c> scope has not been torn down yet is not an announcement that works.</para>
///
/// <para>The fixture is the ORDINARY-USER shape — RLS on, and no access assignment for the creator
/// anywhere, in particular none under <c>Admin</c>, where the partition definitions live. It is the
/// same mesh composition MeshWeaver.Plugins' <c>SpaceDeletionPartitionDropTests</c> uses, which is
/// where the intermittent failure was measured.</para>
/// </summary>
public class PostCreationAnnouncementContextTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TriggerType = "AnnouncementTrigger";
    private const string TargetType = "AnnouncementTarget";
    private const string TargetNamespace = "Admin/Announcements";
    private const string SpaceId = "announcecontrol";
    private const string TriggerId = "trigger";
    private const string OwnedId = "owned";

    /// <summary>Completed by the ANNOUNCED node's own hub the moment the announcement arrives.</summary>
    private readonly AsyncSubject<Unit> announcementArrived = new();

    /// <summary>Completed by the OWNED node's hub when the negative control's own write arrives.</summary>
    private readonly AsyncSubject<Unit> ownedWriteArrived = new();

    /// <summary>Every <see cref="DataChangeRequest"/> this suite's node type received, by path.</summary>
    private readonly ConcurrentDictionary<string, AccessContext?> receivedAs = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => Bootstrap.Bootstrap(builder)
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .AddMeshNodes(
                new MeshNode(TriggerType)
                {
                    Name = TriggerType,
                    NodeType = MeshNode.NodeTypePath,
                    Content = new NodeTypeDefinition { DefaultNamespace = "" },
                },
                new MeshNode(TargetType)
                {
                    Name = TargetType,
                    NodeType = MeshNode.NodeTypePath,
                    Content = new NodeTypeDefinition { DefaultNamespace = TargetNamespace },
                    // The instrument: the announced node's OWN hub, recording who the write that
                    // reached it was posted as. The delivery is handed on unchanged, so the data
                    // source still applies it.
                    HubConfiguration = config => config
                        .AddMeshDataSource(source => source.WithContentType<PartitionDefinition>())
                        .WithHandler<DataChangeRequest>((hub, delivery) =>
                        {
                            receivedAs[hub.Address.ToString()] = delivery.AccessContext;
                            if (hub.Address.ToString().StartsWith(TargetNamespace, StringComparison.Ordinal))
                            {
                                announcementArrived.OnNext(Unit.Default);
                                announcementArrived.OnCompleted();
                            }
                            else
                            {
                                ownedWriteArrived.OnNext(Unit.Default);
                                ownedWriteArrived.OnCompleted();
                            }
                            return delivery;
                        }),
                })
            .ConfigureServices(services =>
                services.AddSingleton<INodePostCreationHandler>(_ => new AnnouncingHandler()))
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    /// <summary>
    /// A post-creation handler that announces an additional node and impersonates NOTHING — the
    /// framework's own contract, with no sibling handler's scope for the announcement to ride on.
    /// <see cref="SpaceNodeType"/>'s handler has one (its creator-Admin grant runs under
    /// <c>ImpersonateAsSystem</c>), which is exactly what has been masking this.
    /// </summary>
    private sealed class AnnouncingHandler : INodePostCreationHandler
    {
        public string NodeType => TriggerType;

        public IObservable<Unit> Handle(MeshNode createdNode, string? createdBy)
            => Observable.Empty<Unit>();

        public IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode)
        {
            yield return new MeshNode(createdNode.Id, TargetNamespace)
            {
                NodeType = TargetType,
                Name = createdNode.Id,
                State = MeshNodeState.Active,
                Content = new PartitionDefinition { Namespace = createdNode.Id, DataSource = "default" },
            };
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task TheAnnouncementDeclaresTheHubsOwnIdentity()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // A Space the creator owns, so the two creates below are legitimately permitted. The
        // creator gets Admin on the Space and NOTHING under `Admin` — the ordinary-user shape.
        await meshService.CreateNode(new MeshNode(SpaceId)
        {
            Name = "Announce Control",
            NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Should().Within(TestTimeouts.CrossSilo).Emit("the creator may create a Space", cancellationToken: TestContext.Current.CancellationToken);

        // ── the NEGATIVE CONTROL, first: the same instrument on a node the creator DOES own,
        // written by the creator. If it reported the hub identity here too, the assertion below
        // would be reading a constant of the delivery path instead of a property of the write.
        await meshService.CreateNode(new MeshNode(OwnedId, SpaceId)
        {
            Name = "Owned", NodeType = TargetType, State = MeshNodeState.Active,
            Content = new PartitionDefinition { Namespace = OwnedId, DataSource = "default" },
        }).Should().Within(TestTimeouts.CrossSilo).Emit("the creator owns the Space", cancellationToken: TestContext.Current.CancellationToken);

        // The SAME message, to a path the creator may write, posted by the creator.
        Mesh.Post(
            DataChangeRequest.Update([new MeshNode(OwnedId, SpaceId)
            {
                Name = "Owned, renamed", NodeType = TargetType, State = MeshNodeState.Active,
                Content = new PartitionDefinition { Namespace = OwnedId, DataSource = "default" },
            }]),
            o => o.WithTarget(new Address($"{SpaceId}/{OwnedId}")));
        await ownedWriteArrived.Should().Within(TestTimeouts.Convergence).Emit(
            "an ordinary DataChangeRequest by the creator, to a path the creator owns, reaches the "
            + "node's hub — so the instrument below is measuring the announcement, not a path that "
            + "always answers the same way", cancellationToken: TestContext.Current.CancellationToken);

        var ownedContext = receivedAs[$"{SpaceId}/{OwnedId}"];
        ownedContext.Should().NotBeNull();
        ownedContext!.ObjectId.Should().Be(TestUsers.Admin.ObjectId,
            "the instrument reports the REAL identity a write was posted under — an ordinary write "
            + "by the creator arrives as the CREATOR. Without this the assertion below would be "
            + "reading a constant of the DataChangeRequest path rather than a property of the "
            + $"announcement, and it arrived as '{ownedContext.ObjectId ?? "(null)"}'");

        // ── the property under test.
        await meshService.CreateNode(new MeshNode(TriggerId, SpaceId)
        {
            Name = "Trigger", NodeType = TriggerType, State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.CrossSilo).Emit("the creator owns the Space, so the trigger create is permitted", cancellationToken: TestContext.Current.CancellationToken);

        await announcementArrived.Should().Within(TestTimeouts.Convergence).Emit(
            "the post-creation announcement of an additional node must reach that node's hub", cancellationToken: TestContext.Current.CancellationToken);

        var announcedContext = receivedAs[$"{TargetNamespace}/{TriggerId}"];
        announcedContext.Should().NotBeNull(
            "the announcement is an INFRASTRUCTURE write the framework makes on the node type's "
            + "behalf, so it must carry a context of its own");
        announcedContext!.ObjectId.Should().Be(WellKnownUsers.System,
            "the announcement must carry the platform-infrastructure identity as a VALUE "
            + "(PostOptions.WithAccessContext(WellKnownUsers.SystemContext)) instead of riding "
            + "whatever ambient AccessContext the persistence layer happened to leave set — which "
            + "is the CALLER's for a node created by an ordinary user, and the caller holds no "
            + $"Update under {TargetNamespace}. It arrived as "
            + $"'{announcedContext.ObjectId ?? "(null)"}'");
    }
}
