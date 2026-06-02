using System;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests that creating a Space via normal CreateNodeRequest grants admin access
/// to the creator. The Space top-level validator eagerly provisions the partition
/// (a no-op on the in-memory backend used here) and the post-creation handler emits
/// the Admin/Partition/{id} PartitionDefinition + the creator-admin AccessAssignment.
/// </summary>
public class CreateSpaceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddSpaceType();

    [Fact(Timeout = 60000)]
    public void CreateSpace_CreatesAdminAccess()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = "Test Space" }
        };
        var created = NodeFactory.CreateNode(spaceNode).Should().Emit();

        created.Should().NotBeNull();
        created.State.Should().Be(MeshNodeState.Active);
        created.NodeType.Should().Be("Space");
        created.Name.Should().Be("Test Space");

        // Creator has Admin permissions on the Space namespace
        Mesh.GetEffectivePermissions(spaceId, TestUsers.Admin.ObjectId)
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Update),
                "Creator should have Admin permissions on the space");

        NodeFactory.DeleteNode(spaceId).Should().Emit();
    }
}
