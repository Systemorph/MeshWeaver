using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A node type's DECLARED point read (<see cref="PointReadDeclaration"/>, MeshWeaver#4306),
/// measured on a real monolith mesh through every seam that serves a node: the RLS validator a
/// point read and a listing row each pass, the RLS-filtered listing, the node-stream read gate, and
/// the delivery gate plus the node hub's own data read.
///
/// <para>The property under test: the declaration admits a caller to ONE node at its OWN path and
/// to nothing else — never a listing, never another node, never a node the platform did not issue,
/// never a type the platform does not own — and it is no grant row. Every admission has a refusal
/// beside it that differs in one input.</para>
/// </summary>
public class DeclaredPointReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string DeclaredType = "Kiosk/Ticket";
    private const string UndeclaredType = "Kiosk/Plain";
    private const string Partition = "t";
    private const string Group = "Groups/acme";

    private static readonly AccessContext Visitor = new() { ObjectId = WellKnownUsers.Anonymous, Name = "Anonymous" };
    private static readonly AccessContext Carol = new() { ObjectId = "carol", Name = "carol" };
    private static readonly AccessContext Dave = new() { ObjectId = "dave", Name = "dave" };

    private const string FaultyType = "Kiosk/Faulty";

    private static readonly PointReadDeclaration Declaration = new()
    {
        SubjectProperty = "subject",
        Namespace = Partition,
        RevokedProperty = "revokedAt",
        ExpiresProperty = "expiresAt",
        // The DEFAULT area only — so an explicitly named area, even the one the default renders, is not declared.
        Areas = [""],
    };

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .WithMeshType(typeof(TicketContent), nameof(TicketContent))
        .AddMeshNodes(
            TypeNode(DeclaredType, Declaration),
            TypeNode(UndeclaredType, declaration: null),
            TypeNode(FaultyType, Declaration),
            // carol is a member of the group an audience may name; dave is not.
            new MeshNode("carol_Membership", Group)
            {
                NodeType = "GroupMembership",
                Content = new GroupMembership { Member = Carol.ObjectId, Groups = [new MembershipEntry { Group = Group }] },
            },
            // alice may write — and move — in the partition (Admin: the move entitlement a carried
            // authorship requires), but a node SHE writes is not platform-issued.
            AssignmentNodeFactory.UserRole("alice", "Admin", Partition))
        // A type whose OWN Read rule faults: its check is UNDETERMINED, which no declaration may
        // re-decide as if it were a denial.
        .ConfigureServices(services => services.AddSingleton<INodeTypeAccessRule>(new FaultingReadRule(FaultyType)));

    private sealed class FaultingReadRule(string nodeType) : INodeTypeAccessRule
    {
        public string NodeType => nodeType;
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];
        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
            => Observable.Throw<bool>(new InvalidOperationException("the type's read rule cannot answer"));
    }

    /// <summary>
    /// 🚨 The declaration admits a POINT read of the node and nothing a LISTING sees: the RLS
    /// validator admits the node's own read and refuses the same node as a listing row; a listing of
    /// the partition run as the visitor is empty; the node stream and the node hub's data read serve
    /// it. NEGATIVE CONTROL: restoring the old grant row makes the listing leak.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task APointRead_IsAdmitted_AndNoListingSeesTheNode()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var link = await Issue("a", WellKnownUsers.Anonymous, ct);
        var sibling = await Issue("b", WellKnownUsers.Anonymous, ct);

        Assert.True((await Rls(Visitor, link, pointRead: true, ct)).IsValid, "the declared point read admits the visitor to the node's own path");
        Assert.False((await Rls(Visitor, link, pointRead: false, ct)).IsValid, "the same node as a LISTING row is refused");

        Assert.Empty(await PathsAs(Visitor, $"namespace:{Partition} scope:descendants").Should().Emit(cancellationToken: ct));
        Assert.Empty(await PathsAs(Visitor, $"path:{link}").Should().Emit(cancellationToken: ct));

        var streamed = await StreamAs(Visitor, link).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.Equal(link, streamed.Path);
        var served = await DataReadAs(Visitor, link).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.NotNull(served.Data);

        // The sibling is a point read of ITS OWN id — reachable only by a caller who already holds that id.
        Assert.DoesNotContain(sibling, await PathsAs(Visitor, $"namespace:{Partition} scope:children").Should().Emit(cancellationToken: ct));

        // NEGATIVE CONTROL: the shape this replaces — an Anonymous Viewer grant on the node — leaks into the listing.
        await AsSystem(AssignmentNodeFactory.UserRole(WellKnownUsers.Anonymous, "Viewer", link), ct);
        await Within(() => PathsAs(Visitor, $"namespace:{Partition} scope:descendants"), paths => paths.Contains(link),
            "a grant row is visible to every listing — which is why the declaration replaces it", ct);
    }

    /// <summary>
    /// 🚨 Every input that withdraws the admission withdraws it: a revoked node, an expired node, a
    /// node of a type that declares nothing, a node a person (not the platform) wrote, a stored type
    /// in a partition the platform does not own — each refused, beside a positive control that
    /// differs in that input alone. Revoking an admitted node ends the point read.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task EveryWithdrawingInput_RefusesThePointRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);

        var open = await Issue("open", WellKnownUsers.Anonymous, ct);
        Assert.True((await Rls(Visitor, open, true, ct)).IsValid, "POSITIVE CONTROL: an issued, unexpired, unrevoked node is admitted");

        var revoked = await Issue("revoked", WellKnownUsers.Anonymous, ct, revoked: true);
        Assert.False((await Rls(Visitor, revoked, true, ct)).IsValid, "a revoked node admits nobody");

        var expired = await Issue("expired", WellKnownUsers.Anonymous, ct, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.False((await Rls(Visitor, expired, true, ct)).IsValid, "an expired node admits nobody");

        var undeclared = await Issue("undeclared", WellKnownUsers.Anonymous, ct, nodeType: UndeclaredType);
        Assert.False((await Rls(Visitor, undeclared, true, ct)).IsValid, "NEGATIVE CONTROL (rule removed): a type that declares nothing admits nobody");

        // A node a PERSON wrote, with the same content, is not platform-issued.
        var personal = Node("personal", WellKnownUsers.Anonymous, DeclaredType, revoked: false, DateTimeOffset.UtcNow.AddDays(1));
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAs(new AccessContext { ObjectId = "alice", Name = "alice" }, () => NodeFactory.CreateNode(personal))
            .Should().Emit(cancellationToken: ct);
        Assert.False((await Rls(Visitor, personal.Path, true, ct)).IsValid, "a node a person wrote cannot open itself to the world");

        // A STORED type in a system-owned partition (one-way _GitSync) is honoured; the same type in
        // a partition nobody owns is not.
        await SeedStoredType("pkg", systemOwned: true, ct);
        await SeedStoredType("home", systemOwned: false, ct);
        var owned = await Issue("owned", WellKnownUsers.Anonymous, ct, nodeType: "pkg/Ticket");
        Assert.True((await Rls(Visitor, owned, true, ct)).IsValid, "a stored type in a system-owned partition declares the point read");
        var unowned = await Issue("unowned", WellKnownUsers.Anonymous, ct, nodeType: "home/Ticket");
        Assert.False((await Rls(Visitor, unowned, true, ct)).IsValid, "a type in a partition the platform does not own declares nothing");

        // Review of #6935 (thread 4306016282): a stored type's declaration is read from its OWNER — once
        // the owner acknowledged its withdrawal, the very next point read is refused (no polling).
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream("pkg/Ticket").Update(node => node with { Content = new JsonObject { ["withdrawn"] = true } }))
            .Should().Emit(cancellationToken: ct);
        Assert.False((await Rls(Visitor, owned, true, ct)).IsValid, "a withdrawn declaration admits nobody from the next read on");

        // Review of #6935 (thread 4306016260): a System identity in a caller's place is an ambient context leaking — refused.
        var signedInOnly = await Issue("signedin-sys", WellKnownUsers.Public, ct);
        Assert.False(await DeclaredPointRead.Admits(Mesh, signedInOnly, new AccessContext { ObjectId = WellKnownUsers.System }).Take(1)
                .Should().Emit(cancellationToken: ct),
            "a System caller is never admitted by a declaration (it would otherwise match a Public audience)");
        Assert.True(await DeclaredPointRead.Admits(Mesh, signedInOnly, Dave).Take(1).Should().Emit(cancellationToken: ct),
            "POSITIVE CONTROL: a signed-in caller is admitted");

        // Revoking the admitted node ends the point read at every seam.
        await AsSystemUpdate(open, content => content with { RevokedAt = DateTimeOffset.UtcNow.ToString("O") }, ct);
        await Within(() => RlsDecision(Visitor, open, true), r => !r.IsValid, "a revoked node is refused at the RLS validator", ct);
        var cut = await StreamAs(Visitor, open).Materialize().Take(1).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.Equal(System.Reactive.NotificationKind.OnError, cut.Kind);
    }

    /// <summary>
    /// The subject names the audience: <c>Public</c> admits every signed-in user and never a
    /// logged-out one; a group admits its members and nobody else.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TheSubject_NamesTheAudience()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);

        var signedIn = await Issue("signedin", WellKnownUsers.Public, ct);
        Assert.True((await Rls(Dave, signedIn, true, ct)).IsValid, "a signed-in user is in the Public audience");
        Assert.False((await Rls(Visitor, signedIn, true, ct)).IsValid, "a logged-out visitor is not");

        var group = await Issue("group", Group, ct);
        Assert.True((await Rls(Carol, group, true, ct)).IsValid, "a member of the group is admitted");
        Assert.False((await Rls(Dave, group, true, ct)).IsValid, "a signed-in non-member is not");
        Assert.False((await Rls(Visitor, group, true, ct)).IsValid, "nor is a logged-out visitor");
    }

    /// <summary>
    /// 🚨 Review of #6935: the declaration is honoured only for a DIRECT child of the namespace the
    /// type declares — never a node elsewhere, never a descendant — and never for an API-token
    /// caller (the fold's live <c>api</c> clamp is not reopened) or a VIRTUAL visitor treated as
    /// signed in.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ThePointRead_StaysInsideItsNamespace_AndKeepsTheCallersFlags()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var inside = await Issue("inside", WellKnownUsers.Anonymous, ct);
        Assert.True((await Rls(Visitor, inside, true, ct)).IsValid, "POSITIVE CONTROL: a direct child of the declared namespace");

        await SeedTopLevel(new MeshNode("u") { NodeType = "Markdown", Name = "u" });
        var elsewhere = await Issue("elsewhere", WellKnownUsers.Anonymous, ct, ns: "u");
        Assert.False((await Rls(Visitor, elsewhere, true, ct)).IsValid, "a node outside the declared namespace admits nobody");
        await AsSystem(new MeshNode("sub", Partition) { NodeType = "Markdown", Name = "sub" }, ct);
        var deeper = await Issue("deeper", WellKnownUsers.Anonymous, ct, ns: $"{Partition}/sub");
        Assert.False((await Rls(Visitor, deeper, true, ct)).IsValid, "a DESCENDANT of the declared namespace admits nobody");

        var token = new AccessContext { ObjectId = Dave.ObjectId, Name = Dave.Name, IsApiToken = true };
        Assert.True((await Rls(Dave, inside, true, ct)).IsValid, "POSITIVE CONTROL: dave's browser is in an Anonymous audience");
        Assert.False((await Rls(token, inside, true, ct)).IsValid, "dave's API TOKEN is not admitted by a declaration");

        var guest = new AccessContext { ObjectId = "guest-7f3a", Name = "Guest", IsVirtual = true };
        var signedIn = await Issue("signedin-only", WellKnownUsers.Public, ct);
        Assert.False((await Rls(guest, signedIn, true, ct)).IsValid, "a VIRTUAL visitor is not signed in, whatever id it carries");
        Assert.False(await DeclaredPointRead.ObservePointRead(Mesh, signedIn, guest).Take(1).Should().Emit(cancellationToken: ct),
            "…nor through the page's live verdict");
        Assert.True(await DeclaredPointRead.ObservePointRead(Mesh, signedIn, Dave).Take(1).Should().Emit(cancellationToken: ct),
            "POSITIVE CONTROL: a signed-in user is admitted through the page's live verdict");
    }

    /// <summary>
    /// 🚨 Review of #6935: the admission is LIVE. An OPEN node stream admitted by the declaration is
    /// cut when the node is revoked (later content is never delivered), the page's live verdict on
    /// its ORIGINAL subscription turns false on revocation and at expiry, and a point read right after
    /// the owner acknowledged the revocation — with no wait for persistence — is refused.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TheAdmission_IsLive_AndEndsOnAnOpenSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var link = await Issue("live", WellKnownUsers.Anonymous, ct);

        var stream = Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAs(Visitor, () => Mesh.GetMeshNodeStream(link))
            .Materialize()
            .Replay(1);
        using var openStream = stream.Connect();
        await stream.Should().Within(TimeSpan.FromSeconds(20))
            .Match(n => n.HasValue && n.Value?.Path == link, "the open stream serves the node before the revocation", cancellationToken: ct);
        var verdict = DeclaredPointRead.ObservePointRead(Mesh, link, Visitor).Replay(1);
        using var openVerdict = verdict.Connect();
        await verdict.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v, "the live verdict admits before the revocation", cancellationToken: ct);

        await AsSystemUpdate(link, content => content with { RevokedAt = DateTimeOffset.UtcNow.ToString("O") }, ct);
        // No wait for persistence: the decision reads the OWNER's current node.
        Assert.False((await Rls(Visitor, link, true, ct)).IsValid, "a point read right after the acknowledged revocation is refused");

        var cut = await stream.Should().Within(TimeSpan.FromSeconds(20))
            .Match(n => n.Kind != System.Reactive.NotificationKind.OnNext || n.Value?.Path != link || IsRevoked(n.Value),
                "the open stream is cut once the admission ends", cancellationToken: ct);
        if (cut.Kind == System.Reactive.NotificationKind.OnNext)
            cut = await stream.Should().Within(TimeSpan.FromSeconds(20))
                .Match(n => n.Kind == System.Reactive.NotificationKind.OnError, "…and faults with the read gate's refusal", cancellationToken: ct);
        Assert.Equal(System.Reactive.NotificationKind.OnError, cut.Kind);
        Assert.IsType<UnauthorizedAccessException>(cut.Exception);
        await verdict.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v, "the ORIGINAL live verdict turns false on revocation", cancellationToken: ct);

        var expiring = await Issue("expiring", WellKnownUsers.Anonymous, ct, expiresAt: DateTimeOffset.UtcNow.AddSeconds(3));
        var expiry = DeclaredPointRead.ObservePointRead(Mesh, expiring, Visitor).Replay(1);
        using var openExpiry = expiry.Connect();
        await expiry.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v, "admitted before the expiry", cancellationToken: ct);
        await expiry.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v, "the live verdict turns false AT the expiry, with nothing else emitting", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Review of #6935: two ways to borrow the standard check's place, both refused. A type whose
    /// own Read rule cannot answer (UNDETERMINED) is not re-decided by its declaration; and a person
    /// who carries a System-authored source's stamps onto a node of the declared type
    /// (<c>AuthorshipFrom</c> naming a source of ANOTHER type) is recorded as its author, so the node
    /// is not platform-issued.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task NeitherAnUndeterminedRuleNorBorrowedAuthorship_OpensThePointRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);

        var faulty = await Issue("faulty", WellKnownUsers.Anonymous, ct, nodeType: FaultyType);
        Assert.False((await Rls(Visitor, faulty, true, ct, FaultyType)).IsValid, "an undetermined type rule is not re-decided at the RLS validator");
        var refused = await StreamAs(Visitor, faulty).Materialize().Take(1).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.Equal(System.Reactive.NotificationKind.OnError, refused.Kind);

        await AsSystem(new MeshNode("src", Partition) { NodeType = "Markdown", Name = "src" }, ct);
        var alice = new AccessContext { ObjectId = "alice", Name = "alice" };
        // PRECONDITION, or the case below is vacuous: alice holds the move entitlement on the source.
        Assert.True(await Mesh.CheckPermission(Partition, alice.ObjectId, Permission.Delete).Take(1).Should().Emit(cancellationToken: ct),
            "alice holds Delete on the source's namespace — the entitlement that lets a create carry its authorship");
        var forged = Node("forged", WellKnownUsers.Anonymous, DeclaredType, revoked: false, DateTimeOffset.UtcNow.AddDays(1));
        var response = await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAs(alice, () => ObserveNodeOperation(new CreateNodeRequest(forged) { AuthorshipFrom = $"{Partition}/src" },
                o => o.WithAccessContext(alice)))
            .Select(d => d.Message)
            .Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.True(response.Success, $"alice may create in the partition — {response.Error}");
        Assert.Equal("alice", response.Node?.LastModifiedBy);
        Assert.False((await Rls(Visitor, forged.Path, true, ct)).IsValid, "a borrowed System stamp does not make a node platform-issued");
    }

    private bool IsRevoked(MeshNode? node)
        => node?.ContentAs<TicketContent>(Mesh.JsonSerializerOptions)?.RevokedAt is not null;

    /// <summary>
    /// 🚨 Review of #6935 (thread 4305971990): the delivery gate admits a declaration-only caller to
    /// the ONE point-read shape — the node itself, or a layout area the type declares — and to nothing
    /// else on that hub. The hub's workspace also holds the node's satellites, so a COLLECTION read or
    /// subscription, a request for another path, or an undeclared area keeps the standard denial.
    /// POSITIVE CONTROLS: the node itself and the declared default area are served; the same
    /// collection request is answered for a reader with a grant (so the refusal is the gate's).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task OnlyTheNodeAndADeclaredArea_AreAdmittedOnItsHub()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var link = await Issue("hub", WellKnownUsers.Anonymous, ct);
        const string Secret = "SATELLITE-SECRET-6f1d";
        await AsSystem(new MeshNode("_Secret", link) { NodeType = "Markdown", Name = Secret, Content = new JsonObject { ["content"] = Secret } }, ct);

        var visitor = ClientAs(Visitor);
        var address = new Address(link);
        await visitor.Observe(new PingRequest(), o => o.WithTarget(address)).Should().Emit(cancellationToken: ct);

        // POSITIVE CONTROL: the node itself.
        var node = await visitor.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(address))
            .Select(d => d.Message).Take(1).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.NotNull(node.Data);

        // (1) A COLLECTION read on the admitted hub is refused and returns nothing of the satellite.
        var collection = await visitor.Observe(new GetDataRequest(new CollectionReference(nameof(MeshNode))), o => o.WithTarget(address))
            .Select(d => d.Message).Materialize().Take(1).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.False(collection.HasValue && collection.Value.Data is not null
                     && JsonSerializer.Serialize(collection.Value.Data, Mesh.JsonSerializerOptions).Contains(Secret),
            "a declaration-only caller's collection read must not return the hub's satellites");
        Assert.True(collection.Kind == System.Reactive.NotificationKind.OnError || collection.Value.Data is null,
            $"the collection read is refused: {collection.Kind} {collection.Exception?.Message}");

        // (2) A COLLECTION subscription is refused the same way.
        var subscribed = await visitor.GetWorkspace()
            .GetRemoteStream<JsonElement, CollectionReference>(address, new CollectionReference(nameof(MeshNode)))
            .Materialize().Take(1).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.Equal(System.Reactive.NotificationKind.OnError, subscribed.Kind);
        Assert.Contains("Access denied", subscribed.Exception!.Message);

        // A request naming ANOTHER path through the node reference is not the node's own point read.
        Assert.Null(DeclaredPointRead.RequestShape(new GetDataRequest(new MeshNodeReference($"{Partition}/other")), link));

        // (3) The declared default area renders; an UNDECLARED area is refused.
        var page = await visitor.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(address, new LayoutAreaReference((string?)null) { Id = "" })
            .Materialize().Take(1).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.Equal(System.Reactive.NotificationKind.OnNext, page.Kind);
        var undeclared = await visitor.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(address, new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea))
            .Materialize().Take(1).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.Equal(System.Reactive.NotificationKind.OnError, undeclared.Kind);

        // POSITIVE CONTROL: the same collection request is ANSWERED for a reader holding a grant, so the
        // visitor's refusal above is the gate's doing, not a request shape nobody may send.
        var admin = await ClientAs(new AccessContext { ObjectId = "alice", Name = "alice" })
            .Observe(new GetDataRequest(new CollectionReference(nameof(MeshNode))), o => o.WithTarget(address))
            .Select(d => d.Message).Materialize().Take(1).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.Equal(System.Reactive.NotificationKind.OnNext, admin.Kind);
    }

    private IMessageHub ClientAs(AccessContext caller)
    {
        var client = GetClient(config => ConfigureClient(config).AddLayoutClient());
        client.ServiceProvider.GetRequiredService<AccessService>().SetHostIdentity(caller);
        return client;
    }

    // ───────────────────────────── fixtures ─────────────────────────────

    private static MeshNode TypeNode(string path, PointReadDeclaration? declaration)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(path[(slash + 1)..], path[..slash])
        {
            NodeType = "NodeType",
            Name = path,
            Content = new NodeTypeDefinition { PointRead = declaration },
            HubConfiguration = config => config.WithContentType<TicketContent>().AddDefaultLayoutAreas(),
        };
    }

    private static MeshNode Node(string id, string subject, string nodeType, bool revoked, DateTimeOffset expiresAt, string ns = Partition)
        => new(id, ns)
        {
            NodeType = nodeType,
            Name = id,
            Content = new TicketContent(subject, expiresAt.ToString("O"), revoked ? DateTimeOffset.UtcNow.ToString("O") : null),
        };

    private Task SeedPartition(CancellationToken ct) => SeedTopLevel(new MeshNode(Partition) { NodeType = "Markdown", Name = "t" });

    private async Task<string> Issue(string id, string subject, CancellationToken ct,
        bool revoked = false, DateTimeOffset? expiresAt = null, string nodeType = DeclaredType, string ns = Partition)
    {
        var node = Node(id, subject, nodeType, revoked, expiresAt ?? DateTimeOffset.UtcNow.AddDays(1), ns);
        var created = await AsSystem(node, ct);
        Assert.Equal(WellKnownUsers.System, created.LastModifiedBy);
        return created.Path;
    }

    private async Task SeedStoredType(string partition, bool systemOwned, CancellationToken ct)
    {
        await SeedTopLevel(new MeshNode(partition) { NodeType = "Markdown", Name = partition });
        if (systemOwned)
            await AsSystem(new MeshNode(AccessAssignmentGuard.SyncConfigId, partition)
            {
                NodeType = "Markdown",
                Content = new JsonObject { ["repositoryUrl"] = $"https://github.com/example/{partition}" },
            }, ct);
        await AsSystem(new MeshNode("Ticket", partition)
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

    private async Task<MeshNode> AsSystem(MeshNode node, CancellationToken ct)
        => (await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateNode(node)).Should().Emit(cancellationToken: ct))!;

    private async Task AsSystemUpdate(string path, Func<TicketContent, TicketContent> change, CancellationToken ct)
        => await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream(path).Update(node =>
                node with { Content = change(node.ContentAs<TicketContent>(Mesh.JsonSerializerOptions)!) }))
            .Should().Emit(cancellationToken: ct);

    // ───────────────────────────── the seams ─────────────────────────────

    /// <summary>The RLS decision for <paramref name="caller"/>: a point read of the node, or the same node as a listing row.</summary>
    private Task<NodeValidationResult> Rls(AccessContext caller, string path, bool pointRead, CancellationToken ct, string nodeType = DeclaredType)
        => RlsDecision(caller, path, pointRead, nodeType).Should().Within(TimeSpan.FromSeconds(10)).Emit(cancellationToken: ct);

    private IObservable<NodeValidationResult> RlsDecision(AccessContext caller, string path, bool pointRead, string nodeType = DeclaredType)
    {
        var slash = path.LastIndexOf('/');
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<RlsNodeValidator>().Single();
        return validator.Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Read,
                Node = new MeshNode(path[(slash + 1)..], path[..slash]) { NodeType = nodeType },
                AccessContext = caller,
                IsPointRead = pointRead,
            })
            .Take(1);
    }

    private IObservable<string[]> PathsAs(AccessContext caller, string query) =>
        Mesh.ServiceProvider.GetRequiredService<AccessService>().RunAs(caller, () =>
                NodeFactory.Query<MeshNode>(MeshQueryRequest.FromQuery($"{query} select:path")))
            .Where(change => change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Select(change => (change.Items ?? []).Select(n => n.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray());

    private IObservable<MeshNode> StreamAs(AccessContext caller, string path) =>
        Mesh.ServiceProvider.GetRequiredService<AccessService>().RunAs(caller, () => Mesh.GetMeshNodeStream(path))
            .Where(n => n is not null)
            .Take(1)!;

    private IObservable<GetDataResponse> DataReadAs(AccessContext caller, string path)
    {
        var client = GetClient();
        client.ServiceProvider.GetRequiredService<AccessService>().SetHostIdentity(caller);
        return client.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Select(delivery => delivery.Message)
            .Take(1);
    }

    private static Task Within<T>(Func<IObservable<T>> read, Func<T, bool> condition, string because, CancellationToken ct)
        => Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => read().Take(1))
            .Where(condition)
            .Take(1)
            .Should().Within(TimeSpan.FromSeconds(20)).Emit(because, cancellationToken: ct);
}

