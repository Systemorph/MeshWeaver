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
        });

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
        var expiringLink = await Issue("direct-expiring", HostDeclaredType, DateTimeOffset.UtcNow.AddSeconds(8), ct);

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

        // NEGATIVE CONTROL: a non-revoking revision of a link that is not revoked is forwarded.
        await AsSystemUpdate(keptLink, c => c with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(2).ToString("O") }, ct);
        await kept.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == NotificationKind.OnNext && ExpiresAfter(n.Value, DateTimeOffset.UtcNow.AddDays(1.5)),
                "a direct subscription to a link that is not revoked keeps streaming", cancellationToken: ct);

        // The expiry: no write, no revision — the subscription ends AT the instant the link expires.
        var expired = await expiring.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext, "the subscription ends at the link's expiry", cancellationToken: ct);
        Assert.Equal(NotificationKind.OnError, expired.Kind);
        Assert.Contains("Access denied", expired.Exception!.Message);

        // …and the kept link is STILL served afterwards (the negative control holds past the expiry).
        await AsSystemUpdate(keptLink, c => c with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(3).ToString("O") }, ct);
        await kept.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == NotificationKind.OnNext && ExpiresAfter(n.Value, DateTimeOffset.UtcNow.AddDays(2.5)),
                "the kept link's subscription is still open", cancellationToken: ct);
    }

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
