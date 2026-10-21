using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
        .AddMeshNodes(
            TypeNode(SealedType, declaration: null),
            TypeNode(SealedDeclaredType, Declaration),
            TypeNode(OpenType, declaration: null),
            TypeNode(SatType, declaration: null),
            TypeNode(StaleType, declaration: null),
            TypeNode(SwitchType, declaration: null),
            // erin is a PLATFORM admin: the Admin role in the Admin partition — no data grant anywhere.
            new MeshNode(PermissionEvaluator.AdminScope) { NodeType = "Markdown", Name = "Admin" },
            AssignmentNodeFactory.UserRole(Erin.ObjectId, "Admin", PermissionEvaluator.AdminScope),
            // carol holds Viewer on the rule partition — the fold grants her every node there.
            AssignmentNodeFactory.UserRole(Carol.ObjectId, "Viewer", RulePartition),
            // Readable by anyone in a browser, NOT through the API: the shape that makes the
            // API-token clamp observable (RoutedApiTokenClampTest).
            new MeshNode(CappedPartition) { NodeType = "Markdown", Name = CappedPartition },
            new MeshNode("Page", CappedPartition) { NodeType = "Markdown", Name = "Capped Page" },
            AssignmentNodeFactory.Policy(CappedPartition, new PartitionAccessPolicy { PublicRead = true, Api = false }))
        .ConfigureServices(services => services
            .AddSingleton<INodeTypeAccessRule>(new FaultingReadRule(FaultyType))
            .AddSingleton<INodeTypeAccessRule>(new FixedReadRule(SealedType, verdict: false))
            .AddSingleton<INodeTypeAccessRule>(new FixedReadRule(SealedDeclaredType, verdict: false))
            .AddSingleton<INodeTypeAccessRule>(new FixedReadRule(OpenType, verdict: true))
            .AddSingleton<INodeTypeAccessRule>(switchable)
            // The real satellite rule (Read delegates to the MainNode), held to the rule contract's
            // ONE verdict exactly as the lease does — a rule's own stream never makes a verdict live.
            .AddSingleton<INodeTypeAccessRule>(sp => new FirstVerdictRule(
                new MeshWeaver.Graph.Security.SatelliteAccessRule(SatType, sp.GetRequiredService<IMessageHub>())))
            // A rule whose FIRST answer was decided before a revocation landed (it answers true once),
            // then the real satellite rule — the subscription-establishment race, made deterministic.
            .AddSingleton<INodeTypeAccessRule>(sp => new FirstCallGrantsRule(
                new MeshWeaver.Graph.Security.SatelliteAccessRule(StaleType, sp.GetRequiredService<IMessageHub>()))))
        .AddSpaceType();

    /// <summary>A satellite type: Read is Read on the node's MainNode.</summary>
    private const string SatType = "Kiosk/Sat";
    /// <summary>A satellite type whose first evaluation predates a revocation.</summary>
    private const string StaleType = "Kiosk/Stale";
    private static readonly AccessContext Erin = new() { ObjectId = "erin", Name = "erin" };

    private sealed class FirstCallGrantsRule(INodeTypeAccessRule inner) : INodeTypeAccessRule
    {
        private int calls;
        public string NodeType => inner.NodeType;
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];
        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
            => Interlocked.Increment(ref calls) == 1 ? Observable.Return(true) : inner.HasAccess(context, userId).Take(1);
    }

    private sealed class FirstVerdictRule(INodeTypeAccessRule inner) : INodeTypeAccessRule
    {
        public string NodeType => inner.NodeType;
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];
        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId) => inner.HasAccess(context, userId).Take(1);
    }

    /// <summary>A type whose Read rule always DENIES.</summary>
    private const string SealedType = "Kiosk/Sealed";
    /// <summary>A type whose Read rule always DENIES and which DECLARES a point read.</summary>
    private const string SealedDeclaredType = "Kiosk/SealedDeclared";
    /// <summary>A type whose Read rule always GRANTS.</summary>
    private const string OpenType = "Kiosk/Open";
    private const string RulePartition = "r";
    private const string CappedPartition = "capped";

    private sealed class FixedReadRule(string nodeType, bool verdict) : INodeTypeAccessRule
    {
        public string NodeType => nodeType;
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];
        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId) => Observable.Return(verdict);
    }

    /// <summary>A type whose Read rule starts governing only when the test says so.</summary>
    private const string SwitchType = "Kiosk/Switch";
    private readonly SwitchableReadRule switchable = new(SwitchType);

    /// <summary>
    /// A DENYING Read rule whose governance of Read is switched on mid-subscription — what the rule
    /// set answers when a module's rule that was absent arrives (or a rule that could not be produced
    /// recovers): <see cref="NodeTypeAccessRuleSet"/> reads <c>SupportedOperations</c> on every lookup.
    /// </summary>
    private sealed class SwitchableReadRule(string nodeType) : INodeTypeAccessRule
    {
        private volatile bool governsRead;
        public void GovernRead() => governsRead = true;
        public string NodeType => nodeType;
        public IReadOnlyCollection<NodeOperation> SupportedOperations
            => governsRead ? [NodeOperation.Read] : [NodeOperation.Update];
        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId) => Observable.Return(false);
    }

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

    /// <summary>
    /// 🚨 MeshWeaver#4306, follow-up 4: the page's live verdict (<see cref="DeclaredPointRead.ObservePointRead"/>)
    /// decides like the RLS validator's point read — the type's Read rule first, the declaration only
    /// after a DEFINITIVE denial. Before the fix it asked the permission fold alone, so a denying rule
    /// was overridden by a grant, a granting rule was ignored, and an UNDETERMINED rule was re-decided
    /// through the declaration — the one thing no seam may do.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ThePagesVerdict_ConsultsTheTypesRule_LikeEveryPointReadSeam()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        await SeedTopLevel(new MeshNode(RulePartition) { NodeType = "Markdown", Name = RulePartition });

        var plain = await Issue("plain", "nobody", ct, nodeType: UndeclaredType, ns: RulePartition);
        Assert.True(await Verdict(plain, Carol, ct), "POSITIVE CONTROL: carol's Viewer grant admits her to a node no rule governs");

        var sealedNode = await Issue("sealed", "nobody", ct, nodeType: SealedType, ns: RulePartition);
        Assert.False((await Rls(Carol, sealedNode, true, ct, SealedType)).IsValid, "PRECONDITION: the RLS validator lets the denying rule outrank the grant");
        Assert.False(await Verdict(sealedNode, Carol, ct), "the page's verdict lets a DENYING rule outrank the fold's grant, as RLS does");

        var open = await Issue("open", "nobody", ct, nodeType: OpenType, ns: RulePartition);
        Assert.True((await Rls(Dave, open, true, ct, OpenType)).IsValid, "PRECONDITION: the RLS validator admits through the granting rule");
        Assert.True(await Verdict(open, Dave, ct), "the page's verdict admits through a GRANTING rule, as RLS does");
        Assert.False(await Verdict(sealedNode, Dave, ct), "NEGATIVE CONTROL: dave holds nothing and the rule denies");

        var faulty = await Issue("faulty-page", WellKnownUsers.Anonymous, ct, nodeType: FaultyType);
        Assert.False((await Rls(Visitor, faulty, true, ct, FaultyType)).IsValid, "PRECONDITION: RLS never re-decides an undetermined rule");
        Assert.False(await Verdict(faulty, Visitor, ct),
            "an UNDETERMINED rule is never re-decided through the declaration — although the node names the visitor's audience");

        var declared = await Issue("sealed-declared", WellKnownUsers.Anonymous, ct, nodeType: SealedDeclaredType);
        Assert.True((await Rls(Visitor, declared, true, ct, SealedDeclaredType)).IsValid, "PRECONDITION: RLS re-decides a DEFINITIVE rule denial through the declaration");
        Assert.True(await Verdict(declared, Visitor, ct), "a DEFINITIVE rule denial is re-decided through the declaration, as RLS does");
    }

    /// <summary>
    /// 🚨 MeshWeaver#4306, follow-up 3: the page's live verdict evaluates the permission fold AS THE
    /// VIEWER — its API-token flag included — never as whatever context is ambient. Before the fix a
    /// Bearer viewer was answered as a browser (the clamp never saw it), and a browser viewer was
    /// answered as a token whenever a token was ambient.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ThePagesVerdict_EvaluatesTheFoldAsTheViewer()
    {
        var ct = TestContext.Current.CancellationToken;
        const string page = CappedPartition + "/Page";
        var token = new AccessContext { ObjectId = Dave.ObjectId, Name = Dave.Name, IsApiToken = true };

        Assert.True(await Verdict(page, Dave, ct), "POSITIVE CONTROL: dave's browser reads a PublicRead page");
        Assert.False(await Verdict(page, token, ct), "dave's API TOKEN is clamped — the page is not reachable through the API");

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        Assert.True(await access.RunAs(token, () => DeclaredPointRead.ObservePointRead(Mesh, page, Dave)).Take(1)
                .Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct),
            "an AMBIENT token does not clamp a browser viewer — the verdict is about the viewer");
        Assert.False(await access.RunAs(Dave, () => DeclaredPointRead.ObservePointRead(Mesh, page, token)).Take(1)
                .Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct),
            "an AMBIENT browser does not unclamp a token viewer");
    }

    /// <summary>
    /// 🚨 Review of #7013 (thread 4333662636): the rule is selected and fed from the OWNER's current
    /// revision, never a stored snapshot. Repointing a satellite's MainNode from a node carol reads to
    /// a private one closes her read — on a fresh verdict right after the acknowledged write, and on
    /// the subscription that was already open.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ThePagesVerdict_FollowsTheOwnersRevision_OfTheNodeTheRuleReads()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTopLevel(new MeshNode(RulePartition) { NodeType = "Markdown", Name = RulePartition });
        await SeedTopLevel(new MeshNode("p") { NodeType = "Markdown", Name = "p" });
        await AsSystem(new MeshNode("doc", RulePartition) { NodeType = "Markdown", Name = "doc" }, ct);
        await AsSystem(new MeshNode("secret", "p") { NodeType = "Markdown", Name = "secret" }, ct);
        var sat = (await AsSystem(new MeshNode("sat", RulePartition) { NodeType = SatType, Name = "sat", MainNode = $"{RulePartition}/doc" }, ct)).Path;

        var open = DeclaredPointRead.ObservePointRead(Mesh, sat, Carol).Replay(1);
        using var connection = open.Connect();
        var tri = PointReadProbe.Open(Mesh, sat, Carol);
        using var triConnection = tri.Connect();
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v, "POSITIVE CONTROL: carol reads the satellite of a node she reads", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted, "POSITIVE CONTROL: the tri-state read grants first", cancellationToken: ct);

        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream(sat).Update(node => node with { MainNode = "p/secret" }))
            .Should().Emit(cancellationToken: ct);

        Assert.False(await Verdict(sat, Carol, ct), "a fresh verdict right after the acknowledged repoint reads the OWNER's revision");
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v,
            "the ALREADY-OPEN verdict re-decides the rule on the new revision", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the repoint to a private MainNode is honoured — a DEFINITIVE denial on the tri-state stream, not the pending withdrawal", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Review of #7013 (thread 4333662701): a rule answers ONE verdict, so the live path re-asks
    /// it when its inputs change. A grant that admits dave through the satellite rule, then revoked,
    /// withdraws the verdict on the SAME subscription.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ThePagesVerdict_ThroughARule_EndsWhenTheGrantIsRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTopLevel(new MeshNode("g") { NodeType = "Markdown", Name = "g" });
        await AsSystem(new MeshNode("doc", "g") { NodeType = "Markdown", Name = "doc" }, ct);
        var sat = (await AsSystem(new MeshNode("sat", "g") { NodeType = SatType, Name = "sat", MainNode = "g/doc" }, ct)).Path;
        Assert.False(await Verdict(sat, Dave, ct), "PRECONDITION: dave holds nothing on g");

        var grant = (await AsSystem(AssignmentNodeFactory.UserRole(Dave.ObjectId, "Viewer", "g"), ct)).Path;
        var open = DeclaredPointRead.ObservePointRead(Mesh, sat, Dave).Replay(1);
        using var connection = open.Connect();
        var tri = PointReadProbe.Open(Mesh, sat, Dave);
        using var triConnection = tri.Connect();
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v, "the grant admits dave through the rule", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted, "POSITIVE CONTROL: the tri-state read grants first", cancellationToken: ct);

        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.DeleteNode(grant))
            .Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);

        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v,
            "revoking the grant withdraws the rule's earlier verdict on the SAME subscription", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the revocation is honoured — a DEFINITIVE denial on the tri-state stream, not the pending withdrawal", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Review of #7013 (thread 4335537915): the governing rule is resolved on EVERY re-check, never
    /// captured with the owner revision. No rule governs Read at first, so carol's Viewer grant admits
    /// her through the fold; then a DENYING Read rule starts to govern the type — the node's revision
    /// unchanged — and an access commit on the partition re-checks. Capturing the rule with the
    /// revision re-asked the fold and granted again; the re-check must reach the rule and deny.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ARuleThatStartsToGovern_IsConsultedOnTheNextReCheck_OfTheSameRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTopLevel(new MeshNode(RulePartition) { NodeType = "Markdown", Name = RulePartition });
        var node = await Issue("switch", "nobody", ct, nodeType: SwitchType, ns: RulePartition);

        var tri = PointReadProbe.Open(Mesh, node, Carol);
        using var triConnection = tri.Connect();
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted,
            "POSITIVE CONTROL: no rule governs Read yet, so carol's Viewer grant admits her", cancellationToken: ct);

        switchable.GovernRead();
        Assert.False(await Verdict(node, Carol, ct), "PRECONDITION: a fresh verdict meets the rule that now governs, and it denies");

        // An access commit on the node's partition — the node's revision and carol's fold unchanged.
        await AsSystem(AssignmentNodeFactory.UserRole(Dave.ObjectId, "Viewer", RulePartition), ct);

        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the re-check consults the rule that governs NOW — a DEFINITIVE denial on the open subscription, not a fold grant re-asked under a captured \"no rule\"", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Review of #7013 (thread 4333751923): a fold's INITIAL value is a re-evaluation signal. The
    /// rule's first answer was decided before a revocation landed (it grants once); dave holds nothing,
    /// and the fold's first value already says so. Skipping that value left the open verdict granted
    /// with no later change to correct it.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ARevocationDuringSubscription_IsNotMissed()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTopLevel(new MeshNode("s") { NodeType = "Markdown", Name = "s" });
        await AsSystem(new MeshNode("doc", "s") { NodeType = "Markdown", Name = "doc" }, ct);
        var stale = (await AsSystem(new MeshNode("stale", "s") { NodeType = StaleType, Name = "stale", MainNode = "s/doc" }, ct)).Path;

        var tri = PointReadProbe.Open(Mesh, stale, Dave);
        using var triConnection = tri.Connect();
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the fold's first value (dave holds nothing) re-decides the rule's stale first answer — a DEFINITIVE denial on the tri-state stream, not the pending withdrawal", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Review of #7013 (thread 4333751987): <c>SpaceAccessRule</c> admits a platform admin to a
    /// Space while its <c>_GitSync</c> is one-way. Deleting that config withdraws the admission — the
    /// Space's revision and the admin's fold are unchanged, so only the config commit can say so — on
    /// the SAME subscription.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task APlatformAdminsSpaceRead_EndsWhenTheOneWaySyncGoes()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTopLevel(new MeshNode("owned") { NodeType = "Space", Name = "owned", Content = new MeshWeaver.Graph.Space() });
        var sync = (await AsSystem(new MeshNode(AccessAssignmentGuard.SyncConfigId, "owned")
        {
            NodeType = "Markdown",
            Content = new JsonObject { ["repositoryUrl"] = "https://github.com/example/owned" },
        }, ct)).Path;

        var open = DeclaredPointRead.ObservePointRead(Mesh, "owned", Erin).Replay(1);
        using var connection = open.Connect();
        var tri = PointReadProbe.Open(Mesh, "owned", Erin);
        using var triConnection = tri.Connect();
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v,
            "POSITIVE CONTROL: a platform admin reads a system-owned Space", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted, "POSITIVE CONTROL: the tri-state read grants first", cancellationToken: ct);
        Assert.False(await Verdict("owned", Dave, ct), "NEGATIVE CONTROL: a non-admin does not");

        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.DeleteNode(sync))
            .Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);

        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v,
            "removing the one-way sync withdraws the admin's read on the SAME subscription", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the one-way sync's removal is honoured — a DEFINITIVE denial on the tri-state stream, not the pending withdrawal", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Review of #7013 (thread 4333751951): every re-evaluation signal leaves its source gate as its
    /// OWN pool work item, in order, on a scheduler that pins no thread per subscription — never
    /// inline on the emitting call, never a long-lived <c>ObserveOn(TaskPoolScheduler.Default)</c>.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ReEvaluationSignals_LeaveTheSourceGate_AsPoolWorkItems()
    {
        var ct = TestContext.Current.CancellationToken;

        var source = new System.Reactive.Subjects.Subject<int>();
        var seen = DeclaredPointRead.OffTheGate(source)
            .Select(value => (value, pooled: Thread.CurrentThread.IsThreadPoolThread))
            .Take(3).ToList().Replay(1);
        using var connection = seen.Connect();
        // The source emits on a DEDICATED (non-pool) thread, so a delivery inline on the emitting
        // call would report IsThreadPoolThread = false.
        new Thread(() =>
        {
            source.OnNext(1);
            source.OnNext(2);
            source.OnNext(3);
        }) { IsBackground = true }.Start();
        var delivered = await seen.Should().Within(TimeSpan.FromSeconds(10)).Emit(cancellationToken: ct);
        Assert.Equal([1, 2, 3], delivered.Select(d => d.value));
        Assert.All(delivered, d => Assert.True(d.pooled, "each signal is delivered as a pool work item, never inline on the emitting call"));
    }

    private Task<bool> Verdict(string path, AccessContext viewer, CancellationToken ct)
        => DeclaredPointRead.ObservePointRead(Mesh, path, viewer).Take(1)
            .Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);

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

    /// <summary>
    /// 🚨 Review of #6935 (thread 4306187851): a DIRECT node subscription is decided about the caller's
    /// trusted context WITH its flags. A virtual (logged-out) circuit — <c>guest-…</c>, IsVirtual — is
    /// refused a <c>Public</c>-audience node, however it is addressed; a signed-in circuit is admitted.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ADirectSubscription_KeepsTheCircuitsFlags()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var link = await Issue("public-only", WellKnownUsers.Public, ct);
        var address = new Address(link);

        var guest = new AccessContext { ObjectId = "guest-9c41", Name = "Guest", IsVirtual = true };
        var refused = await DirectSubscribe(ClientAs(guest), address).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.Equal(System.Reactive.NotificationKind.OnError, refused.Kind);

        var admitted = await DirectSubscribe(ClientAs(Dave), address).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.Equal(System.Reactive.NotificationKind.OnNext, admitted.Kind);
    }

    /// <summary>
    /// 🚨 Review of #6935 (thread 4306187851), the CIRCUIT source: a delivery carrying NO context whose
    /// identity therefore comes from the receiving hub's circuit — a VIRTUAL guest — is decided about
    /// that circuit context WHOLE, so the guest stays logged-out and is refused a <c>Public</c>
    /// audience; an identity carried only as a string (<c>SubscribeRequest.Identity</c>) has no trusted
    /// context and consults no declaration at all. POSITIVE CONTROL: a signed-in circuit is admitted.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AContextlessDelivery_IsDecidedAboutTheCircuitWithItsFlags()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartition(ct);
        var link = await Issue("public-circuit", WellKnownUsers.Public, ct);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        IMessageDelivery Delivery(string? identity = null)
            => new MessageDelivery<SubscribeRequest>(
                new SubscribeRequest("s", new MeshNodeReference()) { Identity = identity },
                new PostOptions(new Address("client", "x")).WithTarget(new Address(link)),
                Mesh.JsonSerializerOptions);

        access.SetHostIdentity(new AccessContext { ObjectId = "guest-c0de", Name = "Guest", IsVirtual = true });
        var guest = MeshWeaver.Hosting.Security.AccessControlPipeline.ResolveCaller(Delivery(), access);
        Assert.True(guest?.IsVirtual, "the circuit's context is carried WHOLE — the guest stays virtual");
        Assert.False(await DeclaredPointRead.Admits(Mesh, link, guest).Take(1).Should().Emit(cancellationToken: ct),
            "a virtual circuit is not in a Public audience");
        Assert.Null(MeshWeaver.Hosting.Security.AccessControlPipeline.ResolveCaller(Delivery(identity: "dave"), access));

        access.SetHostIdentity(Dave);
        var signedIn = MeshWeaver.Hosting.Security.AccessControlPipeline.ResolveCaller(Delivery(), access);
        Assert.True(await DeclaredPointRead.Admits(Mesh, link, signedIn).Take(1).Should().Emit(cancellationToken: ct),
            "POSITIVE CONTROL: a signed-in circuit is in a Public audience");
    }

    /// <summary>A raw node subscription to the node's own hub — the shape that bypasses MeshDataSource's GetData-only validator.</summary>
    internal static IObservable<System.Reactive.Notification<IMessageDelivery<SubscribeAck>>> DirectSubscribe(IMessageHub client, Address address)
        => client.Observe(new SubscribeRequest($"direct-{Guid.NewGuid():N}", new MeshNodeReference()), o => o.WithTarget(address))
            .Materialize()
            .Take(1);

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

        // A DIRECT subscription from a VIRTUAL circuit is refused under the switch (thread 4306187851).
        var client = GetClient();
        client.ServiceProvider.GetRequiredService<AccessService>()
            .SetHostIdentity(new AccessContext { ObjectId = "guest-41aa", Name = "Guest", IsVirtual = true });
        var refused = await DeclaredPointReadTest.DirectSubscribe(client, new Address(node.Path))
            .Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);
        Assert.Equal(System.Reactive.NotificationKind.OnError, refused.Kind);
    }
}

