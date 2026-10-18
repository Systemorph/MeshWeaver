using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
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

    private static readonly PointReadDeclaration Declaration = new()
    {
        SubjectProperty = "subject",
        RevokedProperty = "revokedAt",
        ExpiresProperty = "expiresAt",
    };

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .AddMeshNodes(
            TypeNode(DeclaredType, Declaration),
            TypeNode(UndeclaredType, declaration: null),
            // carol is a member of the group an audience may name; dave is not.
            new MeshNode("carol_Membership", Group)
            {
                NodeType = "GroupMembership",
                Content = new GroupMembership { Member = Carol.ObjectId, Groups = [new MembershipEntry { Group = Group }] },
            },
            // alice may write in the partition — but a node SHE writes is not platform-issued.
            AssignmentNodeFactory.UserRole("alice", "Editor", Partition));

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

        // Revoking the admitted node ends the point read at every seam.
        await AsSystemUpdate(open, content => content["revokedAt"] = DateTimeOffset.UtcNow.ToString("O"), ct);
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

    // ───────────────────────────── fixtures ─────────────────────────────

    private static MeshNode TypeNode(string path, PointReadDeclaration? declaration)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(path[(slash + 1)..], path[..slash])
        {
            NodeType = "NodeType",
            Name = path,
            Content = new NodeTypeDefinition { PointRead = declaration },
            HubConfiguration = config => config,
        };
    }

    private static MeshNode Node(string id, string subject, string nodeType, bool revoked, DateTimeOffset expiresAt)
        => new(id, Partition)
        {
            NodeType = nodeType,
            Name = id,
            Content = new JsonObject
            {
                ["subject"] = subject,
                ["expiresAt"] = expiresAt.ToString("O"),
                ["revokedAt"] = revoked ? DateTimeOffset.UtcNow.ToString("O") : null,
            },
        };

    private Task SeedPartition(CancellationToken ct) => SeedTopLevel(new MeshNode(Partition) { NodeType = "Markdown", Name = "t" });

    private async Task<string> Issue(string id, string subject, CancellationToken ct,
        bool revoked = false, DateTimeOffset? expiresAt = null, string nodeType = DeclaredType)
    {
        var node = Node(id, subject, nodeType, revoked, expiresAt ?? DateTimeOffset.UtcNow.AddDays(1));
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
                    ["revokedProperty"] = "revokedAt",
                    ["expiresProperty"] = "expiresAt",
                },
            },
        }, ct);
    }

    private async Task<MeshNode> AsSystem(MeshNode node, CancellationToken ct)
        => (await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateNode(node)).Should().Emit(cancellationToken: ct))!;

    private async Task AsSystemUpdate(string path, Action<JsonObject> change, CancellationToken ct)
        => await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream(path).Update(node =>
            {
                var content = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(node.Content, Mesh.JsonSerializerOptions))!.AsObject();
                change(content);
                return node with { Content = content };
            }))
            .Should().Emit(cancellationToken: ct);

    // ───────────────────────────── the seams ─────────────────────────────

    /// <summary>The RLS decision for <paramref name="caller"/>: a point read of the node, or the same node as a listing row.</summary>
    private Task<NodeValidationResult> Rls(AccessContext caller, string path, bool pointRead, CancellationToken ct)
        => RlsDecision(caller, path, pointRead).Should().Within(TimeSpan.FromSeconds(10)).Emit(cancellationToken: ct);

    private IObservable<NodeValidationResult> RlsDecision(AccessContext caller, string path, bool pointRead)
    {
        var slash = path.LastIndexOf('/');
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<RlsNodeValidator>().Single();
        return validator.Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Read,
                Node = new MeshNode(path[(slash + 1)..], path[..slash]) { NodeType = DeclaredType },
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
            Content = new NodeTypeDefinition { PointRead = new PointReadDeclaration { SubjectProperty = "subject" } },
            HubConfiguration = config => config,
        })
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
                Content = new JsonObject { ["subject"] = WellKnownUsers.Anonymous },
            })).Should().Emit(cancellationToken: ct))!;

        var anonymous = new AccessContext { ObjectId = WellKnownUsers.Anonymous };
        Assert.False(await DeclaredPointRead.Admits(Mesh, node.Path, anonymous).Take(1).Should().Emit(cancellationToken: ct),
            "an instance that denies anonymous access admits no logged-out caller, whatever a type declares");
        Assert.True(await DeclaredPointRead.Admits(Mesh, node.Path, new AccessContext { ObjectId = "dave" }).Take(1).Should().Emit(cancellationToken: ct),
            "POSITIVE CONTROL: a signed-in caller is in an Anonymous audience");
    }
}
