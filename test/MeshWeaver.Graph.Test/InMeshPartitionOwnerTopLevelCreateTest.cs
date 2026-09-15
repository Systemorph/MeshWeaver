using System.Reactive.Linq;
using MeshWeaver.Fixture;
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
/// 🚨 <b>A top-level instance of a partition-owning NodeType declared in mesh CONTENT can be
/// created by an ordinary signed-in user — exactly as a Space can.</b>
///
/// <para><b>The defect.</b> <c>Crm/Client</c> declares <c>ownsPartition: true</c> in its
/// <see cref="NodeTypeDefinition"/>, and the CRM guide's recipe for a new client is a top-level
/// <c>create</c>. On memex.systemorph.com that create was refused for EVERYONE — platform admins
/// included — with <c>Access denied: Create permission required for node 'Notus'</c>. Three places
/// knew what "owns a partition" means, and all three only asked the STATIC registry:</para>
/// <list type="number">
///   <item><c>RlsNodeValidator</c>: only <c>Space</c> carries an access rule that lets an
///     authenticated identity create a top-level instance; any other owning type fell to the
///     standard check on its own path, where no grant can exist before the node does.</item>
///   <item><c>PartitionWriteGuardValidator</c> rule 3 resolved ownership through
///     <c>FindStaticNode</c>, which cannot see a type compiled from mesh content, so the create
///     would have been refused as "must be a Space" even past RLS.</item>
///   <item><c>OwnsPartitionProvisioningValidator</c> used the same lookup, so the partition's
///     schema would never have been provisioned, and nothing granted the creator Admin.</item>
/// </list>
///
/// <para>The type here is created at RUNTIME as a persisted <c>NodeType</c> node — never through
/// <c>AddMeshNodes</c>, which would make it static and let every one of the three lookups pass by
/// accident. The creator is an identity that holds NOTHING anywhere; that it can then write a
/// child is the proof that it became the partition's Admin and that the partition is routable.</para>
///
/// <para>Security fixture: <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>, NOT the default
/// <c>ConfigureMesh</c>, which adds public admin access and would make every refusal below pass
/// trivially.</para>
/// </summary>
public class InMeshPartitionOwnerTopLevelCreateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypeLibrary = "ownertypes";
    private const string OwnerTypeId = "Partner";
    private const string NonOwnerTypeId = "Widget";
    private const string OwnerType = TypeLibrary + "/" + OwnerTypeId;
    private const string NonOwnerType = TypeLibrary + "/" + NonOwnerTypeId;

    /// <summary>An authenticated identity with no grant anywhere — the ordinary-user shape.</summary>
    private static readonly AccessContext Probe = new() { ObjectId = "ownerprobe", Name = "Owner Probe" };

    /// <summary>The not-logged-in caller. It arrives NAMED, which is why "authenticated" must never
    /// be spelled as "has a user id".</summary>
    private static readonly AccessContext Nobody = new() { ObjectId = WellKnownUsers.Anonymous, Name = "Anonymous" };

    private static readonly TimeSpan RefusalBudget = TimeSpan.FromSeconds(60);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>
    /// The type library: a Space holding two NodeType definitions persisted at runtime — one that
    /// owns its partition and one that does not. Written as System (the platform installs types).
    /// </summary>
    private async Task SeedTypes()
    {
        await SeedTopLevel(new MeshNode(TypeLibrary)
        {
            Name = "Owner Types",
            NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new Space(),
        });

        foreach (var (id, owns) in new[] { (OwnerTypeId, true), (NonOwnerTypeId, false) })
            await Access.RunAsSystem(() => MeshService.CreateNode(new MeshNode(id, TypeLibrary)
                {
                    Name = id,
                    NodeType = MeshNode.NodeTypePath,
                    State = MeshNodeState.Active,
                    Content = new NodeTypeDefinition { OwnsPartition = owns, DefaultNamespace = owns ? "" : null },
                }))
                .Should().Within(TestTimeouts.CrossSilo).Emit(
                    $"the platform can persist the NodeType definition '{TypeLibrary}/{id}'",
                    cancellationToken: TestContext.Current.CancellationToken);

        Mesh.ServiceProvider.FindStaticNode(OwnerType).Should().BeNull(
            "the premise: the owning type is NOT static. If it were, every lookup that only asks "
            + "the static registry would pass and this test would prove nothing");
    }

    private ValueTask<Exception?> RefusedAs(AccessContext who, MeshNode node, CancellationToken ct) =>
        Record.ExceptionAsync(() => Access.RunAs(who, () => MeshService.CreateNode(node))
            .Take(1)
            .Timeout(RefusalBudget)
            .Await(ct));

    /// <summary>
    /// THE property: an ordinary user creates a top-level instance of an in-mesh owning type, and
    /// then — holding nothing but what that create gave them — writes a child into it.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnOrdinaryUser_CreatesATopLevelInstanceOfAnInMeshOwningType_AndOwnsIt()
    {
        await SeedTypes();

        await Access.RunAs(Probe, () => MeshService.CreateNode(new MeshNode("acmepartner")
            {
                Name = "Acme Partner",
                NodeType = OwnerType,
                State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "a type that declares ownsPartition may be created at the top level by any "
                + "authenticated identity — the rule Space already has, keyed on the declaration, "
                + "not on the type's name or on whether it happens to be registered in src/",
                cancellationToken: TestContext.Current.CancellationToken);

        await Access.RunAs(Probe, () => MeshService.CreateNode(new MeshNode("page", "acmepartner")
            {
                Name = "Page",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "the creator holds NOTHING outside the new partition, so writing a child proves "
                + "three things at once: its schema was provisioned, it is routable, and the creator "
                + "was granted Admin on it — the 'Create permission required for node "
                + "'{id}/page'' an ownerless partition answers",
                cancellationToken: TestContext.Current.CancellationToken);

        var queryCore = Mesh.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        var grant = await queryCore
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:acmepartner/_Access/{Probe.ObjectId}_Access"), Mesh.JsonSerializerOptions)
            .Take(1)
            .Should().Within(TestTimeouts.CrossSilo).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var assignment = grant.Items.Single().ContentAs<AccessAssignment>(Mesh.JsonSerializerOptions);
        assignment!.Roles.Should().ContainSingle(r => r.Role == Role.Admin.Id && !r.Denied,
            "the creator becomes the partition's Admin, as a Space's creator does");

        var definition = await queryCore
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{PartitionNodeType.Namespace}/acmepartner"), Mesh.JsonSerializerOptions)
            .Take(1)
            .Should().Within(TestTimeouts.CrossSilo).Emit(cancellationToken: TestContext.Current.CancellationToken);
        definition.Items.Should().ContainSingle(
            "the partition is announced to routing with its Admin/Partition/{id} definition, "
            + "exactly as a Space's is — without it the partition is half-provisioned");
    }

    /// <summary>Control: a type that does NOT own a partition is still refused at the top level.</summary>
    [Fact(Timeout = 180_000)]
    public async Task ANonOwningInMeshType_IsStillRefusedAtTheTopLevel()
    {
        await SeedTypes();

        var failure = await RefusedAs(Probe, new MeshNode("loosewidget")
        {
            Name = "Loose Widget",
            NodeType = NonOwnerType,
            State = MeshNodeState.Active,
        }, TestContext.Current.CancellationToken);

        failure.Should().NotBeNull(
            "top-level ⟺ owns a partition: content never lands at the root as a bare node");
    }

    /// <summary>Control: an unauthenticated caller cannot create a partition, owning type or not.</summary>
    [Fact(Timeout = 180_000)]
    public async Task AnAnonymousCaller_CannotCreateAnOwningInstance()
    {
        await SeedTypes();

        var failure = await RefusedAs(Nobody, new MeshNode("anonpartner")
        {
            Name = "Anonymous Partner",
            NodeType = OwnerType,
            State = MeshNodeState.Active,
        }, TestContext.Current.CancellationToken);

        failure.Should().NotBeNull(
            "'any authenticated identity' excludes the logged-out caller, who arrives named "
            + WellKnownUsers.Anonymous);
    }

    /// <summary>
    /// Control: Space is unchanged — still creatable by an ordinary user, and granted ONCE. Space is
    /// static and has its own post-creation handler; the generic in-mesh path must not run for it
    /// too, or the second grant would collide with the first and fail the create.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ASpace_IsUnchanged_AndGrantedExactlyOnce()
    {
        await Access.RunAs(Probe, () => MeshService.CreateNode(new MeshNode("probespace")
            {
                Name = "Probe Space",
                NodeType = SpaceNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new Space(),
            }))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "Space's own access rule still lets an ordinary user create one",
                cancellationToken: TestContext.Current.CancellationToken);

        var grants = await Mesh.ServiceProvider.GetRequiredService<IMeshQueryCore>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                "namespace:probespace/_Access nodeType:AccessAssignment"), Mesh.JsonSerializerOptions)
            .Take(1)
            .Should().Within(TestTimeouts.CrossSilo).Emit(cancellationToken: TestContext.Current.CancellationToken);
        grants.Items.Should().ContainSingle("one creator grant, from Space's own handler");
    }
}