/// <summary>
/// 🚨 Review of #7013 (threads 4333751887, 4333874256, 4334011102, 4334011138): each re-evaluation of
/// a rule is bounded by the permission-establishment budget, and a pending re-check withdraws the
/// grant. The rule here GRANTS until the verdict is established (every initial signal answered),
/// then the test switches it into an explicit STALLED phase — so nothing but the commits the test
/// makes can start a re-check. A short mesh timeout keeps the budget at 1.5 s.
/// </summary>
public class DeclaredPointReadBudgetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string StallType = "Kiosk/Stall";
    private static readonly AccessContext Dave = new() { ObjectId = "dave", Name = "dave" };

    /// <summary>
    /// The initial evaluations of ONE subscription: the first check, the first value of each watched
    /// fold (<see cref="DeclaredPointRead.FoldScopes"/>) and the first value of the role catalog.
    /// </summary>
    private static int InitialEvaluations(MeshNode node) => 1 + DeclaredPointRead.FoldScopes(node).Count + 1;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .WithMeshType(typeof(TicketContent), nameof(TicketContent))
        .AddMeshNodes(new MeshNode("Stall", "Kiosk")
        {
            NodeType = "NodeType",
            Name = StallType,
            Content = new NodeTypeDefinition(),
            HubConfiguration = config => config.WithContentType<TicketContent>(),
        })
        .ConfigureServices(services => services
            .AddSingleton<ControlledRule>()
            .AddSingleton<INodeTypeAccessRule>(sp => sp.GetRequiredService<ControlledRule>())
            .AddSingleton(new MeshOperationOptions { Timeout = TimeSpan.FromSeconds(6) }));

    /// <summary>
    /// Grants until <see cref="Stall"/>; afterwards never answers. Records each verdict it has
    /// DELIVERED, correlated to the subscription that asked by its viewer context — each subscription
    /// in these tests is given its own <see cref="AccessContext"/> instance, which the live read
    /// hands every evaluation it makes as <see cref="NodeValidationContext.AccessContext"/>.
    /// </summary>
    private sealed class ControlledRule : INodeTypeAccessRule
    {
        private int stalled;
        private readonly ReplaySubject<AccessContext?> delivered = new();
        public string NodeType => StallType;
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];
        public void Stall() => Volatile.Write(ref stalled, 1);

        /// <summary>The number of verdicts delivered to the subscription whose viewer is <paramref name="viewer"/> (by reference), live.</summary>
        public IObservable<int> DeliveredTo(AccessContext viewer)
            => delivered.Where(v => ReferenceEquals(v, viewer)).Select((_, index) => index + 1).StartWith(0);

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
            => Volatile.Read(ref stalled) == 1
                ? Observable.Never<bool>()
                // Counted when the verdict is DELIVERED, never when the evaluation merely starts.
                : Observable.Return(true).Do(_ => delivered.OnNext(context.AccessContext));
    }

    private ControlledRule Rule => Mesh.ServiceProvider.GetRequiredService<ControlledRule>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>Creates the node, opens both verdicts, waits until they are ESTABLISHED (granted, every initial signal answered), then stalls the rule.</summary>
    private async Task<(IConnectableObservable<bool> open, IConnectableObservable<PermissionCheckOutcome> tri, IDisposable connections)> EstablishThenStall(
        string partition, CancellationToken ct)
    {
        await SeedTopLevel(new MeshNode(partition) { NodeType = "Markdown", Name = partition });
        var node = (await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("stall", partition) { NodeType = StallType, Name = "stall" }))
            .Should().Emit(cancellationToken: ct))!;
        // One viewer INSTANCE per subscription: the rule's delivered verdicts are correlated by it.
        var openViewer = new AccessContext { ObjectId = Dave.ObjectId, Name = Dave.Name };
        var triViewer = new AccessContext { ObjectId = Dave.ObjectId, Name = Dave.Name };
        var initial = InitialEvaluations(node);
        var open = DeclaredPointRead.ObservePointRead(Mesh, node.Path, openViewer).Replay(1);
        var tri = PointReadProbe.Open(Mesh, node.Path, triViewer);
        var connections = new System.Reactive.Disposables.CompositeDisposable(open.Connect(), tri.Connect());
        // ESTABLISHED, per subscription: every one of ITS initial evaluations has delivered its verdict.
        await Rule.DeliveredTo(openViewer).Should().Within(TimeSpan.FromSeconds(20)).Match(n => n >= initial,
            $"the page subscription's {initial} initial evaluations have all delivered their grants", cancellationToken: ct);
        await Rule.DeliveredTo(triViewer).Should().Within(TimeSpan.FromSeconds(20)).Match(n => n >= initial,
            $"the tri-state subscription's {initial} initial evaluations have all delivered their grants", cancellationToken: ct);
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v, "established: the page grants", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted, "established: granted", cancellationToken: ct);
        Rule.Stall();
        return (open, tri, connections);
    }

    /// <summary>
    /// After the stalled phase begins, ONE acknowledged access commit starts a re-check that never
    /// answers. The grant is withdrawn at once (pending — a separate assertion), and the re-check's
    /// own DEADLINE then classifies it: the timeout-classified undetermined outcome, specifically.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AStalledReEvaluation_EndsInTheDeadlinesVerdict()
    {
        var ct = TestContext.Current.CancellationToken;
        var (open, tri, connections) = await EstablishThenStall("k", ct);
        using var _ = connections;

        await Access.RunAsSystem(() => NodeFactory.CreateNode(AssignmentNodeFactory.UserRole("someone", "Viewer", "k")))
            .Should().Within(TimeSpan.FromSeconds(20)).Emit("the access commit is acknowledged", cancellationToken: ct);

        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsPending,
            "the commit withdraws the grant while its re-check is pending", cancellationToken: ct);
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v, "the page's verdict is withdrawn", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsTimedOut,
            "the stalled re-check ends in the DEADLINE's verdict — undetermined, classified from its timeout", cancellationToken: ct);
    }

    /// <summary>
    /// After the stalled phase begins, access commits arrive every 300 ms for 3 s — twice the 1.5 s
    /// budget, each ACKNOWLEDGED (a writer error fails the test). The grant is withdrawn within the
    /// first budget, while the commits are still arriving, and is never granted again while they
    /// continue.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SignalsFasterThanTheBudget_CannotKeepAnOldGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (open, tri, connections) = await EstablishThenStall("q", ct);
        using var _ = connections;

        const int commits = 10;
        var outcomes = tri.Replay();
        using var recording = outcomes.Connect();
        var writer = Observable.Interval(TimeSpan.FromMilliseconds(300)).Take(commits)
            .Select(i => Access.RunAsSystem(() => NodeFactory.CreateNode(AssignmentNodeFactory.UserRole($"storm{i}", "Viewer", "q"))).Take(1))
            .Concat()
            .ToList()
            .Replay(1);
        using var writing = writer.Connect();

        await open.Should().Within(TimeSpan.FromMilliseconds(1200)).Match(v => !v,
            "the grant is withdrawn within the first budget, while the commits keep arriving", cancellationToken: ct);
        var written = await writer.Should().Within(TimeSpan.FromSeconds(20)).Emit("the writer's commits, each acknowledged", cancellationToken: ct);
        Assert.Equal(commits, written.Count);

        var seen = await outcomes.TakeUntil(Observable.Return(0L, System.Reactive.Concurrency.Scheduler.Default)).ToList()
            .Should().Within(TimeSpan.FromSeconds(10)).Emit(cancellationToken: ct);
        var afterWithdrawal = seen.SkipWhile(o => !PointReadProbe.IsPending(o)).ToList();
        Assert.NotEmpty(afterWithdrawal);
        Assert.DoesNotContain(afterWithdrawal, o => o.IsGranted);
    }
}

