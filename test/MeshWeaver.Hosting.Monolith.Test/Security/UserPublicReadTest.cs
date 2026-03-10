using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test.Security;

/// <summary>
/// Tests that User and Organization nodes are publicly readable
/// (any authenticated user can see them) without requiring an explicit AccessAssignment.
/// Both node types have WithPublicRead() and INodeTypeAccessRule that grants Read to everyone.
/// </summary>
public class UserPublicReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddOrganizationType()
            .AddRowLevelSecurity()
            .AddMeshNodes(
                new MeshNode("Roland", "User")
                {
                    Name = "Roland",
                    NodeType = "User",
                    State = MeshNodeState.Active,
                    Content = new User { Email = "rbuergi@systemorph.com" }
                },
                // Organization at root namespace (DefaultNamespace = "")
                new MeshNode("Acme")
                {
                    Name = "Acme Corp",
                    NodeType = "Organization",
                    State = MeshNodeState.Active,
                    Content = new Organization { Name = "Acme Corp" }
                },
                // Second user for nodeType query tests
                new MeshNode("Bob", "User")
                {
                    Name = "Bob",
                    NodeType = "User",
                    State = MeshNodeState.Active,
                    Content = new User { Email = "bob@example.com" }
                }
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

    [Fact(Timeout = 10000)]
    public async Task AuthenticatedUser_CanRead_UserNode_ByPath()
    {
        LoginAsUnprivilegedUser();

        var results = await MeshQuery.QueryAsync<MeshNode>(
            "path:User/Roland scope:exact",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Length} results");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} (NodeType={r.NodeType})");

        results.Should().HaveCount(1, "User nodes should be publicly readable by any authenticated user");
        results[0].Path.Should().Be("User/Roland");
        results[0].Name.Should().Be("Roland");
    }

    [Fact(Timeout = 10000)]
    public async Task AuthenticatedUser_CanRead_OrganizationNode_ByPath()
    {
        LoginAsUnprivilegedUser();

        var results = await MeshQuery.QueryAsync<MeshNode>(
            "path:Acme scope:exact",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Length} results");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} (NodeType={r.NodeType})");

        results.Should().HaveCount(1, "Organization nodes should be publicly readable by any authenticated user");
        results[0].Path.Should().Be("Acme");
        results[0].Name.Should().Be("Acme Corp");
    }

    [Fact(Timeout = 10000)]
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

    [Fact(Timeout = 10000)]
    public async Task AuthenticatedUser_CanQuery_OrganizationNodes_ByNodeType()
    {
        LoginAsUnprivilegedUser();

        var results = await MeshQuery.QueryAsync<MeshNode>(
            "nodeType:Organization",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Length} Organization nodes");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} (NodeType={r.NodeType})");

        results.Should().HaveCountGreaterThanOrEqualTo(1, "All Organization nodes should be publicly readable");
        results.Should().Contain(n => n.Path == "Acme");
    }

    [Fact(Timeout = 30000)]
    public async Task DynamicallyCreated_OrganizationNode_IsPubliclyReadable()
    {
        // Create an Organization dynamically (simulates runtime creation)
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        // Give the creator Admin role at root so they can create
        await securityService.AddUserRoleAsync("Creator", "Admin", null, "system",
            TestContext.Current.CancellationToken);

        var orgNode = new MeshNode("Globex")
        {
            Name = "Globex Corp",
            NodeType = "Organization",
            Content = new Organization { Name = "Globex Corp" }
        };
        var created = await NodeFactory.CreateNodeAsync(orgNode,
            ct: TestContext.Current.CancellationToken);
        created.Should().NotBeNull();
        Output.WriteLine($"Created: {created.Path}");

        // Now query as a different unprivileged user
        LoginAsUnprivilegedUser();

        var results = await MeshQuery.QueryAsync<MeshNode>(
            "path:Globex scope:exact",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        results.Should().HaveCount(1, "Dynamically created Organization should be publicly readable");
        results[0].Name.Should().Be("Globex Corp");
    }
}
