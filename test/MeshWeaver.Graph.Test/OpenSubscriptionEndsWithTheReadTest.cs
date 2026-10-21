using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The two windows where an OPEN subscription outlived a revoked or expired delegated read
/// (MeshWeaver#4306, follow-ups 1 and 2), measured on a real monolith mesh.
///
/// <list type="bullet">
///   <item><description><b>Follow-up 1, the node-stream read gate.</b> An admitted stream switched on
///     ONE live verdict, so the revision that revokes the read reached the caller before the verdict
///     could turn — the declaration of a STORED type is looked up asynchronously. Each revision is now
///     decided before it is forwarded: the revoking revision is never delivered.</description></item>
///   <item><description><b>Follow-up 2, a direct owner-side subscription.</b> The delivery gate
///     admitted a <c>SubscribeRequest</c> on its first verdict and the owner forwarded every later
///     revision for the subscription's life: after a revocation, after an expiry. It now ends with
///     the read.</description></item>
/// </list>
///
/// Every refusal has a NEGATIVE CONTROL beside it: a link that is not revoked keeps streaming a new
/// revision on the same kind of open subscription.
/// </summary>
public class OpenSubscriptionEndsWithTheReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "t";
    private const string HostDeclaredType = "Kiosk/Ticket";
    // A STORED type in a system-owned partition: its declaration is read through the partition's
    // one-way _GitSync, asynchronously — the case where the revoking revision used to arrive first.
    private const string StoredType = "pkg/Ticket";

    private static readonly AccessContext Visitor = new() { ObjectId = WellKnownUsers.Anonymous, Name = "Anonymous" };

    private static readonly PointReadDeclaration Declaration = new()
    {
        SubjectProperty = "subject",
        Namespace = Partition,
        RevokedProperty = "revokedAt",
        ExpiresProperty = "expiresAt",
        Areas = [""],
    };

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .WithMeshType(typeof(TicketContent), nameof(TicketContent))
        .AddMeshNodes(new MeshNode("Ticket", "Kiosk")
        {
            NodeType = "NodeType",
            Name = HostDeclaredType,
            Content = new NodeTypeDefinition { PointRead = Declaration },
            HubConfiguration = config => config.WithContentType<TicketContent>().AddDefaultLayoutAreas(),
        }, new MeshNode("Faulty", "Kiosk")
        {
            NodeType = "NodeType",
            Name = FaultyType,
            Content = new NodeTypeDefinition { PointRead = Declaration },
            HubConfiguration = config => config.WithContentType<TicketContent>().AddDefaultLayoutAreas(),
        })
        // A declared type whose OWN Read rule faults: every standard Read of it is UNDETERMINED.
        .ConfigureServices(services => services.AddSingleton<INodeTypeAccessRule>(new FaultingReadRule(FaultyType)));

    private const string FaultyType = "Kiosk/Faulty";

    private sealed class FaultingReadRule(string nodeType) : INodeTypeAccessRule
    {
        public string NodeType => nodeType;
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];
        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
            => Observable.Throw<bool>(new InvalidOperationException("the type's read rule cannot answer"));
    }

    /// <summary>
    /// 🚨 Follow-up 1: an open node stream of a STORED declared type never receives the revision that
    /// revokes it — the stream ends with the read gate's refusal instead. NEGATIVE CONTROL: an open
    /// stream of a link that is not revoked receives its next revision and stays open.
    /// </summary>
    [HubFact]
    public async Task TheNodeStream_NeverDeliversTheRevisionThatRevokesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        await SeedStoredType(ct);
        var revokedLink = await Issue("node-revoked", StoredType, DateTimeOffset.UtcNow.AddDays(1), ct);
        var keptLink = await Issue("node-kept", StoredType, DateTimeOffset.UtcNow.AddDays(1), ct);

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var revoked = Record(access.RunAs(Visitor, () => Mesh.GetMeshNodeStream(revokedLink)));
        var kept = Record(access.RunAs(Visitor, () => Mesh.GetMeshNodeStream(keptLink)));
        using var openRevoked = revoked.Connect();
        using var openKept = kept.Connect();
        await FirstNode(revoked, revokedLink, "the visitor's open stream serves the link before the revocation", ct);
        await FirstNode(kept, keptLink, "the visitor's open stream serves the kept link", ct);

        await AsSystemUpdate(revokedLink, c => c with { RevokedAt = DateTimeOffset.UtcNow.ToString("O") }, ct);
        await AsSystemUpdate(keptLink, c => c with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(2).ToString("O") }, ct);

        var end = await revoked.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext || IsRevoked(n.Value), "the stream ends, or delivers the revoking revision", cancellationToken: ct);
        Assert.False(end.Kind == NotificationKind.OnNext, "the revision that revokes the read must never reach the visitor");
        Assert.Equal(NotificationKind.OnError, end.Kind);
        Assert.IsType<UnauthorizedAccessException>(end.Exception);

        // NEGATIVE CONTROL: the same kind of open stream, a non-revoking revision, delivered.
        await kept.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == NotificationKind.OnNext && ExpiresAfter(n.Value, DateTimeOffset.UtcNow.AddDays(1.5)),
                "a link that is not revoked keeps streaming its next revision", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Follow-up 2: a DIRECT subscription to the link's own hub — the shape that bypasses the
    /// node-stream cache's read gate — ends when the link is revoked, and the revoking revision is
    /// never forwarded; and it ends at the link's expiry with nothing else emitting. NEGATIVE CONTROL:
    /// a direct subscription to a link that is not revoked keeps receiving revisions.
    /// </summary>
    [HubFact]
    public async Task ADirectSubscription_EndsWithTheDelegatedRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var revokedLink = await Issue("direct-revoked", HostDeclaredType, DateTimeOffset.UtcNow.AddDays(1), ct);
        var keptLink = await Issue("direct-kept", HostDeclaredType, DateTimeOffset.UtcNow.AddDays(1), ct);
        // A DISTANT expiry while the subscriptions are set up: the expiry is shortened only right
        // before the expiry phase (review of #7016, thread 4338339736), so a slow setup can never
        // refuse the link before its subscription is established.
        var expiringLink = await Issue("direct-expiring", HostDeclaredType, DateTimeOffset.UtcNow.AddDays(1), ct);

        var visitor = ClientAs(Visitor);
        var revoked = Record(DirectStream(visitor, revokedLink));
        var kept = Record(DirectStream(visitor, keptLink));
        var expiring = Record(DirectStream(visitor, expiringLink));
        using var openRevoked = revoked.Connect();
        using var openKept = kept.Connect();
        using var openExpiring = expiring.Connect();
        await FirstNode(revoked, revokedLink, "the direct subscription serves the link before the revocation", ct);
        await FirstNode(kept, keptLink, "the direct subscription serves the kept link", ct);
        await FirstNode(expiring, expiringLink, "the direct subscription serves the link before its expiry", ct);

        await AsSystemUpdate(revokedLink, c => c with { RevokedAt = DateTimeOffset.UtcNow.ToString("O") }, ct);
        var end = await revoked.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext || IsRevoked(n.Value), "the subscription ends, or forwards the revoking revision", cancellationToken: ct);
        Assert.False(end.Kind == NotificationKind.OnNext, "the owner must never forward the revision that revokes the read");
        Assert.Equal(NotificationKind.OnError, end.Kind);
        Assert.Contains("Access denied", end.Exception!.Message);
        // The exception TYPE does not survive the wire: the client recognises the denial — its
        // localized presentation, the denied-path redirect — only by the banner naming the path.
        Assert.Equal(revokedLink, MeshWeaver.Layout.AreaErrorClassifier.TryGetAccessDeniedPath(end.Exception));

        // NEGATIVE CONTROL: a non-revoking revision of a link that is not revoked is forwarded.
        await AsSystemUpdate(keptLink, c => c with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(2).ToString("O") }, ct);
        await kept.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == NotificationKind.OnNext && ExpiresAfter(n.Value, DateTimeOffset.UtcNow.AddDays(1.5)),
                "a direct subscription to a link that is not revoked keeps streaming", cancellationToken: ct);

        // The expiry, shortened now as System — the subscription is already open. The shortened
        // revision is forwarded first (the link has not expired yet); after it, no write and no
        // revision: the subscription ends AT the instant the link expires.
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O");
        await AsSystemUpdate(expiringLink, c => c with { ExpiresAt = expiresAt }, ct);
        var shortened = await expiring.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext || ExpiresAtOf(n.Value) == expiresAt,
                "the direct subscription receives the revision that shortens the expiry", cancellationToken: ct);
        Assert.Equal(NotificationKind.OnNext, shortened.Kind);
        var expired = await expiring.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext, "the subscription ends at the link's expiry", cancellationToken: ct);
        Assert.Equal(NotificationKind.OnError, expired.Kind);
        Assert.Contains("Access denied", expired.Exception!.Message);
        Assert.Equal(expiringLink, MeshWeaver.Layout.AreaErrorClassifier.TryGetAccessDeniedPath(expired.Exception));

        // …and the kept link is STILL served afterwards (the negative control holds past the expiry).
        await AsSystemUpdate(keptLink, c => c with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(3).ToString("O") }, ct);
        await kept.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == NotificationKind.OnNext && ExpiresAfter(n.Value, DateTimeOffset.UtcNow.AddDays(2.5)),
                "the kept link's subscription is still open", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Review of #7016, thread 4336475542: the follow's standing Read is TRI-STATE. A type whose
    /// own Read rule faults decides every standard Read UNDETERMINED; that is never a "no standing
    /// Read" for the declaration to re-decide. The followed verdict refuses a revision the
    /// declaration alone would admit. Pre-fix the undetermined check became <c>false</c>, the
    /// declaration was consulted, and the revision was admitted.
    /// </summary>
    [HubFact]
    public async Task AnUndeterminedStandingRead_IsNeverReDecidedThroughTheDeclaration()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var link = await Issue("faulty-link", FaultyType, DateTimeOffset.UtcNow.AddDays(1), ct);
        var (follow, node) = await FollowAs(link, ct);

        var declared = await DeclaredPointRead.AdmitsRevision(OwnerOf(link), node, Visitor).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        Assert.True(declared, "CONTROL: the declaration alone admits this revision, so only the tri-state can refuse it");
        await follow.VerdictOf(node).Take(1).Should().Within(TestTimeouts.Convergence)
            .Match(admitted => !admitted,
                "an undetermined standing Read refuses — although the declaration names the visitor", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Review of #7016, thread 4336475780: a node-read revision is decided on ITSELF whatever CLR
    /// shape it arrives in. The owner's current node admits the visitor; the revision handed to the
    /// verdict is a REVOKED one, as untyped JSON. Pre-fix a revision that was not a
    /// <see cref="MeshNode"/> instance skipped the per-revision check and was decided on the owner's
    /// current state alone — admitted. NEGATIVE CONTROL: the same revision unrevoked is admitted.
    /// </summary>
    [HubFact]
    public async Task AnUntypedRevision_IsStillDecidedOnItself()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var link = await Issue("untyped-link", HostDeclaredType, DateTimeOffset.UtcNow.AddDays(1), ct);
        var (follow, node) = await FollowAs(link, ct);
        var options = Mesh.JsonSerializerOptions;
        var revoked = node with
        {
            Content = node.ContentAs<TicketContent>(options)! with { RevokedAt = DateTimeOffset.UtcNow.ToString("O") },
        };

        await follow.VerdictOf(System.Text.Json.JsonSerializer.SerializeToElement(node, options)).Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Match(admitted => admitted, "NEGATIVE CONTROL: the unrevoked revision, untyped, is admitted", cancellationToken: ct);
        await follow.VerdictOf(System.Text.Json.JsonSerializer.SerializeToElement(revoked, options)).Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Match(admitted => !admitted,
                "a REVOKED revision is refused although it arrived untyped and the owner's current node still admits", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 A stream the NODE-STREAM CACHE opened is never followed, although it carries the context of
    /// the user whose write or read opened it. The cache is the shared reader every per-user read
    /// goes through, and it applies the per-user admission at its own read gate; following its stream
    /// decided that shared stream as one user, ran that user's standing Read on the owner hub, and
    /// delivered the owner's revisions to the cache through the follow's verdict chain. Measured in
    /// MeshWeaver.Plugins (<c>OperationRequestOnControlTest</c>): the request hub's control plane then
    /// composed its next read as the approver rather than as System, and the DSL prelude it reads
    /// was invisible to him. NEGATIVE CONTROL: the same delivery from a client address is followed.
    /// </summary>
    [HubFact]
    public async Task AStreamTheNodeStreamCacheOpened_IsNeverFollowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var link = await Issue("cache-link", HostDeclaredType, DateTimeOffset.UtcNow.AddDays(1), ct);
        await FollowAs(link, ct); // NEGATIVE CONTROL inside: a client's delivery IS followed

        var owner = OwnerOf(link);
        var fromTheCache = new MessageDelivery<SubscribeRequest>(
            new Address(MeshWeaver.Hosting.MeshNodeCacheIdentity.HubAddressType, "mesh-under-test"), owner.Address,
            new SubscribeRequest("cache-stream", new MeshNodeReference()), Mesh.JsonSerializerOptions)
        {
            AccessContext = Visitor,
        };

        Assert.Null(MeshWeaver.Hosting.Security.DeclaredReadSubscriptionFollow.Follow(owner, fromTheCache));
    }

    /// <summary>
    /// 🚨 Review of #7016, thread 4340066051: a REFUSED direct subscription leaves no owner-side
    /// subscription behind — the client's recovery must not re-open it. The refused mirror only
    /// FAULTED, and its recovery kept running: the owner's end announcement and the change-feed
    /// staleness latch (the revoking revision was announced on the feed and never delivered) each
    /// re-subscribed AS SYSTEM — an identity the follow exempts and the gate admits — so the owner
    /// served the revoked link again, unfollowed. Measured before the fix: a NEW owner-side stream
    /// for the same subscriber and stream id, about two seconds after the refusal. Measured here on
    /// the owner's own registry, compared by reference: after the refusal reached the client, the
    /// owner serves neither the refused stream nor a new one for that subscriber and stream id.
    /// The three-second window outlasts the latch's debounce and grace (sanctioned negative wait).
    /// </summary>
    [HubFact]
    public async Task ARefusedDirectSubscription_LeavesNoOwnerSubscriptionBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var link = await Issue("refused-recovery-link", HostDeclaredType, DateTimeOffset.UtcNow.AddDays(1), ct);
        var visitor = ClientAs(Visitor);
        var stream = ((Workspace)visitor.GetWorkspace()).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(new Address(link), new MeshNodeReference());
        var streamId = stream.StreamId;
        var recorded = Record(stream.Select(change => change.Value).Where(node => node is not null)!);
        using var open = recorded.Connect();
        await FirstNode(recorded, link, "the direct subscription serves the link before the revocation", ct);
        var ownerWorkspace = (Workspace)OwnerOf(link).GetWorkspace();
        var refused = ownerWorkspace.GetClientSubscription(visitor.Address, streamId, new MeshNodeReference());
        Assert.NotNull(refused);

        await AsSystemUpdate(link, c => c with { RevokedAt = DateTimeOffset.UtcNow.ToString("O") }, ct);
        var end = await recorded.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext, "the subscription ends with the refusal", cancellationToken: ct);
        Assert.Equal(NotificationKind.OnError, end.Kind);

        // Nothing serves it afterwards: neither the refused server-side stream (ended — unregistered,
        // then disposed) nor a new one (the end announcement and the client's recovery re-open
        // nothing). The served stream is compared by REFERENCE, so a re-open is told apart from a
        // stream that never ended.
        await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Select(_ => ownerWorkspace.GetClientSubscription(visitor.Address, streamId, new MeshNodeReference()))
            .Do(s =>
            {
                if (s is not null)
                    Output.WriteLine(ReferenceEquals(s, refused)
                        ? "the owner still serves the REFUSED stream (never ended)"
                        : "the owner serves a NEW stream for the refused subscriber (re-opened by recovery)");
            })
            .Select(s => s is not null)
            .Where(serves => serves)
            .Should().NotEmit(TimeSpan.FromSeconds(3), "a refused subscription is never re-opened on the owner", ct);
    }

    /// <summary>
    /// 🚨 Review of #7016, thread 4338481261: the cache exemption trusts only a delivery that did NOT
    /// enter through a participant ingress. An ingress re-stamps the identity but keeps the
    /// participant's own <c>Sender</c>, and <see cref="SubscribeRequest"/> is participant-postable —
    /// so a declaration-admitted subscribe claiming a <c>cache/…</c> sender used to skip the follow,
    /// and its owner-side stream outlived a revocation or an expiry. NEGATIVE CONTROL: the same
    /// cache-shaped delivery WITHOUT the ingress stamp (the real internal cache) stays exempt.
    /// </summary>
    [HubFact]
    public async Task AParticipantDeliveryClaimingTheCacheSender_IsStillFollowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var link = await Issue("spoofed-cache-link", HostDeclaredType, DateTimeOffset.UtcNow.AddDays(1), ct);
        await FollowAs(link, ct); // activates the owner; a client's delivery IS followed

        var owner = OwnerOf(link);
        MessageDelivery<SubscribeRequest> CacheShaped() => new(
            new Address(MeshWeaver.Hosting.MeshNodeCacheIdentity.HubAddressType, "mesh-under-test"), owner.Address,
            new SubscribeRequest("cache-shaped", new MeshNodeReference()), Mesh.JsonSerializerOptions)
        {
            AccessContext = Visitor,
        };

        Assert.Null(MeshWeaver.Hosting.Security.DeclaredReadSubscriptionFollow.Follow(owner, CacheShaped()));
        var fromAParticipant = (IMessageDelivery<SubscribeRequest>)CacheShaped().FromParticipant(Visitor, "signalr");
        Assert.True(fromAParticipant.IsFromParticipant(), "PRECONDITION: the delivery carries the ingress stamp");
        Assert.NotNull(MeshWeaver.Hosting.Security.DeclaredReadSubscriptionFollow.Follow(owner, fromAParticipant));
    }

    /// <summary>
    /// 🚨 Review of #7016, thread 4338481338: a STORED type's declaration is honoured only while its
    /// partition is system-owned (a one-way <c>_GitSync</c> naming a repository), and that ownership
    /// is followed LIVE. It used to be read once per verdict, so turning the sync two-way withdrew
    /// nothing from an open subscription until the link itself was written again, although a fresh
    /// read was already refused. NEGATIVE CONTROL: a change to the sync config that keeps the
    /// partition system-owned leaves the subscription open, and the next link revision is delivered.
    /// </summary>
    [HubFact]
    public async Task AnOpenSubscription_EndsWhenTheTypesPartitionStopsBeingSystemOwned()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        await SeedStoredType(ct);
        var link = await Issue("ownership-link", StoredType, DateTimeOffset.UtcNow.AddDays(1), ct);
        var direct = Record(DirectStream(ClientAs(Visitor), link));
        using var open = direct.Connect();
        await FirstNode(direct, link, "the direct subscription serves the stored type's link", ct);

        // NEGATIVE CONTROL: the sync config changes, the partition stays system-owned.
        await AsSystemSyncConfig(twoWay: false, ct);
        await AsSystemUpdate(link, c => c with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(2).ToString("O") }, ct);
        var stillOpen = await direct.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext || ExpiresAfter(n.Value, DateTimeOffset.UtcNow.AddDays(1.5)),
                "a sync-config change that keeps the partition system-owned leaves the subscription open", cancellationToken: ct);
        Assert.Equal(NotificationKind.OnNext, stillOpen.Kind);

        // The partition stops being system-owned — and NOTHING writes the link.
        await AsSystemSyncConfig(twoWay: true, ct);
        var end = await direct.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext, "the subscription ends when the type's partition stops being system-owned", cancellationToken: ct);
        Assert.Equal(NotificationKind.OnError, end.Kind);
        Assert.Contains("Access denied", end.Exception!.Message);
        Assert.Equal(link, MeshWeaver.Layout.AreaErrorClassifier.TryGetAccessDeniedPath(end.Exception));
    }

    private async Task AsSystemSyncConfig(bool twoWay, CancellationToken ct)
        => await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream($"pkg/{AccessAssignmentGuard.SyncConfigId}").Update(node => node with
            {
                Content = new JsonObject
                {
                    ["repositoryUrl"] = "https://github.com/example/pkg",
                    ["twoWay"] = twoWay,
                    ["lastSyncAt"] = DateTimeOffset.UtcNow.ToString("O"),
                },
            }))
            .Should().Emit(cancellationToken: ct);

    /// <summary>
    /// 🚨 Review of #7016, thread 4338167103: the follow's shared standing-Read verdict is owned by
    /// the SUBSCRIPTION, never by the owner hub. Its connection used to be registered on the node hub
    /// through the hub overload of <c>AutoConnectOwnedBy</c> — one composite per followed subscribe,
    /// removed only when the hub shut down — so every open/close cycle of a direct subscription on a
    /// long-lived node hub retained one registrant. Measured here: a run of closed followed
    /// subscriptions leaves the owner's disposal registry where it was.
    /// </summary>
    [HubFact]
    public async Task ClosedFollowedSubscriptions_DoNotGrowTheOwnerHubsDisposalRegistry()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var link = await Issue("cycled-link", HostDeclaredType, DateTimeOffset.UtcNow.AddDays(1), ct);
        var visitor = ClientAs(Visitor);
        // One cycle first, so whatever the owner registers once per hub (not per subscription) exists.
        await OpenAndClose(visitor, link, ct);
        var owner = (MessageHub)OwnerOf(link);
        var before = owner.DisposalRegistrantCount;

        const int cycles = 20;
        for (var i = 0; i < cycles; i++)
            await OpenAndClose(visitor, link, ct);

        var grown = owner.DisposalRegistrantCount - before;
        Output.WriteLine($"owner disposal registrants: {before} before, {owner.DisposalRegistrantCount} after {cycles} closed followed subscriptions");
        // Other services may register with the same hub meanwhile, so the bound is loose — but far
        // below one registrant per closed subscription.
        Assert.True(grown < cycles / 2,
            $"{cycles} closed followed subscriptions must not each leave a registrant on the owner hub — the registry grew by {grown}");
    }

    /// <summary>Opens a direct visitor subscription to <paramref name="path"/>, waits for the node, closes it, and waits until the owner no longer serves it.</summary>
    private async Task OpenAndClose(IMessageHub client, string path, CancellationToken ct)
    {
        var stream = ((Workspace)client.GetWorkspace()).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(new Address(path), new MeshNodeReference());
        var streamId = stream.StreamId;
        await stream.Select(change => change.Value).Where(node => node is not null).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var ownerWorkspace = (Workspace)OwnerOf(path).GetWorkspace();
        Assert.True(ownerWorkspace.ServesClientSubscription(client.Address, streamId),
            "PRECONDITION: the owner serves the direct subscription while it is open");
        stream.Dispose();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => ownerWorkspace.ServesClientSubscription(client.Address, streamId))
            .Where(serves => !serves)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
    }

    /// <summary>The follow a direct visitor subscription to <paramref name="path"/> gets on its owner, and the stored node.</summary>
    private async Task<(MeshWeaver.Data.Serialization.ClientReadFollow follow, MeshNode node)> FollowAs(string path, CancellationToken ct)
    {
        // As System: activates the per-node hub and reads the node, without asking the visitor's read.
        var node = (await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream(path)).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct))!;
        var owner = OwnerOf(path);
        var delivery = new MessageDelivery<SubscribeRequest>(
            new Address("client", "visitor"), owner.Address, new SubscribeRequest("follow-probe", new MeshNodeReference()), Mesh.JsonSerializerOptions)
        {
            AccessContext = Visitor,
        };
        var follow = MeshWeaver.Hosting.Security.DeclaredReadSubscriptionFollow.Follow(owner, delivery);
        Assert.NotNull(follow);
        return (follow!, node);
    }

    private IMessageHub OwnerOf(string path)
        => Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never)
           ?? throw new InvalidOperationException($"the per-node hub of '{path}' is not active");

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>Every notification of <paramref name="source"/>, replayed, so an assertion sees all of them in order.</summary>
    private static IConnectableObservable<Notification<MeshNode>> Record(IObservable<MeshNode> source)
        => source.Materialize().Replay();

    private static Task FirstNode(IObservable<Notification<MeshNode>> stream, string path, string because, CancellationToken ct)
        => stream.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == NotificationKind.OnNext && n.Value?.Path == path, because, cancellationToken: ct);

    private static IObservable<MeshNode> DirectStream(IMessageHub client, string path)
        => ((Workspace)client.GetWorkspace()).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(new Address(path), new MeshNodeReference())
            .Select(change => change.Value)
            .Where(node => node is not null)!;

    private IMessageHub ClientAs(AccessContext caller)
    {
        var client = GetClient(config => ConfigureClient(config).AddLayoutClient());
        client.ServiceProvider.GetRequiredService<AccessService>().SetHostIdentity(caller);
        return client;
    }

    private bool IsRevoked(MeshNode? node)
        => node?.ContentAs<TicketContent>(Mesh.JsonSerializerOptions)?.RevokedAt is not null;

    private string? ExpiresAtOf(MeshNode? node)
        => node?.ContentAs<TicketContent>(Mesh.JsonSerializerOptions)?.ExpiresAt;

    private bool ExpiresAfter(MeshNode? node, DateTimeOffset instant)
        => node?.ContentAs<TicketContent>(Mesh.JsonSerializerOptions)?.ExpiresAt is { } at
           && DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture) > instant;

    private Task SeedPartition(CancellationToken ct) => SeedTopLevel(new MeshNode(Partition) { NodeType = "Markdown", Name = "t" });

    private async Task SeedStoredType(CancellationToken ct)
    {
        await SeedTopLevel(new MeshNode("pkg") { NodeType = "Markdown", Name = "pkg" });
        await AsSystem(new MeshNode(AccessAssignmentGuard.SyncConfigId, "pkg")
        {
            NodeType = "Markdown",
            Content = new JsonObject { ["repositoryUrl"] = "https://github.com/example/pkg" },
        }, ct);
        await AsSystem(new MeshNode("Ticket", "pkg")
        {
            NodeType = "Markdown",
            Content = new JsonObject
            {
                [DeclaredPointRead.DeclarationProperty] = new JsonObject
                {
                    ["subjectProperty"] = "subject",
                    ["namespace"] = Partition,
                    ["revokedProperty"] = "revokedAt",
                    ["expiresProperty"] = "expiresAt",
                },
            },
        }, ct);
    }

    private async Task<string> Issue(string id, string nodeType, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var created = await AsSystem(new MeshNode(id, Partition)
        {
            NodeType = nodeType,
            Name = id,
            Content = new TicketContent(WellKnownUsers.Anonymous, expiresAt.ToString("O"), null),
        }, ct);
        Assert.Equal(WellKnownUsers.System, created.LastModifiedBy);
        return created.Path;
    }

    private async Task<MeshNode> AsSystem(MeshNode node, CancellationToken ct)
        => (await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateNode(node)).Should().Emit(cancellationToken: ct))!;

    private async Task AsSystemUpdate(string path, Func<TicketContent, TicketContent> change, CancellationToken ct)
        => await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream(path).Update(node =>
                node with { Content = change(node.ContentAs<TicketContent>(Mesh.JsonSerializerOptions)!) }))
            .Should().Emit(cancellationToken: ct);
}