/// <summary>
/// The live standard-read path's signals that the mesh's own state does not replay: the Admin fold
/// converging after the commit, a declared feed GAP, and a deletion whose event carries no node type
/// (review of #7013, threads 4333874317, 4333874386, 4333874432). An invalidation feed that can lose,
/// gap and strip events stands in for a cross-process feed.
/// </summary>
public class DeclaredPointReadFeedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Ops = "Groups/ops";
    private static readonly AccessContext Erin = new() { ObjectId = "erin", Name = "erin" };
    private static readonly AccessContext Frank = new() { ObjectId = "frank", Name = "frank" };
    private static readonly AccessContext Gina = new() { ObjectId = "gina", Name = "gina" };

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .AddMeshNodes(
            new MeshNode(PermissionEvaluator.AdminScope) { NodeType = "Markdown", Name = "Admin" },
            AssignmentNodeFactory.UserRole(Erin.ObjectId, "Admin", PermissionEvaluator.AdminScope),
            // The ops GROUP is a platform admin; gina is admin only through her membership.
            AssignmentNodeFactory.UserRole(Ops, "Admin", PermissionEvaluator.AdminScope),
            // The storage partition: `sb/_Storage` breaks inheritance, so a binding's own fold never
            // carries the root's grants — only the root fold does.
            new MeshNode("sb") { NodeType = "Markdown", Name = "sb" },
            AssignmentNodeFactory.Policy(MeshWeaver.Mesh.Storage.StorageBindingPaths.NamespaceOf("sb"), new PartitionAccessPolicy { BreaksInheritance = true }),
            // A satellite type: Read is Read on the MainNode, decided through the permission fold.
            new MeshNode("Sat", "Kiosk") { NodeType = "NodeType", Name = SatType, Content = new NodeTypeDefinition() })
        .AddSpaceType()
        .ConfigureServices(services => services
            .AddSingleton<MeshWeaver.Mesh.Storage.IInstanceStore>(sp => new MeshWeaver.Graph.Storage.DirectoryInstanceStore(
                StoreId, "Test files", StoreRoot(), [MeshWeaver.Mesh.Storage.StoragePurpose.DocParts],
                sp.GetRequiredService<MeshWeaver.Mesh.Threading.IoPoolRegistry>()))
            .AddSingleton(sp => new LossyFeed(sp.GetRequiredService<MeshWeaver.Hosting.InProcessMeshChangeFeed>()))
            .AddSingleton<IMeshInvalidationFeed>(sp => sp.GetRequiredService<LossyFeed>())
            // The node-stream cache, able to HOLD one security query's refresh (see HoldableQueryCache).
            .AddSingleton(sp => new HoldableQueryCache(
                sp.GetRequiredService<MeshWeaver.Hosting.MeshNodeStreamCache>(), SecurityQueries.PartitionAssignmentsQueryId(HeldPartition)))
            .AddSingleton<IMeshNodeStreamCache>(sp => sp.GetRequiredService<HoldableQueryCache>())
            .AddSingleton<INodeTypeAccessRule>(sp => new OneVerdict(
                new MeshWeaver.Graph.Security.SatelliteAccessRule(SatType, sp.GetRequiredService<IMessageHub>()))));

    private const string SatType = "Kiosk/Sat";
    private static readonly AccessContext Hank = new() { ObjectId = "hank", Name = "hank" };
    private static readonly AccessContext Ivan = new() { ObjectId = "ivan", Name = "ivan" };

    private sealed class OneVerdict(INodeTypeAccessRule inner) : INodeTypeAccessRule
    {
        public string NodeType => inner.NodeType;
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];
        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId) => inner.HasAccess(context, userId).Take(1);
    }

    /// <summary>The process feed, able to lose an event, strip its node type, or declare a gap.</summary>
    private sealed class LossyFeed(IMeshInvalidationFeed inner) : IMeshInvalidationFeed
    {
        private ImmutableHashSet<string> dropped = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
        private ImmutableHashSet<string> untyped = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Reactive.Subjects.Subject<ChangeFeedGap> gaps = new();

        public void Drop(string path) => ImmutableInterlocked.Update(ref dropped, set => set.Add(path));
        public void StripType(string path) => ImmutableInterlocked.Update(ref untyped, set => set.Add(path));
        public void DeclareGap() => gaps.OnNext(new ChangeFeedGap("test", DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, "injected"));

        public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
            => inner.Subscribe(change =>
            {
                if (Volatile.Read(ref dropped).Contains(change.Path))
                    return;
                handler(Volatile.Read(ref untyped).Contains(change.Path) ? change with { NodeType = null } : change);
            }, filter);

        public IObservable<ChangeFeedGap> Gaps => inner.Gaps.Merge(gaps);
    }

    /// <summary>The partition whose assignment query the test can hold.</summary>
    private const string HeldPartition = "hr";
    private static readonly AccessContext Lena = new() { ObjectId = "lena", Name = "lena" };

    /// <summary>
    /// 🚨 The test's CONTROLLED SWITCH for "the security-query cache has not refreshed yet". It wraps
    /// the mesh's real <see cref="IMeshNodeStreamCache"/> and gates ONE cached security query (by its
    /// process-wide id): every subscriber of that id — a long-lived permission watcher and a fresh
    /// re-check alike — is served from one shared value that follows the real query while the switch
    /// is open. While it is HELD the real query keeps refreshing underneath, but every subscriber, a
    /// new one included, keeps seeing the value from before the hold; on release the refreshed value
    /// is served. That is exactly the window in which a commit's re-check reads the assignments from
    /// before the commit. A mesh-scoped instance, with no sleep and no wait primitive: the test flips it.
    /// </summary>
    private sealed class HoldableQueryCache(IMeshNodeStreamCache inner, string heldId) : IMeshNodeStreamCache, IDisposable
    {
        private HeldQuery? held;

        public void Hold() => Held().Hold();
        public void Release() => Held().Release();

        private HeldQuery Held() => Volatile.Read(ref held)
            ?? throw new InvalidOperationException($"nothing has subscribed '{heldId}' yet — there is no value to hold");

        public IObservable<MeshNode> GetStream(string path, JsonSerializerOptions options) => inner.GetStream(path, options);
        public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update, JsonSerializerOptions options) => inner.Update(path, update, options);
        public IObservable<MeshNode> Overwrite(string path, MeshNode node, JsonSerializerOptions options) => inner.Overwrite(path, node, options);
        public void Invalidate(string path) => inner.Invalidate(path);
        public bool ReleaseIfUnwatched(string path) => inner.ReleaseIfUnwatched(path);
        void IMeshNodeStreamCache.InvalidatePartition(string partition) => inner.InvalidatePartition(partition);

        IObservable<IEnumerable<MeshNode>>? IMeshNodeStreamCache.GetQuery(object id)
            => Equals(id, heldId) && Volatile.Read(ref held) is { } h ? h.Stream : inner.GetQuery(id);

        IObservable<IEnumerable<MeshNode>> IMeshNodeStreamCache.GetQuery(object id, JsonSerializerOptions options, params string[] queries)
        {
            if (!Equals(id, heldId))
                return inner.GetQuery(id, options, queries);
            if (Volatile.Read(ref held) is null)
            {
                var created = new HeldQuery(inner.GetQuery(id, options, queries));
                if (Interlocked.CompareExchange(ref held, created, null) is not null)
                    created.Dispose();
            }
            return Volatile.Read(ref held)!.Stream;
        }

        public void Dispose() => Volatile.Read(ref held)?.Dispose();

        /// <summary>
        /// The gate for one query. Live values, holds and releases are ONE serialized stream (Merge
        /// delivers one event at a time) folded by Scan, so what is served is decided in event order and
        /// a release can never publish a snapshot that a refresh already superseded (review of #7013,
        /// thread 4335721574): a release serves the latest live value as of the release event itself.
        /// </summary>
        public sealed class HeldQuery : IDisposable
        {
            private readonly Subject<bool> holds = new();
            private readonly IConnectableObservable<IEnumerable<MeshNode>> served;
            private readonly IDisposable connection;

            public HeldQuery(IObservable<IEnumerable<MeshNode>> live)
            {
                served = Observable.Merge(
                        live.Select(value => (Value: (IEnumerable<MeshNode>?)value, Held: (bool?)null)),
                        holds.Select(isHeld => (Value: (IEnumerable<MeshNode>?)null, Held: (bool?)isHeld)))
                    .Scan((Latest: (IEnumerable<MeshNode>?)null, IsHeld: false, Serve: (IEnumerable<MeshNode>?)null),
                        (state, e) => e.Held switch
                        {
                            null => (e.Value, state.IsHeld, state.IsHeld ? null : e.Value),
                            true => (state.Latest, true, null),
                            false => (state.Latest, false, state.Latest),
                        })
                    .Where(state => state.Serve is not null)
                    .Select(state => state.Serve!)
                    .Replay(1);
                connection = served.Connect();
            }

            public IObservable<IEnumerable<MeshNode>> Stream => served;

            public void Hold() => holds.OnNext(true);

            public void Release() => holds.OnNext(false);

            public void Dispose() => connection.Dispose();
        }
    }

    private HoldableQueryCache Cache => Mesh.ServiceProvider.GetRequiredService<HoldableQueryCache>();

    /// <summary>
    /// The seam's own contract (review of #7013, thread 4335721574), event by event: while held, new
    /// subscribers and old ones keep the pre-hold value; a release serves the latest live value; a
    /// refresh after the release supersedes it, and a release after a refresh never re-serves an
    /// older snapshot. Ordering is decided by the one serialized event stream, not by timing.
    /// </summary>
    [Fact]
    public void TheHoldSeam_ServesInEventOrder()
    {
        var live = new Subject<IEnumerable<MeshNode>>();
        using var gate = new HoldableQueryCache.HeldQuery(live);
        MeshNode[] Of(string id) => [new MeshNode(id)];
        string? Served()
        {
            string? last = null;
            using (gate.Stream.Subscribe(v => last = v.Single().Id, _ => { }))
                return last;
        }

        live.OnNext(Of("granted"));
        Assert.Equal("granted", Served());
        gate.Hold();
        live.OnNext(Of("revoked"));
        Assert.Equal("granted", Served());
        gate.Release();
        Assert.Equal("revoked", Served());
        live.OnNext(Of("refreshed"));
        Assert.Equal("refreshed", Served());
        gate.Release();
        Assert.Equal("refreshed", Served());
        gate.Hold();
        gate.Release();
        Assert.Equal("refreshed", Served());
    }

    /// <summary>
    /// 🚨 Thread 4335537984: a permission watcher whose mask was computed under a role's OLD definition
    /// cannot see a revocation of that role's assignment. lena's custom role grants nothing, so her
    /// watchers hold <c>None</c>. The role gains Read and the role catalog's signal re-decides the
    /// verdict to Granted, but the watchers are not recomputed. Her assignment is then revoked while the
    /// assignment query has NOT refreshed (held): the commit's re-check reads the old assignment and
    /// grants again. When the query converges the watchers compute <c>None</c> once more, a non-change,
    /// so nothing re-decides and the open verdict stays Granted although a fresh check denies. The
    /// watchers must follow role definitions, so that convergence is a CHANGE (Read → None) that re-decides.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ARevocationReadStale_AfterARolePromotion_IsCorrectedWhenTheQueryConverges()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Same(Cache, Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>());
        await SeedTopLevel(new MeshNode(HeldPartition) { NodeType = "Markdown", Name = HeldPartition });
        await SeedTopLevel(new MeshNode("hrroles") { NodeType = "Markdown", Name = "hrroles" });
        var role = (await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("Grow", "hrroles")
        {
            NodeType = SecurityQueries.RoleNodeType,
            Name = "Grow",
            Content = new Role { Id = "Grow", Permissions = Permission.None },
        })).Should().Emit(cancellationToken: ct))!.Path;
        var page = (await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("page", HeldPartition) { NodeType = "Markdown", Name = "page" }))
            .Should().Emit(cancellationToken: ct))!.Path;
        var grant = (await Access.RunAsSystem(() => NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Lena.ObjectId, "Grow", HeldPartition)))
            .Should().Emit(cancellationToken: ct))!.Path;

        // Every outcome, in order. Each wait reads only what was emitted AFTER its step (Since), so an
        // earlier outcome can never satisfy a later assertion.
        var tri = DeclaredPointRead.ObserveStandardRead(Mesh, page, Lena).Replay();
        using var triConnection = tri.Connect();
        var seen = 0;
        using var counter = tri.Subscribe(_ => Interlocked.Increment(ref seen), _ => { });
        IObservable<PermissionCheckOutcome> Since() => tri.Skip(Volatile.Read(ref seen));

        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "PRECONDITION: the role grants nothing, so lena is denied", cancellationToken: ct);

        var afterPromotion = Since();
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(role).Update(node => node with
            {
                Content = node.ContentAs<Role>(Mesh.JsonSerializerOptions)! with { Permissions = Permission.Read },
            }))
            .Should().Within(TimeSpan.FromSeconds(20)).Emit("the role gains Read — acknowledged", cancellationToken: ct);
        await afterPromotion.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted,
            "POSITIVE CONTROL: the role catalog's signal re-decides the verdict to Granted", cancellationToken: ct);

        Cache.Hold();
        var afterRevocation = Since();
        await Delete(grant, ct);
        await Mesh.GetEffectivePermissions(page, Lena.ObjectId).Take(1).Should().Within(TimeSpan.FromSeconds(20))
            .Match(p => p.HasFlag(Permission.Read), "PRECONDITION: while held, a fresh fold still reads the revoked assignment — the hold is real", cancellationToken: ct);
        await afterRevocation.SkipWhile(o => !PointReadProbe.IsPending(o)).Where(o => o.IsGranted)
            .Should().Within(TimeSpan.FromSeconds(20)).Emit(
                "PRECONDITION: the revocation's re-check ran and read the assignment from before the commit", cancellationToken: ct);

        var afterConvergence = Since();
        Cache.Release();
        await Mesh.GetEffectivePermissions(page, Lena.ObjectId).Take(1).Should().Within(TimeSpan.FromSeconds(20))
            .Match(p => !p.HasFlag(Permission.Read), "PRECONDITION: once released, a fresh fold sees the revocation", cancellationToken: ct);
        await afterConvergence.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the assignment query's convergence re-decides the open verdict — a DEFINITIVE denial on the SAME subscription, not a grant read stale and never corrected", cancellationToken: ct);
    }

    private const string StoreId = "files";
    private readonly string storeRoot = Path.Combine(Path.GetTempPath(), "mw-pointread-" + Guid.NewGuid().ToString("N")[..8]);

    private string StoreRoot()
    {
        Directory.CreateDirectory(Path.Combine(storeRoot, "sb"));
        return storeRoot;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(storeRoot, recursive: true); }
        catch (IOException) { }
    }

    private static readonly AccessContext Jill = new() { ObjectId = "jill", Name = "jill" };
    private static readonly AccessContext Kate = new() { ObjectId = "kate", Name = "kate" };

    private LossyFeed Feed => Mesh.ServiceProvider.GetRequiredService<LossyFeed>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private async Task<string> SystemOwnedSpace(string name, CancellationToken ct)
    {
        await SeedTopLevel(new MeshNode(name) { NodeType = "Space", Name = name, Content = new MeshWeaver.Graph.Space() });
        return (await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode(AccessAssignmentGuard.SyncConfigId, name)
        {
            NodeType = "Markdown",
            Content = new JsonObject { ["repositoryUrl"] = $"https://github.com/example/{name}" },
        })).Should().Emit(cancellationToken: ct))!.Path;
    }

    private Task Delete(string path, CancellationToken ct)
        => Access.RunAsSystem(() => NodeFactory.DeleteNode(path)).Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);

    private static IConnectableObservable<bool> Open(IMessageHub mesh, string path, AccessContext viewer)
        => DeclaredPointRead.ObservePointRead(mesh, path, viewer).Replay(1);

    /// <summary>
    /// 🚨 Thread 4333874317: <c>SpaceAccessRule</c> reads the platform-admin predicate, i.e. the
    /// viewer's live <c>Admin</c> fold. Here the revoking commit never reaches this process's feed
    /// (the commit-triggered check had nothing new to read); only the Admin fold, converging, can
    /// say the admin grant is gone — and it withdraws the verdict on the same subscription.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnAdminRevocation_SeenOnlyByTheAdminFold_EndsTheRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await SystemOwnedSpace("owned1", ct);
        var grant = (await Access.RunAsSystem(() => NodeFactory.CreateNode(
            AssignmentNodeFactory.UserRole(Frank.ObjectId, "Admin", PermissionEvaluator.AdminScope))).Should().Emit(cancellationToken: ct))!.Path;

        var open = Open(Mesh, "owned1", Frank);
        using var connection = open.Connect();
        var tri = PointReadProbe.Open(Mesh, "owned1", Frank);
        using var triConnection = tri.Connect();
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v, "POSITIVE CONTROL: frank, a platform admin, reads the system-owned Space", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted, "POSITIVE CONTROL: the tri-state read grants first", cancellationToken: ct);

        Feed.Drop(grant);
        await Delete(grant, ct);
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v,
            "the Admin fold's convergence alone withdraws the read", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the admin revocation is honoured — a DEFINITIVE denial on the tri-state stream, not the pending withdrawal", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Thread 4334011007: the fold resolves a custom role from ONE value of the role catalog and
    /// does not re-emit when a definition changes. Here the role's commit never reaches this process's
    /// feed (its re-check would have read the old role); only the role catalog's convergence can say
    /// the role lost Read — and the existing subscription ends in a DEFINITIVE denial.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ARoleLosingRead_IsHonouredWhenTheRoleCatalogConverges()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTopLevel(new MeshNode("rp") { NodeType = "Markdown", Name = "rp" });
        await SeedTopLevel(new MeshNode("rd") { NodeType = "Markdown", Name = "rd" });
        var role = (await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("Peek", "rp")
        {
            NodeType = SecurityQueries.RoleNodeType,
            Name = "Peek",
            Content = new Role { Id = "Peek", Permissions = Permission.Read },
        })).Should().Emit(cancellationToken: ct))!.Path;
        await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("doc", "rd") { NodeType = "Markdown", Name = "doc" }))
            .Should().Emit(cancellationToken: ct);
        var sat = (await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("sat", "rd") { NodeType = SatType, Name = "sat", MainNode = "rd/doc" }))
            .Should().Emit(cancellationToken: ct))!.Path;
        await Access.RunAsSystem(() => NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Hank.ObjectId, "Peek", "rd")))
            .Should().Emit(cancellationToken: ct);

        var open = Open(Mesh, sat, Hank);
        using var connection = open.Connect();
        var tri = PointReadProbe.Open(Mesh, sat, Hank);
        using var triConnection = tri.Connect();
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v, "POSITIVE CONTROL: the custom role grants Read", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted, "POSITIVE CONTROL: the tri-state read grants first", cancellationToken: ct);

        Feed.Drop(role);
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(role).Update(node => node with
            {
                Content = node.ContentAs<Role>(Mesh.JsonSerializerOptions)! with { Permissions = Permission.None },
            }))
            .Should().Within(TimeSpan.FromSeconds(20)).Emit("the role loses Read — acknowledged", cancellationToken: ct);

        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v, "the page's verdict is withdrawn", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the role catalog's convergence re-decides the rule — a DEFINITIVE denial, not the pending withdrawal", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Thread 4334078688: the DELETION form of a role change. ivan is a platform admin only through
    /// a custom role <c>operator</c> (Permission.All) granted on <c>Admin</c>, and reads a system-owned
    /// Space through that. The role node lives at <c>Roles/operator</c> — outside every scope the
    /// typeless-deletion filter watches — and its deletion arrives TYPELESS. The role catalog's own
    /// emission re-decides the read, and the same subscription SETTLES on a definitive denial.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ACustomRoleDeletedOutsideTheScopes_EndsTheRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await SystemOwnedSpace("owned4", ct);
        await SeedTopLevel(new MeshNode("Roles") { NodeType = "Markdown", Name = "Roles" });
        var role = (await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("operator", "Roles")
        {
            NodeType = SecurityQueries.RoleNodeType,
            Name = "operator",
            Content = new Role { Id = "operator", Permissions = Permission.All },
        })).Should().Emit(cancellationToken: ct))!.Path;
        await Access.RunAsSystem(() => NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Ivan.ObjectId, "operator", PermissionEvaluator.AdminScope)))
            .Should().Emit(cancellationToken: ct);
        await Mesh.IsGlobalAdmin(Ivan.ObjectId).Should().Within(TimeSpan.FromSeconds(20)).Match(a => a,
            "PRECONDITION: the custom role makes ivan a platform admin", cancellationToken: ct);

        var open = Open(Mesh, "owned4", Ivan);
        using var connection = open.Connect();
        var tri = PointReadProbe.Open(Mesh, "owned4", Ivan);
        using var triConnection = tri.Connect();
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v, "POSITIVE CONTROL: ivan reads the system-owned Space", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted, "POSITIVE CONTROL: the tri-state read grants first", cancellationToken: ct);

        Feed.StripType(role);
        await Delete(role, ct);

        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the role's deletion SETTLES on a definitive denial — not just the pending withdrawal", cancellationToken: ct);
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v, "the page's verdict is withdrawn", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Thread 4334180374: the NO-RULE branch (a plain Markdown node) shares the input set. jill reads
    /// it only through a custom role; the role's commit is lost, the role then loses Read — and the
    /// same subscription SETTLES on a definitive denial once the role catalog converges.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ANoRuleNode_ReadThroughACustomRole_EndsWhenTheRoleLosesRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var (page, role) = await NoRulePageReadThroughARole("nr1", "Look1", ct);
        var (open, tri, connections) = await OpenGranted(page, Jill, ct);
        using var _ = connections;

        Feed.Drop(role);
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(role).Update(node => node with
            {
                Content = node.ContentAs<Role>(Mesh.JsonSerializerOptions)! with { Permissions = Permission.None },
            }))
            .Should().Within(TimeSpan.FromSeconds(20)).Emit("the role loses Read — acknowledged", cancellationToken: ct);

        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "a no-rule node's read SETTLES on a definitive denial when the role loses Read", cancellationToken: ct);
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v, "the page's verdict is withdrawn", cancellationToken: ct);
    }

    /// <summary>🚨 Thread 4334180374, the deletion form: the custom role is deleted, typeless and outside the node's scopes.</summary>
    [Fact(Timeout = 60000)]
    public async Task ANoRuleNode_ReadThroughACustomRole_EndsWhenTheRoleIsDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (page, role) = await NoRulePageReadThroughARole("nr2", "Look2", ct);
        var (open, tri, connections) = await OpenGranted(page, Jill, ct);
        using var _ = connections;

        Feed.StripType(role);
        await Delete(role, ct);

        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "a no-rule node's read SETTLES on a definitive denial when the role is deleted", cancellationToken: ct);
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v, "the page's verdict is withdrawn", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Thread 4334180426: <c>StorageBindingAccessRule</c> decides Read from Update on the PARTITION
    /// ROOT. The binding's MainNode is itself and <c>sb/_Storage/_Policy</c> breaks inheritance, so the
    /// binding's own fold never moves. kate's root Editor grant is revoked with its commit lost: only
    /// the root fold converging can say so — and the same subscription SETTLES on a definitive denial.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AStorageBindingRead_EndsWhenTheRootGrantIsRevoked_WithInheritanceBroken()
    {
        var ct = TestContext.Current.CancellationToken;
        var grant = (await Access.RunAsSystem(() => NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Kate.ObjectId, "Editor", "sb")))
            .Should().Emit(cancellationToken: ct))!.Path;
        var bindingPath = $"{MeshWeaver.Mesh.Storage.StorageBindingPaths.NamespaceOf("sb")}/files";
        var binding = (await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("files", MeshWeaver.Mesh.Storage.StorageBindingPaths.NamespaceOf("sb"))
        {
            NodeType = MeshWeaver.Mesh.Storage.StorageBindingPaths.NodeType,
            Name = "files",
            MainNode = bindingPath,
            Content = new MeshWeaver.Mesh.Storage.StorageBinding { Purpose = MeshWeaver.Mesh.Storage.StoragePurpose.DocParts, StoreId = StoreId, Container = "sb" },
        })).Should().Emit(cancellationToken: ct))!;
        Assert.Equal(bindingPath, binding.MainNode);
        await Mesh.GetEffectivePermissions(bindingPath, Kate.ObjectId).Should().Within(TimeSpan.FromSeconds(20))
            .Match(p => !p.HasFlag(Permission.Read), "PRECONDITION: inheritance is broken — the binding's own fold grants kate nothing", cancellationToken: ct);

        var (open, tri, connections) = await OpenGranted(bindingPath, Kate, ct);
        using var _ = connections;

        Feed.Drop(grant);
        await Delete(grant, ct);

        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the root fold's convergence re-decides the binding read — a SETTLED definitive denial", cancellationToken: ct);
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v, "the page's verdict is withdrawn", cancellationToken: ct);
    }

    /// <summary>A Markdown page in <paramref name="partition"/> that jill reads only through custom role <paramref name="roleId"/> (Read), defined OUTSIDE the partition.</summary>
    private async Task<(string page, string role)> NoRulePageReadThroughARole(string partition, string roleId, CancellationToken ct)
    {
        await SeedTopLevel(new MeshNode(partition) { NodeType = "Markdown", Name = partition });
        await SeedTopLevel(new MeshNode($"{partition}roles") { NodeType = "Markdown", Name = $"{partition}roles" });
        var role = (await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode(roleId, $"{partition}roles")
        {
            NodeType = SecurityQueries.RoleNodeType,
            Name = roleId,
            Content = new Role { Id = roleId, Permissions = Permission.Read },
        })).Should().Emit(cancellationToken: ct))!.Path;
        var page = (await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("page", partition) { NodeType = "Markdown", Name = "page" }))
            .Should().Emit(cancellationToken: ct))!.Path;
        await Access.RunAsSystem(() => NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Jill.ObjectId, roleId, partition)))
            .Should().Emit(cancellationToken: ct);
        return (page, role);
    }

    private async Task<(IConnectableObservable<bool> open, IConnectableObservable<PermissionCheckOutcome> tri, IDisposable connections)> OpenGranted(
        string path, AccessContext viewer, CancellationToken ct)
    {
        var open = Open(Mesh, path, viewer);
        var tri = PointReadProbe.Open(Mesh, path, viewer);
        var connections = new System.Reactive.Disposables.CompositeDisposable(open.Connect(), tri.Connect());
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v, "POSITIVE CONTROL: the viewer reads the node", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted, "POSITIVE CONTROL: the tri-state read grants first", cancellationToken: ct);
        return (open, tri, connections);
    }

    /// <summary>
    /// 🚨 Thread 4333874386: a GAP declares that commits were lost. The one-way sync's deletion is
    /// lost here (the open verdict stays granted — the loss is real), and the gap withdraws it.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AFeedGap_ReDecidesTheRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var sync = await SystemOwnedSpace("owned2", ct);

        var open = Open(Mesh, "owned2", Erin);
        using var connection = open.Connect();
        var tri = PointReadProbe.Open(Mesh, "owned2", Erin);
        using var triConnection = tri.Connect();
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v, "POSITIVE CONTROL: erin, a platform admin, reads the system-owned Space", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted, "POSITIVE CONTROL: the tri-state read grants first", cancellationToken: ct);

        Feed.Drop(sync);
        await Delete(sync, ct);
        Assert.False(await DeclaredPointRead.ObservePointRead(Mesh, "owned2", Erin).Take(1)
                .Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct),
            "PRECONDITION: a fresh check already refuses — the sync is gone");

        Feed.DeclareGap();
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v,
            "the gap re-decides the open verdict on the same subscription", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the lost sync deletion is honoured after the gap — a DEFINITIVE denial on the tri-state stream, not the pending withdrawal", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 Thread 4333874432: the delete pipeline publishes a deletion with NO node type. gina is a
    /// platform admin only through her group membership; that membership's deletion arrives typeless,
    /// and the open verdict is withdrawn on the same subscription.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ATypelessMembershipDeletion_EndsTheRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await SystemOwnedSpace("owned3", ct);
        await SeedTopLevel(new MeshNode("Groups") { NodeType = "Markdown", Name = "Groups" });
        var membership = (await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("gina_Membership", Ops)
        {
            NodeType = "GroupMembership",
            Content = new GroupMembership { Member = Gina.ObjectId, Groups = [new MembershipEntry { Group = Ops }] },
        })).Should().Emit(cancellationToken: ct))!.Path;

        var open = Open(Mesh, "owned3", Gina);
        using var connection = open.Connect();
        var tri = PointReadProbe.Open(Mesh, "owned3", Gina);
        using var triConnection = tri.Connect();
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => v, "POSITIVE CONTROL: gina is a platform admin through her membership", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(o => o.IsGranted, "POSITIVE CONTROL: the tri-state read grants first", cancellationToken: ct);

        Feed.StripType(membership);
        await Delete(membership, ct);
        await open.Should().Within(TimeSpan.FromSeconds(20)).Match(v => !v,
            "the typeless deletion of her membership withdraws the read", cancellationToken: ct);
        await tri.Should().Within(TimeSpan.FromSeconds(20)).Match(PointReadProbe.IsDefinitiveDenial,
            "the membership's deletion is honoured — a DEFINITIVE denial on the tri-state stream, not the pending withdrawal", cancellationToken: ct);
    }

    /// <summary>
    /// The filter itself: a typeless DELETION anywhere is a signal (it may have been a membership or
    /// a role); access inputs outside the node's scopes and ordinary content changes are not, so an
    /// open page does not blink for them.
    /// </summary>
    [Fact]
    public void TheSignalFilter_IsTight_ButCountsTypelessDeletions()
    {
        var scopes = DeclaredPointRead.ReadScopes(new MeshNode("x", "acme/space") { MainNode = "acme/doc" });
        Assert.True(DeclaredPointRead.AffectsRead(MeshChangeEvent.Deleted("Groups/ops/gina_Membership"), scopes), "a typeless MEMBERSHIP deletion, wherever its group lives");
        Assert.True(DeclaredPointRead.AffectsRead(MeshChangeEvent.Deleted("acme/space/x"), scopes), "the node itself, deleted typeless");
        Assert.True(DeclaredPointRead.AffectsRead(MeshChangeEvent.Deleted("acme/doc"), scopes), "its MainNode, deleted typeless");
        Assert.False(DeclaredPointRead.AffectsRead(MeshChangeEvent.Deleted("other/page"), scopes),
            "an UNRELATED typeless deletion elsewhere — the delete pipeline publishes every deletion typeless, so this must not blink open pages");
        Assert.False(DeclaredPointRead.AffectsRead(MeshChangeEvent.Deleted("acme/space/sibling"), scopes),
            "a typeless deletion of a SIBLING is no access input");
        Assert.True(DeclaredPointRead.AffectsRead(MeshChangeEvent.Deleted("acme/_GitSync"), scopes), "a sync config on the node's partition, typeless");
        Assert.True(DeclaredPointRead.AffectsRead(MeshChangeEvent.Deleted("acme/space/_Access/bob_Access"), scopes), "a grant on an ancestor, typeless");
        Assert.True(DeclaredPointRead.AffectsRead(MeshChangeEvent.Deleted("Admin/_Access/bob_Access"), scopes), "a grant on Admin");
        Assert.False(DeclaredPointRead.AffectsRead(MeshChangeEvent.Deleted("other/_Access/bob_Access"), scopes), "a grant on an unrelated scope");
        Assert.False(DeclaredPointRead.AffectsRead(MeshChangeEvent.Deleted("other/_GitSync"), scopes), "a sync config elsewhere");
        Assert.False(DeclaredPointRead.AffectsRead(new MeshChangeEvent("acme", "page", "acme/page", MeshChangeKind.Updated, "Markdown", 2, DateTimeOffset.UtcNow), scopes),
            "an ordinary content change");
        Assert.False(DeclaredPointRead.AffectsRead(new MeshChangeEvent("other", "page", "other/page", MeshChangeKind.Updated, null, 2, DateTimeOffset.UtcNow), scopes),
            "a typeless UPDATE is not a deletion");
    }
}

