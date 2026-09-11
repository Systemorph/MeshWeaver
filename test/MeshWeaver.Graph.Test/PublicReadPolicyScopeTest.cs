using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>Public policy grants obey scope precedence, as the SQL prefix fold already does.</summary>
public class PublicReadPolicyScopeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Reader = "scope-policy-reader";

    // The usual test configuration grants Public Admin. That would invalidate these controls.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            new MeshNode("PublicPolicy") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("PublicPolicy", new PartitionAccessPolicy { PublicRead = true }),
            new MeshNode("Page", "PublicPolicy") { NodeType = "Markdown" },
            new MeshNode("Private", "PublicPolicy") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("PublicPolicy/Private", new PartitionAccessPolicy { Read = false }),
            new MeshNode("Page", "PublicPolicy/Private") { NodeType = "Markdown" },
            new MeshNode("Isolated", "PublicPolicy") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("PublicPolicy/Isolated",
                new PartitionAccessPolicy { Read = false, BreaksInheritance = true }),
            new MeshNode("Page", "PublicPolicy/Isolated") { NodeType = "Markdown" },
            new MeshNode("Reopened", "PublicPolicy/Private") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("PublicPolicy/Private/Reopened",
                new PartitionAccessPolicy { PublicRead = true }),
            new MeshNode("Page", "PublicPolicy/Private/Reopened") { NodeType = "Markdown" },
            new MeshNode("SameScope") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("SameScope", new PartitionAccessPolicy { PublicRead = true, Read = false }),
            new MeshNode("Page", "SameScope") { NodeType = "Markdown" },
            new MeshNode("CapOnly") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("CapOnly", new PartitionAccessPolicy { Read = true }),
            new MeshNode("Page", "CapOnly") { NodeType = "Markdown" },
            new MeshNode("RolePolicy") { NodeType = "Markdown" },
            AssignmentNodeFactory.UserRole(Reader, "Viewer", "RolePolicy"),
            new MeshNode("Page", "RolePolicy") { NodeType = "Markdown" },
            new MeshNode("Capped", "RolePolicy") { NodeType = "Markdown" },
            AssignmentNodeFactory.Policy("RolePolicy/Capped", new PartitionAccessPolicy { Read = false }),
            new MeshNode("Page", "RolePolicy/Capped") { NodeType = "Markdown" },
            new MeshNode("Denied", "RolePolicy") { NodeType = "Markdown" },
            AssignmentNodeFactory.UserRole(Reader, "Viewer", "RolePolicy/Denied", denied: true),
            new MeshNode("Page", "RolePolicy/Denied") { NodeType = "Markdown" });

    private Task<Permission> Effective(string path, string subject)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var context = new AccessContext { ObjectId = subject, Name = subject };
        access.SetContext(context);
        access.SetHostIdentity(context);
        // Assert the first decision, not a later matching answer that could hide an initial leak.
        return Mesh.GetEffectivePermissions(path, subject)
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(TestContext.Current.CancellationToken);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("PublicPolicy/Page", WellKnownUsers.Anonymous, true)]
    [InlineData("PublicPolicy/Page", Reader, true)]
    [InlineData("PublicPolicy/Private/Page", WellKnownUsers.Anonymous, false)]
    [InlineData("PublicPolicy/Private/Page", Reader, false)]
    [InlineData("PublicPolicy/Isolated/Page", WellKnownUsers.Anonymous, false)]
    [InlineData("PublicPolicy/Isolated/Page", Reader, false)]
    [InlineData("PublicPolicy/Private/Reopened/Page", WellKnownUsers.Anonymous, true)]
    [InlineData("PublicPolicy/Private/Reopened/Page", Reader, true)]
    [InlineData("SameScope/Page", WellKnownUsers.Anonymous, true)]
    [InlineData("SameScope/Page", Reader, true)]
    [InlineData("CapOnly/Page", WellKnownUsers.Anonymous, false)]
    [InlineData("CapOnly/Page", Reader, false)]
    public async Task PublicRead_UsesTheMostSpecificPolicy(string path, string subject, bool readable)
    {
        (await Effective(path, subject)).HasFlag(Permission.Read).Should().Be(readable,
            "a deeper cap suppresses an inherited public grant, while a grant at the same scope wins");
    }

    [Fact(Timeout = 60_000)]
    public async Task RoleGrantAndCap_KeepTheirExistingPermissionBits()
    {
        var granted = await Effective("RolePolicy/Page", Reader);
        granted.HasFlag(Permission.Read | Permission.Execute | Permission.Api).Should().BeTrue();

        var capped = await Effective("RolePolicy/Capped/Page", Reader);
        capped.HasFlag(Permission.Read).Should().BeFalse();
        capped.HasFlag(Permission.Execute | Permission.Api).Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task RoleDeny_StillRemovesTheInheritedRole()
    {
        (await Effective("RolePolicy/Denied/Page", Reader)).Should().Be(Permission.None);
    }
}
