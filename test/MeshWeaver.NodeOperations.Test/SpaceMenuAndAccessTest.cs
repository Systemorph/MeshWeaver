using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests that a Space created via normal CreateNodeRequest
/// grants the creator Admin permissions (Create, Update, Delete)
/// and that standard node types (Markdown, Thread, Agent) are creatable.
/// </summary>
public class SpaceMenuAndAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    /// <summary>
    /// Uses ConfigureMeshBase (no PublicAdminAccess) so permissions come
    /// purely from Space's PostCreationHandler, not global admin.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddSpaceType()
            .AddSampleUsers();

    [Fact(Timeout = 60000)]
    public void AdminCreator_HasFullPermissions()
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

        var permissions = Mesh.GetEffectivePermissions(spaceId, TestUsers.Admin.ObjectId)
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Read));

        Output.WriteLine($"Effective permissions on {spaceId}: {permissions}");

        permissions.Should().HaveFlag(Permission.Read, "Admin should have Read permission");
        permissions.Should().HaveFlag(Permission.Create, "Admin should have Create permission");
        permissions.Should().HaveFlag(Permission.Update, "Admin should have Update permission");
        permissions.Should().HaveFlag(Permission.Delete, "Admin should have Delete permission");

        NodeFactory.DeleteNode(spaceId).Should().Emit();
    }

    /// <summary>
    /// Verifies that the PostCreationHandler creates an AccessAssignment MeshNode
    /// granting the creator Admin permissions on the Space namespace.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void PostCreationHandler_GrantsAdminViaAccessAssignment()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];
        var creatorId = TestUsers.Admin.ObjectId;

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = "Test Space" }
        };
        NodeFactory.CreateNode(spaceNode).Should().Emit();

        Mesh.GetEffectivePermissions(spaceId, creatorId)
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Update));

        var otherPerms = Mesh.GetEffectivePermissions(spaceId, "SomeOtherUser").Should().Emit();

        Output.WriteLine($"Other user permissions on {spaceId}: {otherPerms}");
        otherPerms.Should().NotHaveFlag(Permission.Create, "Non-assigned user should NOT have Create");
        otherPerms.Should().NotHaveFlag(Permission.Update, "Non-assigned user should NOT have Update");

        var creatorPerms = Mesh.GetEffectivePermissions(spaceId, creatorId).Should().Emit();

        Output.WriteLine($"Creator permissions on {spaceId}: {creatorPerms}");
        creatorPerms.Should().HaveFlag(Permission.Create, "Creator should have Create from Admin role assignment");
        creatorPerms.Should().HaveFlag(Permission.Update, "Creator should have Update from Admin role assignment");
        creatorPerms.Should().HaveFlag(Permission.Delete, "Creator should have Delete from Admin role assignment");

        NodeFactory.DeleteNode(spaceId).Should().Emit();
    }

    [Fact(Timeout = 60000)]
    public void Space_HasStandardCreatableTypes()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = "Test Space" }
        };
        NodeFactory.CreateNode(spaceNode).Should().Emit();

        var provider = Mesh.ServiceProvider.GetRequiredService<ICreatableTypesProvider>();
        var creatableTypes = provider.GetCreatableTypes(spaceId, spaceNode).Should().Emit();
        var typeNames = creatableTypes.Select(t => t.NodeTypePath).ToList();

        Output.WriteLine($"Creatable types at {spaceId}: {string.Join(", ", typeNames)}");

        typeNames.Should().Contain("Markdown", "Markdown should be creatable under Space");
        typeNames.Should().Contain("Thread", "Thread should be creatable under Space");
        typeNames.Should().Contain("Agent", "Agent should be creatable under Space");

        NodeFactory.DeleteNode(spaceId).Should().Emit();
    }

    [Fact(Timeout = 60000)]
    public void Space_ChildrenAreQueryable()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = "Test Space" }
        };
        NodeFactory.CreateNode(spaceNode).Should().Emit();

        NodeFactory.CreateNode(
            new MeshNode("Overview", spaceId) { Name = "Overview", NodeType = "Markdown" }).Should().Emit();

        var children = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{spaceId} is:main"))
            .Select(c => c.Items.ToList())
            .Should().Within(15.Seconds()).Match(items => items.Any(c => c.NodeType == "Markdown"
                && c.Path == $"{spaceId}/Overview"));

        Output.WriteLine($"Children under {spaceId}: {string.Join(", ", children.Select(c => $"{c.Path} ({c.NodeType})"))}");

        children.Should().Contain(c => c.NodeType == "Markdown" && c.Path == $"{spaceId}/Overview",
            "Overview markdown page should exist as child");

        NodeFactory.DeleteNode(spaceId).Should().Emit();
    }
}
