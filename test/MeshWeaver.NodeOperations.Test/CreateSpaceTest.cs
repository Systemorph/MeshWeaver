using System;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests that creating a Space via normal CreateNodeRequest grants admin access
/// to the creator. The per-tenant Partition MeshNode is no longer emitted —
/// the routing layer creates the partition schema lazily on first write.
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
            Content = new Space { Name = "Test Space" }
        };
        var created = await NodeFactory.CreateNode(spaceNode).ToTask(TestContext.Current.CancellationToken);

        created.Should().NotBeNull();
        created.State.Should().Be(MeshNodeState.Active);
        created.NodeType.Should().Be("Space");
        created.Name.Should().Be("Test Space");

        // Creator has Admin permissions on the Space namespace
        var hasAdmin = await Mesh.HasPermissionAsync(
            spaceId, TestUsers.Admin.ObjectId, Permission.Update, TestTimeout);
        hasAdmin.Should().BeTrue("Creator should have Admin permissions on the space");

        await NodeFactory.DeleteNode(spaceId).ToTask(TestContext.Current.CancellationToken);
    }
}
