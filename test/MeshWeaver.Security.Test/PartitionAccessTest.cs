using System.Reactive.Linq;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
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
/// Tests for the Partition node type:
/// - Partition nodes are created under Admin/Partition namespace
/// - Organization creation triggers partition node creation
/// - Partitions are publicly readable by authenticated users
/// </summary>
public class PartitionAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddSpaceType()
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
                },
                // Static role assignment: Roland is global Admin (used by
                // OrganizationCreation_CreatesPartitionNode). AccessAssignment is
                // just a MeshNode — SecurityService reads it at hub init.
                AssignmentNodeFactory.UserRole("Roland", "Admin")
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
    public void Admin_CanSee_AllPartitions()
    {
        // Default login is admin (Roland)
        var results = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
            $"namespace:{PartitionNodeType.Namespace} nodeType:{PartitionNodeType.NodeType}"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        Output.WriteLine($"Found {results.Count} partitions");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} (Content={r.Content?.GetType().Name})");

        results.Should().HaveCountGreaterThanOrEqualTo(2, "Admin should see all partitions");
        results.Should().Contain(n => n.Path == "Admin/Partition/TestPartition");
        results.Should().Contain(n => n.Path == "Admin/Partition/Documentation");
    }

    [Fact(Timeout = 20000)]
    public void AuthenticatedUser_CanRead_PartitionNodes()
    {
        LoginAsUnprivilegedUser();

        var results = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
            $"namespace:{PartitionNodeType.Namespace} nodeType:{PartitionNodeType.NodeType}"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        Output.WriteLine($"Found {results.Count} partitions as unprivileged user");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        // Partition nodes are publicly readable
        results.Should().HaveCountGreaterThanOrEqualTo(2,
            "Partition nodes should be publicly readable by any authenticated user");
    }

    [Fact(Timeout = 20000)]
    public void PartitionNode_HasCorrectNamespace()
    {
        var results = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
            "path:Admin/Partition/TestPartition"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        results.Should().HaveCount(1);
        var partition = results.First();
        partition.Content.Should().BeOfType<PartitionDefinition>();

        var def = (PartitionDefinition)partition.Content!;
        def.Namespace.Should().Be("TestOrg");
        def.DataSource.Should().Be("default");
        def.Description.Should().Contain("Test partition");
    }

    [Fact(Timeout = 20000)]
    public void DocumentationPartition_HasDocNamespace()
    {
        var results = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
            "path:Admin/Partition/Documentation"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        results.Should().HaveCount(1);
        var partition = results.First();
        partition.Content.Should().BeOfType<PartitionDefinition>();

        var def = (PartitionDefinition)partition.Content!;
        def.Namespace.Should().Be("Doc");
        def.DataSource.Should().Be("static");
    }

}