/// <summary>
/// The instance-level anonymous switch (<c>Access:DenyAnonymous</c>) outranks a declared point read:
/// on an instance that states it, an Anonymous subject admits no logged-out caller.
/// </summary>
public class DeclaredPointReadDenyAnonymousTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .AddMeshNodes(new MeshNode("Ticket", "Kiosk")
        {
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { PointRead = new PointReadDeclaration { SubjectProperty = "subject", Namespace = "t" } },
            HubConfiguration = config => config.WithContentType<TicketContent>(),
        })
        .WithMeshType(typeof(TicketContent), nameof(TicketContent))
        .ConfigureServices(services => services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(
                ImmutableDictionary<string, string?>.Empty.Add(AnonymousAccess.DenyAnonymousConfigKey, "true")).Build()));

    [Fact(Timeout = 60000)]
    public async Task DenyAnonymous_OutranksTheDeclaration()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTopLevel(new MeshNode("t") { NodeType = "Markdown" });
        var node = (await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("a", "t")
            {
                NodeType = "Kiosk/Ticket",
                Content = new TicketContent(WellKnownUsers.Anonymous, null, null),
            })).Should().Emit(cancellationToken: ct))!;

        var anonymous = new AccessContext { ObjectId = WellKnownUsers.Anonymous };
        Assert.False(await DeclaredPointRead.Admits(Mesh, node.Path, anonymous).Take(1).Should().Emit(cancellationToken: ct),
            "an instance that denies anonymous access admits no logged-out caller, whatever a type declares");
        Assert.True(await DeclaredPointRead.Admits(Mesh, node.Path, new AccessContext { ObjectId = "dave" }).Take(1).Should().Emit(cancellationToken: ct),
            "POSITIVE CONTROL: a signed-in caller is in an Anonymous audience");
    }
}

/// <summary>The content of the test's declared node types — a REGISTERED type, so every hub that reads it can type it.</summary>
/// <param name="Subject">The admitted grant subject.</param>
/// <param name="ExpiresAt">The ISO instant the admission ends.</param>
/// <param name="RevokedAt">Set when revoked.</param>
public sealed record TicketContent(string? Subject, string? ExpiresAt, string? RevokedAt);