/// <summary>
/// 🚨 The contract of <see cref="DeclaredPointRead.ObserveStandardRead"/>: ANY input a Read decision
/// reads must be one of its signals. Every <see cref="INodeTypeAccessRule"/> in the platform assemblies
/// this suite loads is listed here WITH the inputs its Read reads, each checked against the input set.
/// A NEW rule fails this test until someone reads it and adds its line; a rule that left fails it too.
/// It cannot see what a rule reads (that is the review this forces), and it sees only the assemblies in
/// this suite's output — rules in MeshWeaver.Plugins are outside it.
/// </summary>
public class ReadInputSetGuardTest
{
    /// <summary>Rule type → what its Read consults. Every entry must be covered by the input set in AccessControl.md.</summary>
    private static readonly ImmutableDictionary<string, string> Reviewed = new Dictionary<string, string>
    {
        ["MeshWeaver.Graph.Security.SatelliteAccessRule"] = "fold on MainNode (or the node itself)",
        ["MeshWeaver.Graph.Storage.StorageBindingAccessRule"] = "fold on the partition ROOT (Update)",
        ["MeshWeaver.Graph.SpaceNodeType+SpaceAccessRule"] = "fold on the node; Admin fold (IsGlobalAdmin); {space}/_GitSync content (feed commit)",
        ["MeshWeaver.GitSync.GitHubSyncConfigAccessRule"] = "fold on the node; Admin fold (IsGlobalAdmin)",
        ["MeshWeaver.Graph.Configuration.UserNodeType+UserAccessRule"] = "path shape; fold on the node",
        ["MeshWeaver.Graph.Configuration.PartitionNodeType+PartitionAccessRule"] = "fold on the node",
        ["MeshWeaver.Graph.Configuration.VUserNodeType+VUserAccessRule"] = "caller address only",
        ["MeshWeaver.Graph.Configuration.StepUpNodeTypes+SystemOnlyAccessRule"] = "caller identity only",
        ["MeshWeaver.Graph.Configuration.NodeAccessRuleSet+FunctionalAccessRule"] = "synchronous predicates over the context only",
        ["MeshWeaver.Mesh.Services.NodeTypeAccessRuleSet+DenyingRule"] = "refuses — reads nothing",
        ["MeshWeaver.Mesh.Services.NodeTypeAccessRuleSet+DenyingPermissionRule"] = "refuses — reads nothing",
        ["MeshWeaver.Mesh.Services.NodeTypeAccessRuleSet+LeasedAccessRule"] = "forwards to a module rule (one verdict)",
        ["MeshWeaver.Mesh.Services.NodeTypeAccessRuleSet+LeasedPermissionRule"] = "forwards to a module rule (one verdict)",
    }.ToImmutableDictionary();

