using System;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests that an admin user can create Space nodes.
/// </summary>
public class SpaceNodeCreationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddSpaceType();

    [Fact(Timeout = 60000)]
    public async Task Admin_CanCreateSpace()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];
        var spacePath = spaceId;

        var node = MeshNode.FromPath(spacePath) with
        {
            Name = "Test Space",
            NodeType = SpaceNodeType.NodeType
        };

        var created = await NodeFactory.CreateNode(node).ToTask(TestContext.Current.CancellationToken);

        created.Should().NotBeNull("Admin should be able to create Space nodes");
        created.State.Should().Be(MeshNodeState.Active);
        created.Path.Should().Be(spacePath);
        created.NodeType.Should().Be("Space");
        created.Name.Should().Be("Test Space");
        Output.WriteLine($"Space created at: {created.Path}");

        var fetched = await ReadNodeAsync(spacePath);
        fetched.Should().NotBeNull("Created space should be queryable");
        fetched!.NodeType.Should().Be("Space");

        await NodeFactory.DeleteNode(spacePath).ToTask(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 60000)]
    public async Task Admin_CanCreateSpaceWithContent()
    {
        var spaceId = $"ContentSpace_{Guid.NewGuid():N}"[..20];
        var spacePath = spaceId;

        var spaceContent = new Space
        {
            Name = "Acme Corp",
            Description = "A test space",
            Website = "https://acme.example.com",
            Location = "Switzerland",
            Email = "info@acme.example.com",
            IsVerified = true
        };

        var node = MeshNode.FromPath(spacePath) with
        {
            Name = "Acme Corp",
            NodeType = SpaceNodeType.NodeType,
            Content = spaceContent
        };

        var created = await NodeFactory.CreateNode(node).ToTask(TestContext.Current.CancellationToken);

        created.Should().NotBeNull();
        created.State.Should().Be(MeshNodeState.Active);
        created.NodeType.Should().Be("Space");
        Output.WriteLine($"Space with content created at: {created.Path}");

        await NodeFactory.DeleteNode(spacePath).ToTask(TestContext.Current.CancellationToken);
    }
}
