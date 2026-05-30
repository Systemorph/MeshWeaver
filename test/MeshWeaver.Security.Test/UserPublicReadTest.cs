using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Blazor.Portal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests that User and Organization nodes are publicly readable
/// (any authenticated user can see them) without requiring an explicit AccessAssignment.
/// Both node types have WithPublicRead() and INodeTypeAccessRule that grants Read to everyone.
/// </summary>
public class UserPublicReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddSpaceType()
            .AddMeshNodes(
                new MeshNode("Roland", "User")
                {
                    Name = "Roland",
                    NodeType = "User",
                    State = MeshNodeState.Active,
                    Content = new User { Email = "rbuergi@systemorph.com" }
                },
                // Space at root namespace (DefaultNamespace = "")
                new MeshNode("Acme")
                {
                    Name = "Acme Corp",
                    NodeType = "Space",
                    State = MeshNodeState.Active,
                    Content = new Space { Name = "Acme Corp" }
                },
                // Second user for nodeType query tests
                new MeshNode("Bob", "User")
                {
                    Name = "Bob",
                    NodeType = "User",
                    State = MeshNodeState.Active,
                    Content = new User { Email = "bob@example.com" }
                },
                // "Creator" gets global Admin so DynamicallyCreated_OrganizationNode_RequiresPartitionAccess
                // can create an Organization at runtime.
                AssignmentNodeFactory.UserRole("Creator", "Admin")
            );

    private void LoginAsUnprivilegedUser()
    {
        TestUsers.DevLogin(Mesh, new AccessContext
        {
            ObjectId = "Alice",
            Name = "Alice",
            Email = "alice@example.com"
        });
    }

    [Fact(Timeout = 20000)]
    public async Task AuthenticatedUser_CanRead_UserNode_ByPath()
    {
        LoginAsUnprivilegedUser();

        var results = await MeshQuery.QueryAsync<MeshNode>(
            "path:User/Roland",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Length} results");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} (NodeType={r.NodeType})");

        results.Should().HaveCount(1, "User nodes should be publicly readable by any authenticated user");
        results[0].Path.Should().Be("User/Roland");
        results[0].Name.Should().Be("Roland");
    }

    [Fact(Timeout = 20000)]
    public async Task AuthenticatedUser_CanRead_OrganizationNode_ByPath()
    {
        LoginAsUnprivilegedUser();

        var results = await MeshQuery.QueryAsync<MeshNode>(
            "path:Acme",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Length} results");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} (NodeType={r.NodeType})");

        results.Should().HaveCount(1, "Organization nodes should be publicly readable by any authenticated user");
        results[0].Path.Should().Be("Acme");
        results[0].Name.Should().Be("Acme Corp");
    }

    [Fact(Timeout = 20000)]
    public async Task AuthenticatedUser_CanQuery_UserNodes_ByNodeType()
    {
        LoginAsUnprivilegedUser();

        var results = await MeshQuery.QueryAsync<MeshNode>(
            "nodeType:User",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Length} User nodes");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} (NodeType={r.NodeType})");

        results.Should().HaveCountGreaterThanOrEqualTo(2, "All User nodes should be publicly readable");
        results.Should().Contain(n => n.Path == "User/Roland");
        results.Should().Contain(n => n.Path == "User/Bob");
    }

    [Fact(Timeout = 20000)]
    public async Task AuthenticatedUser_CanQuery_SpaceNodes_ByNodeType()
    {
        LoginAsUnprivilegedUser();

        var results = await MeshQuery.QueryAsync<MeshNode>(
            "nodeType:Space",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Length} Space nodes");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} (NodeType={r.NodeType})");

        results.Should().HaveCountGreaterThanOrEqualTo(1, "All Space nodes should be publicly readable");
        results.Should().Contain(n => n.Path == "Acme");
    }

    [Fact(Timeout = 30000)]
    public async Task DynamicallyCreated_SpaceNode_RequiresPartitionAccess()
    {
        // "Creator" Admin at root is pre-seeded via ConfigureMesh's static AccessAssignment.
        var orgNode = new MeshNode("Globex")
        {
            Name = "Globex Corp",
            NodeType = "Space",
            Content = new Space { Name = "Globex Corp" }
        };
        var created = await NodeFactory.CreateNode(orgNode);
        created.Should().NotBeNull();
        Output.WriteLine($"Created: {created.Path}");

        // Unprivileged user cannot see the org (partition access controls visibility)
        LoginAsUnprivilegedUser();

        var results = await MeshQuery.QueryAsync<MeshNode>(
            "path:Globex",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        results.Should().BeEmpty("Organization instances require partition-level access, not public read");

        // Grant Alice (the unprivileged user) Viewer role on Globex via runtime CreateNode.
        // This tests the live behavior change after a permission grant — but the
        // CreateNode itself goes through the standard RLS validator, which would
        // deny Alice (the current context) the right to write into Globex/_Access.
        // Switch back to the DevLogin admin context for the seeding write, then
        // restore Alice for the post-grant query.
        TestUsers.DevLogin(Mesh);
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole("Alice", "Viewer", "Globex"))
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        LoginAsUnprivilegedUser();

        // Wait for the runtime AccessAssignment to surface in SecurityService's
        // synced query (the QueryAsync path checks live permissions). Without
        // this gate, the immediate query reads the cached snapshot from before
        // the grant landed → empty result.
        await Mesh.GetPermissionAsync("Globex", "Alice",
            until: p => p.HasFlag(Permission.Read),
            ct: TestContext.Current.CancellationToken);

        var resultsAfterGrant = await MeshQuery.QueryAsync<MeshNode>(
            "path:Globex",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        resultsAfterGrant.Should().HaveCount(1, "Organization should be readable after granting Viewer role");
        resultsAfterGrant[0].Name.Should().Be("Globex Corp");
    }
}
