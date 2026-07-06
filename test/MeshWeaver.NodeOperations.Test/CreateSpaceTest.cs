using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
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
    public async Task CreateSpace_CreatesAdminAccess()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        };
        var created = await NodeFactory.CreateNode(spaceNode).Should().Emit();

        created.Should().NotBeNull();
        created.State.Should().Be(MeshNodeState.Active);
        created.NodeType.Should().Be("Space");
        created.Name.Should().Be("Test Space");

        // Creator has Admin permissions on the Space namespace
        await Mesh.GetEffectivePermissions(spaceId, TestUsers.Admin.ObjectId)
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Update),
                "Creator should have Admin permissions on the space");

        // ...and the creator-owner grant is a concrete AccessAssignment node at
        // {space}/_Access/{creator}_Access (owner = creator). This is the node the owner-grant
        // MUST produce; the create now FAULTS rather than returning a silent Ok if it is missing
        // (SpacePostCreationHandler.FailsCreateOnError), so a created Space is never ownerless.
        var grantPath = $"{spaceId}/_Access/{TestUsers.Admin.ObjectId}_Access";
        var grant = await Mesh.GetWorkspace().GetMeshNodeStream(grantPath)
            .Where(n => n is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        grant!.NodeType.Should().Be("AccessAssignment", "the creator-owner grant is an AccessAssignment");

        await NodeFactory.DeleteNode(spaceId).Should().Emit();
    }
}
