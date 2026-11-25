using System.Reactive.Linq;
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
/// 🔒 <b><c>Role.IsInheritable</c> is honoured by the permission fold.</b> A custom role declared
/// <c>isInheritable: false</c> and assigned at <c>A</c> grants on <c>A</c> and NOTHING on
/// <c>A/B</c>; a default (inheritable) role assigned at <c>A</c> still grants on <c>A/B</c>.
/// Until this was pinned no code read the flag, so a non-inheritable role over-granted to every
/// descendant (triage fb-37f6a996). Group grants fold into the same per-scope role map, so the
/// group case is pinned too.
/// </summary>
public class NonInheritableRoleIsNotInheritedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Scope = "inhprobe";
    private const string Child = "inhprobe/folder";
    private const string Deep = "inhprobe/folder/doc";
    private const string Local = "local-reader";
    private const string Inh = "inheritable-reader";
    private const string ViaGroup = "group-member";
    private const string GroupId = "inh-readers";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => Bootstrap.Bootstrap(builder)
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    private async Task Seed(CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await meshService.CreateNode(new MeshNode(Scope)
        {
            Name = "Inheritance Probe", NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active, Content = new Space(),
        }).Should().Within(TestTimeouts.CrossSilo).Emit("the creator may create a Space", ct);

        using (access.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode("LocalOnlyReader", "Role")
            {
                Name = "Local only reader", NodeType = RoleNodeType.NodeType, State = MeshNodeState.Active,
                Content = new Role { Id = "LocalOnlyReader", Permissions = Permission.Read, IsInheritable = false },
            }).Should().Within(TestTimeouts.CrossSilo).Emit("a custom non-inheritable role", ct);
            await meshService.CreateNode(new MeshNode("InheritedReader", "Role")
            {
                Name = "Inherited reader", NodeType = RoleNodeType.NodeType, State = MeshNodeState.Active,
                Content = new Role { Id = "InheritedReader", Permissions = Permission.Read },
            }).Should().Within(TestTimeouts.CrossSilo).Emit("a custom default (inheritable) role", ct);

            await Grant(meshService, Local, "LocalOnlyReader", ct);
            await Grant(meshService, Inh, "InheritedReader", ct);
            await Grant(meshService, $"{Scope}/{GroupId}", "LocalOnlyReader", ct, GroupId);
            await meshService.CreateNode(new MeshNode(GroupId, Scope)
            {
                Name = GroupId, NodeType = "Group", MainNode = Scope,
                Content = new AccessObject { Description = "readers" },
            }).Should().Within(TestTimeouts.CrossSilo).Emit("a group", ct);
            await meshService.CreateNode(new MeshNode($"{ViaGroup}_Membership", $"{Scope}/{GroupId}")
            {
                Name = ViaGroup, NodeType = "GroupMembership", MainNode = Scope,
                Content = new GroupMembership
                {
                    Member = ViaGroup, DisplayName = ViaGroup,
                    Groups = [new MembershipEntry { Group = $"{Scope}/{GroupId}" }],
                },
            }).Should().Within(TestTimeouts.CrossSilo).Emit("a membership", ct);
        }
    }

    private async Task Grant(IMeshService meshService, string subject, string role, CancellationToken ct, string? id = null)
        => await meshService.CreateNode(new MeshNode($"{id ?? subject}_Access", $"{Scope}/_Access")
        {
            NodeType = "AccessAssignment", Name = $"{subject} Access", MainNode = Scope,
            Content = new AccessAssignment
            {
                AccessObject = subject, DisplayName = subject,
                Roles = [new RoleAssignment { Role = role, Denied = false }],
            },
        }).Should().Within(TestTimeouts.CrossSilo).Emit("the grant write must complete", ct);

    private async Task<Permission> At(string path, string user)
        => await Mesh.GetEffectivePermissions(path, user).Take(1).Timeout(TestTimeouts.CrossSilo).FirstAsync().Await();

    [Fact(Timeout = 180_000)]
    public async Task ANonInheritableRole_GrantsWhereAssigned_AndNothingOnDescendants()
    {
        await Seed(TestContext.Current.CancellationToken);
        (await At(Scope, Local)).HasFlag(Permission.Read).Should().BeTrue("the role is assigned at this very scope");
        (await At(Child, Local)).HasFlag(Permission.Read).Should().BeFalse("isInheritable:false stops at the scope it was assigned on");
        (await At(Deep, Local)).HasFlag(Permission.Read).Should().BeFalse("nor any deeper descendant");
    }

    [Fact(Timeout = 180_000)]
    public async Task ADefaultInheritableRole_StillGrantsOnDescendants()
    {
        await Seed(TestContext.Current.CancellationToken);
        (await At(Scope, Inh)).HasFlag(Permission.Read).Should().BeTrue();
        (await At(Child, Inh)).HasFlag(Permission.Read).Should().BeTrue("a default role is inheritable");
        (await At(Deep, Inh)).HasFlag(Permission.Read).Should().BeTrue();
    }

    [Fact(Timeout = 180_000)]
    public async Task ANonInheritableRoleGrantedToAGroup_IsNotInheritedByItsMembers()
    {
        await Seed(TestContext.Current.CancellationToken);
        (await At(Scope, ViaGroup)).HasFlag(Permission.Read).Should().BeTrue("the group grant applies where assigned");
        (await At(Child, ViaGroup)).HasFlag(Permission.Read).Should().BeFalse("a group grant of a non-inheritable role is not inherited either");
    }
}
