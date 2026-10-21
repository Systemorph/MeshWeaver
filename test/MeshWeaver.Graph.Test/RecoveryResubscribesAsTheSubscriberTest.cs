using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A client stream's RECOVERY re-subscribes as the stream's own subscriber, never as System.
///
/// <para>A client's synchronization stream to an owner hub recovers on its own: when the owner ends
/// the server-side half (a recycle, a deactivation — <c>StreamEndedEvent</c>), and when the change
/// feed proves the mirror is behind, it posts a fresh <c>SubscribeRequest</c>. That re-subscribe
/// used to run as SYSTEM. The owner's access gate admits System, so the re-opened stream was decided
/// for nobody and delivered the node to a person whose Read had been revoked in the meantime.
/// Measured before the fix: a reader whose grant was deleted, then whose owner was recycled,
/// received the node's next revision.</para>
///
/// <para>Every refusal has a NEGATIVE CONTROL beside it: a reader who keeps the grant recovers
/// through the same recycle and receives the same revision.</para>
///
/// <para>Scope: the owner is RECYCLED before the post-revocation write, so the old server-side
/// subscription is gone and only a re-subscribe can deliver that write. What an OPEN subscription
/// does after a standard grant is revoked — with no recovery at all — is a separate question this
/// class does not decide (see Doc/Architecture/AccessControl → "Recovery re-subscribes as the
/// subscriber").</para>
/// </summary>
public class RecoveryResubscribesAsTheSubscriberTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Scope = "rr";
    private const string Doc = "rr/doc";

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    /// <summary>
    /// The reader's Read is revoked while the subscription is open; the owner is then recycled, so
    /// the client's recovery re-subscribes. The re-subscribe is decided for the READER — refused —
    /// and the revision written after the recycle never reaches them. NEGATIVE CONTROL: a reader
    /// who keeps the grant recovers through the same recycle and receives that revision.
    /// </summary>
    [HubFact]
    public async Task ARevokedReader_IsNotServedAgainByTheRecoveryResubscribe()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        var revokedGrant = await Grant("rr-revoked", ct);
        await Grant("rr-kept", ct);

        var revoked = Record(DirectStream(ClientAs("rr-revoked"), Doc));
        var kept = Record(DirectStream(ClientAs("rr-kept"), Doc));
        using var openRevoked = revoked.Connect();
        using var openKept = kept.Connect();
        await FirstNode(revoked, "the reader is served while the grant stands", ct);
        await FirstNode(kept, "the kept reader is served", ct);

        await AsSystem(() => NodeFactory.DeleteNode(revokedGrant.Path!), ct);
        await Mesh.GetEffectivePermissions(Doc, "rr-revoked")
            .Should().Within(TestTimeouts.Convergence)
            .Match(p => !p.HasFlag(Permission.Read), "PRECONDITION: the revocation is in force", ct);

        // Recovery: the owner ends every server-side subscription, and each client re-subscribes.
        await AsSystem(() => RequestHub.RecycleNode(Doc, reason: "RecoveryResubscribesAsTheSubscriberTest: end the owner-side subscriptions"), ct);
        await AsSystemRename(Doc, "after-recycle", ct);

        // NEGATIVE CONTROL: recovery works — the kept reader receives the post-recycle revision.
        await kept.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == NotificationKind.OnNext && n.Value?.Name == "after-recycle",
                "a reader who keeps the grant recovers and receives the revision written after the recycle", ct);

        var end = await revoked.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext || n.Value?.Name == "after-recycle",
                "the revoked reader's stream ends, or delivers the post-recycle revision", ct);
        Assert.False(end.Kind == NotificationKind.OnNext,
            "the recovery re-subscribe must be decided for the reader: a revoked Read is never served again");
        Assert.Equal(NotificationKind.OnError, end.Kind);
        Assert.Contains("Access denied", end.Exception!.Message);
    }

    /// <summary>
    /// A reader who NEVER had Read is refused, and nothing re-opens the subscription afterwards — not
    /// the owner's recycle, not a later write the change feed announces. Measured on the owner's own
    /// registry: it never serves that subscriber and stream id. NEGATIVE CONTROL: the permitted
    /// reader's subscription IS served by the owner (the probe can see a served stream). The
    /// three-second window outlasts the change-feed latch's debounce and grace (sanctioned negative
    /// wait). A GUARD: it held before the fix too — the refused initial subscribe stops the
    /// keep-alive (heartbeat and change-feed latch), and an owner that never served the stream never
    /// announces its end.
    /// </summary>
    [HubFact]
    public async Task AReaderWhoNeverHadRead_IsNeverServedByRecovery()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        await Grant("rr-kept", ct);

        var keptClient = ClientAs("rr-kept");
        var keptStream = ((Workspace)keptClient.GetWorkspace()).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(new Address(Doc), new MeshNodeReference());
        var kept = Record(keptStream.Select(c => c.Value).Where(n => n is not null)!);
        using var openKept = kept.Connect();
        await FirstNode(kept, "the permitted reader is served", ct);
        var ownerWorkspace = (Workspace)OwnerOf(Doc).GetWorkspace();
        Assert.True(ownerWorkspace.ServesClientSubscription(keptClient.Address, keptStream.StreamId),
            "NEGATIVE CONTROL: the owner's registry shows a served subscription");

        var outsiderClient = ClientAs("rr-outsider");
        var outsiderStream = ((Workspace)outsiderClient.GetWorkspace()).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(new Address(Doc), new MeshNodeReference());
        var outsider = Record(outsiderStream.Select(c => c.Value).Where(n => n is not null)!);
        using var openOutsider = outsider.Connect();
        var refused = await outsider.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext || n.Value?.Path == Doc, "the outsider is refused, or served", ct);
        Assert.Equal(NotificationKind.OnError, refused.Kind);

        await AsSystem(() => RequestHub.RecycleNode(Doc, reason: "RecoveryResubscribesAsTheSubscriberTest: end the owner-side subscriptions"), ct);
        await AsSystemRename(Doc, "after-recycle", ct);
        await kept.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == NotificationKind.OnNext && n.Value?.Name == "after-recycle",
                "NEGATIVE CONTROL: recovery ran — the permitted reader receives the revision", ct);

        var reopenedOwner = (Workspace)OwnerOf(Doc).GetWorkspace();
        await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Select(_ => reopenedOwner.ServesClientSubscription(outsiderClient.Address, outsiderStream.StreamId))
            .Where(serves => serves)
            .Should().NotEmit(TimeSpan.FromSeconds(3), "a refused reader's subscription is never re-opened by recovery", ct);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private async Task Seed(CancellationToken ct)
    {
        await SeedTopLevel(new MeshNode(Scope) { NodeType = "Markdown", Name = Scope });
        await AsSystem(() => NodeFactory.CreateNode(new MeshNode("doc", Scope) { NodeType = "Markdown", Name = "before" }), ct);
    }

    private async Task<MeshNode> Grant(string user, CancellationToken ct)
    {
        var grant = AssignmentNodeFactory.UserRole(user, Role.Viewer.Id, Scope);
        await AsSystem(() => NodeFactory.CreateNode(grant), ct);
        await Mesh.GetEffectivePermissions(Doc, user)
            .Should().Within(TestTimeouts.Convergence)
            .Match(p => p.HasFlag(Permission.Read), $"PRECONDITION: {user} may read {Doc}", ct);
        return grant;
    }

    private async Task AsSystem<T>(Func<IObservable<T>> work, CancellationToken ct)
        => await Mesh.ServiceProvider.GetRequiredService<AccessService>().RunAsSystem(work)
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task AsSystemRename(string path, string name, CancellationToken ct)
        => AsSystem(() => Mesh.GetMeshNodeStream(path).Update(node => node with { Name = name }), ct);

    private IMessageHub ClientAs(string userId)
    {
        var client = GetClient(config => ConfigureClient(config).AddLayoutClient());
        client.ServiceProvider.GetRequiredService<AccessService>()
            .SetHostIdentity(new AccessContext { ObjectId = userId, Name = userId });
        return client;
    }

    private IMessageHub OwnerOf(string path)
        => Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never)
           ?? throw new InvalidOperationException($"the per-node hub of '{path}' is not active");

    private static IObservable<MeshNode> DirectStream(IMessageHub client, string path)
        => ((Workspace)client.GetWorkspace()).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(new Address(path), new MeshNodeReference())
            .Select(change => change.Value)
            .Where(node => node is not null)!;

    private static IConnectableObservable<Notification<MeshNode>> Record(IObservable<MeshNode> source)
        => source.Materialize().Replay();

    private static Task FirstNode(IObservable<Notification<MeshNode>> stream, string because, CancellationToken ct)
        => stream.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == NotificationKind.OnNext && n.Value?.Path == Doc, because, ct);
}
