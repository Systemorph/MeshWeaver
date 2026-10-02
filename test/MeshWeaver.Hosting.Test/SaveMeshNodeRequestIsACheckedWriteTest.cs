using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <see cref="SaveMeshNodeRequest"/> is not a back door around the write checks.
///
/// <para>It is the per-node hub's own persistence step, and its handler used to write whatever node
/// any sender named straight to storage — no permission check, no validator. Every ingress forwards
/// any delivery to any address (SignalR and gRPC, an unauthenticated client as Anonymous), so any
/// connection could overwrite any node. Two layers close it, and each is tested on its own:</para>
/// <list type="bullet">
/// <item>the HANDLER writes raw only for the hub's own post; any other sender is forwarded to the
/// checked upsert under its own identity;</item>
/// <item>the INGRESS stamp (<see cref="ParticipantIngress"/>) makes the receiving hub refuse a
/// participant's delivery of an <see cref="InfrastructureOnlyAttribute"/> type outright — even an
/// admin's, even one that writes the target's own address as its sender.</item>
/// </list>
/// <para>Each refusal is paired with its control in the same mesh: an identity that may write posts
/// the same message without the stamp and the node changes — so the message reaches its handler and
/// the wait below is long enough to have seen a write.</para>
/// </summary>
public class SaveMeshNodeRequestIsACheckedWriteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ViewerId = "save-request-viewer";
    private const string Overwritten = "overwritten-by-save";

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>The viewer reads the partition and holds no Update anywhere. Built from
    /// <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> — the default mesh grants Public → Admin,
    /// under which every identity may write.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode($"{ViewerId}_Viewer", $"{TestPartition}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{ViewerId} — Viewer",
                MainNode = TestPartition,
                Content = new AccessAssignment
                {
                    AccessObject = ViewerId,
                    DisplayName = ViewerId,
                    Roles = [new RoleAssignment { Role = Role.Viewer.Id }],
                },
            });

    /// <summary>The handler layer: a caller that may not write posts the save over the client
    /// path and nothing changes; an admin posting the same message changes its node.</summary>
    [Theory(Timeout = 60000)]
    [InlineData(ViewerId)]
    [InlineData(WellKnownUsers.Anonymous)]
    public async Task ACallerWithoutWrite_PostingIt_ChangesNothing_WhileAnAdminPostingItDoes(string callerId)
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var target = await CreateNode(NewPath("target"));
        var control = await CreateNode(NewPath("control"));

        var client = GetClient();
        client.Post(new SaveMeshNodeRequest(Hijack(target)),
            o => o.WithTarget(new Address(target.Path)).WithAccessContext(Identity(callerId)));
        client.Post(new SaveMeshNodeRequest(Hijack(control)),
            o => o.WithTarget(new Address(control.Path)).WithAccessContext(TestUsers.Admin));

        // The control: an identity that may write posts the same message, and the node changes.
        await Overwrote(control.Path).Should().Within(TestTimeouts.Convergence)
            .Emit("an admin's SaveMeshNodeRequest is forwarded to the checked upsert, which writes it",
                cancellationToken: TestContext.Current.CancellationToken);

        // The refusal: same message, same window, a caller without Update — the node keeps its name.
        // A negative assertion spends its whole window by construction, and here the positive
        // terminal is already in hand (the control posted after it has already been written), so the window
        // is a fraction of the budget. TestTimeouts.Quick is a full CI-scaled bound (36 s on a
        // runner); one of those per case pushed this project past its 8-minute cap.
        await Overwrote(target.Path).Should().NotEmit(TestTimeouts.Quick / 10,
            $"'{callerId}' holds no Update on {target.Path}; the save must not write it raw");
        (await StoredAt(target.Path))!.Name.Should().Be(target.Name, "the refused save left the node as it was");
    }

    /// <summary>The ingress layer, through the same JSON round trip the SignalR and gRPC endpoints
    /// make (typed when the ingress hub knows the type, raw JSON when it does not): a participant's
    /// save is refused as <see cref="ErrorType.Forbidden"/> even when the connection is an admin's —
    /// and the same delivery without the ingress stamp writes, which is the negative control that
    /// the stamp, not the identity or the payload, is what refused it.</summary>
    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AParticipantsSave_IsRefusedForbidden_AndTheUnstampedDeliveryWrites(bool rawJson)
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var target = await CreateNode(NewPath("ingress"));
        var control = await CreateNode(NewPath("ingress-control"));
        var client = GetClient();

        var refused = AsReceivedByAnIngress(client.Address, Hijack(target), rawJson)
            .FromParticipant(TestUsers.Admin, "test-ingress");
        var answer = client.Observe(refused)
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .Take(1)
            .Replay();
        using var connect = answer.Connect();
        Mesh.DeliverMessage(refused);

        var fault = await answer.Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var failure = fault.Should().BeOfType<DeliveryFailureException>(
            "the participant is answered with a failure, not left waiting").Subject.Failure;
        failure!.ErrorType.Should().Be(ErrorType.Forbidden);
        failure.Message.Should().Contain(nameof(SaveMeshNodeRequest));

        // The negative control: the SAME delivery shape and identity without the stamp is an
        // ordinary non-self save, which the handler forwards to the checked upsert — and it writes.
        Mesh.DeliverMessage(AsReceivedByAnIngress(client.Address, Hijack(control), rawJson)
            .SetAccessContext(TestUsers.Admin));
        await Overwrote(control.Path).Should().Within(TestTimeouts.Convergence)
            .Emit("the unstamped delivery is not refused, so the stamp is what refused the other", cancellationToken: ct);

        // A negative assertion spends its whole window by construction, and here the positive
        // terminal is already in hand (the refusal has been answered and the control written), so the window
        // is a fraction of the budget. TestTimeouts.Quick is a full CI-scaled bound (36 s on a
        // runner); one of those per case pushed this project past its 8-minute cap.
        await Overwrote(target.Path).Should().NotEmit(TestTimeouts.Quick / 10, "the refused save wrote nothing");
    }

    /// <summary>A participant cannot pose as the hub's own persistence post by writing the target's
    /// address as the sender: the stamped delivery is refused before the handler's self-post check
    /// is reached, whatever the identity.</summary>
    [Fact(Timeout = 60000)]
    public async Task AParticipantPosingAsTheHubItself_WritesNothing()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var target = await CreateNode(NewPath("spoof"));

        Mesh.DeliverMessage(AsReceivedByAnIngress(new Address(target.Path), Hijack(target), rawJson: false)
            .FromParticipant(TestUsers.Admin, "test-ingress"));

        await Overwrote(target.Path).Should().NotEmit(TestTimeouts.Quick,
            "a stamped save is refused even when its sender claims to be the hub itself");
        var stored = await Storage.Read(target.Path, Mesh.JsonSerializerOptions).DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        stored!.Name.Should().Be(target.Name);
    }

    /// <summary>Legitimate persistence still works: the canonical write of a node — routed to its
    /// owning hub, whose own persistence (the post-commit flush and the sampler's self-addressed
    /// save) lands it in storage — is untouched by the save becoming a checked write for every
    /// other sender.</summary>
    [Fact(Timeout = 60000)]
    public async Task TheCanonicalWrite_StillPersists()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var target = await CreateNode(NewPath("own"));

        await NodeFactory.UpdateNode(target with { Name = Overwritten })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        await Overwrote(target.Path).Should().Within(TestTimeouts.Convergence)
            .Emit("the owning hub persists the updated node", cancellationToken: ct);
    }

    /// <summary>The delivery as an ingress reads it: the participant serialised its envelope with the
    /// message packaged, and the ingress deserialised it with the portal hub's options — typed when the
    /// type is registered there, or kept as raw JSON (<paramref name="rawJson"/>) for the receiving hub
    /// to type, which is the case an ingress-side type check alone could never see.</summary>
    private IMessageDelivery AsReceivedByAnIngress(Address sender, MeshNode node, bool rawJson)
    {
        var options = Mesh.JsonSerializerOptions;
        IMessageDelivery sent = new MessageDelivery<SaveMeshNodeRequest>(
            sender, new Address(node.Path), new SaveMeshNodeRequest(node), options);
        var packaged = sent.Package(options);
        if (rawJson)
            return packaged;
        var json = JsonSerializer.Serialize(packaged, options);
        return JsonSerializer.Deserialize<IMessageDelivery>(json, options)!;
    }

    /// <summary>The overwrite an attacker would send: a new name at a version ahead of anything the
    /// hub has persisted, so the raw save's duplicate-write suppression (which drops a version the
    /// post-commit flush already wrote) cannot be what keeps the node unchanged.</summary>
    private static MeshNode Hijack(MeshNode node) => node with { Name = Overwritten, Version = node.Version + 1000 };

    /// <summary>Emits once storage holds <paramref name="path"/> under the overwritten name — the
    /// sanctioned re-query shape for a source with no change signal of its own.</summary>
    private IObservable<bool> Overwrote(string path) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Storage.Read(path, Mesh.JsonSerializerOptions).DefaultIfEmpty(null).Take(1))
            .Where(node => node?.Name == Overwritten)
            .Select(_ => true)
            .Take(1);

    private Task<MeshNode?> StoredAt(string path) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private async Task<MeshNode> CreateNode(string path)
        => await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = path,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private static string NewPath(string prefix) =>
        $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private static AccessContext Identity(string userId) => new() { ObjectId = userId, Name = userId };
}