    [Fact]
    public void EveryPlatformAccessRule_IsReviewedAgainstTheInputSet()
    {
        var assemblies = new[]
        {
            typeof(INodeTypeAccessRule).Assembly,
            typeof(MeshWeaver.Graph.Security.SatelliteAccessRule).Assembly,
            typeof(MeshWeaver.GitSync.GitHubSyncConfigAccessRule).Assembly,
        };
        var found = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(INodeTypeAccessRule).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .ToImmutableSortedSet();
        var unreviewed = found.Except(Reviewed.Keys);
        Assert.True(unreviewed.IsEmpty,
            "an access rule nobody has checked against the live read's input set — read what its Read consults, make sure "
            + "every input is a signal of DeclaredPointRead.ObserveStandardRead, then list it here: " + string.Join(", ", unreviewed));
        var gone = Reviewed.Keys.Except(found).ToArray();
        Assert.True(gone.Length == 0, "a reviewed rule that no longer exists — remove its line: " + string.Join(", ", gone));
    }

    [Fact]
    public void TheFoldScopes_CoverNodeMainNodeTheirRootsAndAdmin()
    {
        var scopes = DeclaredPointRead.FoldScopes(new MeshNode("files", "sb/_Storage") { MainNode = "acme/doc" });
        Assert.Equal(["sb/_Storage/files", "acme/doc", "sb", "acme", PermissionEvaluator.AdminScope], scopes);
    }
}

