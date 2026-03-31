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
using Memex.Portal.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests for the Partition node type:
/// - Partition nodes are created under Admin/Partition namespace
/// - Organization creation triggers partition node creation
/// - Partitions are publicly readable by authenticated users
/// </summary>
public class PartitionAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddOrganizationType()
            .AddMeshNodes(
                // Pre-seed a partition node for testing
                new MeshNode("TestPartition", PartitionNodeType.Namespace)
                {
                    Name = "Test Partition",
                    NodeType = PartitionNodeType.NodeType,
                    State = MeshNodeState.Active,
                    Content = new PartitionDefinition
                    {
                        Namespace = "TestOrg",
                        DataSource = "default",
                        Description = "Test partition for unit tests"
                    }
                },
                // Pre-seed the Documentation partition (normally done by AddDocumentation)
                new MeshNode("Documentation", PartitionNodeType.Namespace)
                {
                    Name = "MeshWeaver Documentation",
                    NodeType = PartitionNodeType.NodeType,
                    State = MeshNodeState.Active,
                    Content = new PartitionDefinition
                    {
                        Namespace = "Doc",
                        DataSource = "static",
                        Description = "Built-in documentation"
                    }
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

    [Fact(Timeout = 20000)]
    public async Task Admin_CanSee_AllPartitions()
    {
        // Default login is admin (Roland)
        var results = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{PartitionNodeType.Namespace} nodeType:{PartitionNodeType.NodeType}",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Length} partitions");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} (Content={r.Content?.GetType().Name})");

        results.Should().HaveCountGreaterThanOrEqualTo(2, "Admin should see all partitions");
        results.Should().Contain(n => n.Path == "Admin/Partition/TestPartition");
        results.Should().Contain(n => n.Path == "Admin/Partition/Documentation");
    }

    [Fact(Timeout = 20000)]
    public async Task AuthenticatedUser_CanRead_PartitionNodes()
    {
        LoginAsUnprivilegedUser();

        var results = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{PartitionNodeType.Namespace} nodeType:{PartitionNodeType.NodeType}",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Length} partitions as unprivileged user");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        // Partition nodes are publicly readable
        results.Should().HaveCountGreaterThanOrEqualTo(2,
            "Partition nodes should be publicly readable by any authenticated user");
    }

    [Fact(Timeout = 20000)]
    public async Task PartitionNode_HasCorrectNamespace()
    {
        var results = await MeshQuery.QueryAsync<MeshNode>(
            "path:Admin/Partition/TestPartition",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        results.Should().HaveCount(1);
        var partition = results[0];
        partition.Content.Should().BeOfType<PartitionDefinition>();

        var def = (PartitionDefinition)partition.Content!;
        def.Namespace.Should().Be("TestOrg");
        def.DataSource.Should().Be("default");
        def.Description.Should().Contain("Test partition");
    }

    [Fact(Timeout = 20000)]
    public async Task DocumentationPartition_HasDocNamespace()
    {
        var results = await MeshQuery.QueryAsync<MeshNode>(
            "path:Admin/Partition/Documentation",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        results.Should().HaveCount(1);
        var partition = results[0];
        partition.Content.Should().BeOfType<PartitionDefinition>();

        var def = (PartitionDefinition)partition.Content!;
        def.Namespace.Should().Be("Doc");
        def.DataSource.Should().Be("static");
    }

    [Fact(Timeout = 30000)]
    public async Task OrganizationCreation_CreatesPartitionNode()
    {
        // Give creator Admin role so they can create organizations
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("Roland", "Admin", null, "system",
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
        Output.WriteLine($"Created org: {created.Path}");

        // Give time for post-creation handler to execute
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Check that a partition node was created
        var partitions = await MeshQuery.QueryAsync<MeshNode>(
            "path:Admin/Partition/Globex",
            ct: TestContext.Current.CancellationToken
        ).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {partitions.Length} partition nodes for Globex");
        foreach (var p in partitions)
            Output.WriteLine($"  - {p.Path}: {p.Name} (Content={p.Content?.GetType().Name})");

        partitions.Should().HaveCount(1, "Organization creation should auto-create a Partition node");
        var partitionDef = partitions[0].Content as PartitionDefinition;
        partitionDef.Should().NotBeNull();
        partitionDef!.Namespace.Should().Be("Globex");
        partitionDef.Schema.Should().Be("globex");
        partitionDef.TableMappings.Should().NotBeNull();
        partitionDef.TableMappings.Should().ContainKey("_Activity");
    }
}