/// <summary>
/// The TRI-STATE live standard read (<see cref="DeclaredPointRead.ObserveStandardRead"/>), held open
/// beside a page's boolean verdict: the boolean maps a pending re-check to <c>false</c>, so a
/// revocation test that reads only the boolean can pass on the withdrawal before the re-check has
/// answered. A revocation is proven by a DEFINITIVE denial here.
/// </summary>
internal static class PointReadProbe
{
    public static IConnectableObservable<PermissionCheckOutcome> Open(IMessageHub mesh, string path, AccessContext viewer)
        => DeclaredPointRead.ObserveStandardRead(mesh, path, viewer).Replay(1);

    /// <summary>A verdict reached and refused — neither granted nor undetermined (pending, failed or timed out).</summary>
    public static bool IsDefinitiveDenial(PermissionCheckOutcome outcome) => !outcome.IsGranted && !outcome.IsUndetermined;

    /// <summary>The undetermined outcome a per-evaluation DEADLINE produces (classified from its TimeoutException).</summary>
    public static bool IsTimedOut(PermissionCheckOutcome outcome)
        => outcome.IsUndetermined && outcome.UndeterminedReason!.Contains(nameof(TimeoutException), StringComparison.Ordinal);

    /// <summary>The withdrawal a re-check signal produces while that re-check is pending.</summary>
    public static bool IsPending(PermissionCheckOutcome outcome)
        => outcome.IsUndetermined && outcome.UndeterminedReason!.Contains("being re-decided", StringComparison.Ordinal);
}

/// <summary>The content of the test's declared node types — a REGISTERED type, so every hub that reads it can type it.</summary>
/// <param name="Subject">The admitted grant subject.</param>
/// <param name="ExpiresAt">The ISO instant the admission ends.</param>
/// <param name="RevokedAt">Set when revoked.</param>
public sealed record TicketContent(string? Subject, string? ExpiresAt, string? RevokedAt);
