using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Graph;
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
/// Tests that a global admin (claim-based Admin role) has full CRUD
/// permissions on Space nodes. Uses base ConfigureMesh which
/// includes PublicAdminAccess — matching production behavior.
/// </summary>
public class GlobalAdminSpaceCrudTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddSpaceType()
            .AddSampleUsers();

    [Fact(Timeout = 60000)]
    public async Task GlobalAdmin_CanCreateSpace()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        };

        var created = await NodeFactory.CreateNode(spaceNode).Should().Emit();

        created.Should().NotBeNull("Global admin should be able to create Spaces");
        created.State.Should().Be(MeshNodeState.Active);
        created.NodeType.Should().Be("Space");

        await NodeFactory.DeleteNode(spaceId).Should().Emit();
    }

    [Fact(Timeout = 60000)]
    public async Task GlobalAdmin_CanReadSpace()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        };
        await NodeFactory.CreateNode(spaceNode).Should().Emit();

        var found = await ReadNode(spaceId).Should().Emit();

        found.Should().NotBeNull("Global admin should be able to read Spaces");
        found!.Name.Should().Be("Test Space");

        await NodeFactory.DeleteNode(spaceId).Should().Emit();
    }

    [Fact(Timeout = 60000)]
    public async Task GlobalAdmin_CanUpdateSpace()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Original Name",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        };
        await NodeFactory.CreateNode(spaceNode).Should().Emit();

        var updated = spaceNode with
        {
            Name = "Updated Name",
            Content = new Space { Description = "Updated description" }
        };
        await NodeFactory.UpdateNode(updated).Should().Emit();

        var found = await ReadNode(spaceId).Should().Emit();

        found.Should().NotBeNull();
        found!.Name.Should().Be("Updated Name");

        await NodeFactory.DeleteNode(spaceId).Should().Emit();
    }

    [Fact(Timeout = 60000)]
    public async Task GlobalAdmin_CanDeleteSpace()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "To Delete",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        };
        await NodeFactory.CreateNode(spaceNode).Should().Emit();

        await NodeFactory.DeleteNode(spaceId).Should().Emit();

        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode(spaceId))
            .Should().Within(10.Seconds()).Match(n => n is null, "Deleted space should not be found");
    }

    [Fact(Timeout = 60000)]
    public async Task DeletingSpace_RemovesPartitionDefinition()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Partition Cleanup",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        };
        await NodeFactory.CreateNode(spaceNode).Should().Emit();

        // The create emitted the Admin/Partition/{id} definition (routing prime).
        var defPath = $"{PartitionNodeType.Namespace}/{spaceId}";
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode(defPath))
            .Should().Within(15.Seconds()).Match(n => n is not null,
                "creating a Space emits its Admin/Partition definition");

        await NodeFactory.DeleteNode(spaceId).Should().Emit();

        // The post-deletion handler removes the ENTIRE partition: the space node is gone
        // AND the Admin/Partition/{id} definition is cleaned up.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode(spaceId))
            .Should().Within(10.Seconds()).Match(n => n is null, "Deleted space should not be found");
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode(defPath))
            .Should().Within(10.Seconds()).Match(n => n is null,
                "deleting a Space must remove its Admin/Partition definition");
    }

    [Fact(Timeout = 60000)]
    public async Task GlobalAdmin_HasAllPermissionsOnSpace()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Permission Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        };
        await NodeFactory.CreateNode(spaceNode).Should().Emit();

        var permissions = await Mesh.GetEffectivePermissions(spaceId, TestUsers.Admin.ObjectId).Should().Emit();

        Output.WriteLine($"Admin permissions on {spaceId}: {permissions}");

        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
        permissions.Should().HaveFlag(Permission.Delete);

        var rootPermissions = await Mesh.GetEffectivePermissions("", TestUsers.Admin.ObjectId).Should().Emit();

        Output.WriteLine($"Admin permissions on root: {rootPermissions}");
        rootPermissions.Should().HaveFlag(Permission.Create,
            "Global admin should have Create permission at root level to create Spaces");

        await NodeFactory.DeleteNode(spaceId).Should().Emit();
    }

    [Fact(Timeout = 60000)]
    public async Task GlobalAdmin_CanCreateNodeUnderSpace()
    {
        var spaceId = $"TestSpace_{Guid.NewGuid():N}"[..20];

        var spaceNode = MeshNode.FromPath(spaceId) with
        {
            Name = "Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        };
        await NodeFactory.CreateNode(spaceNode).Should().Emit();

        var childNode = new MeshNode("TestPage", spaceId)
        {
            Name = "Test Page",
            NodeType = "Markdown"
        };
        var created = await NodeFactory.CreateNode(childNode).Should().Emit();

        created.Should().NotBeNull("Admin should be able to create nodes under Space");
        created.Path.Should().Be($"{spaceId}/TestPage");

        await NodeFactory.DeleteNode(spaceId).Should().Emit();
    }

    [Fact(Timeout = 60000)]
    public async Task GlobalAdmin_CanAccessCreateLayoutAreaOnSpace()
    {
        var hasCreate = await Mesh.GetEffectivePermissions("Space", TestUsers.Admin.ObjectId)
            .Select(p => p.HasFlag(Permission.Create))
            .Should().Emit();
        Output.WriteLine($"Admin has Create on 'Space' via SecurityService: {hasCreate}");
        hasCreate.Should().BeTrue("Admin should have Create on Space path");

        var accessRules = Mesh.ServiceProvider.GetServices<INodeTypeAccessRule>();
        var spaceRule = accessRules.FirstOrDefault(r =>
            r.NodeType.Equals("Space", StringComparison.OrdinalIgnoreCase));

        spaceRule.Should().NotBeNull("SpaceAccessRule should be registered via DI");
        spaceRule!.SupportedOperations.Should().Contain(NodeOperation.Create,
            "SpaceAccessRule should support Create operation");

        var ctx = new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = MeshNode.FromPath("TestSpace") with { NodeType = "Space" }
        };
        var ruleAllows = await spaceRule.HasAccess(ctx, TestUsers.Admin.ObjectId).Should().Emit();
        Output.WriteLine($"SpaceAccessRule.HasAccessAsync for Create: {ruleAllows}");
        ruleAllows.Should().BeTrue(
            "SpaceAccessRule should allow Create for admin user");
    }

    [Fact(Timeout = 60000)]
    public async Task SpaceType_IsVisibleToGlobalAdmin()
    {
        var provider = Mesh.ServiceProvider.GetRequiredService<ICreatableTypesProvider>();
        var creatableTypes = await provider.GetCreatableTypes("", parentNode: null)
            .Should().Emit();

        var typeNames = creatableTypes.Select(t => t.NodeTypePath).ToList();
        Output.WriteLine($"Root creatable types: {string.Join(", ", typeNames)}");

        typeNames.Should().Contain("Space",
            "Space should be a creatable type at root level for global admin");
    }

    [Fact(Timeout = 60000)]
    public async Task GlobalAdmin_CanReadSpaceNodeType()
    {
        var hasRead = await Mesh.GetEffectivePermissions("Space", TestUsers.Admin.ObjectId)
            .Select(p => p.HasFlag(Permission.Read))
            .Should().Emit();

        Output.WriteLine($"Admin has Read on 'Space': {hasRead}");
        hasRead.Should().BeTrue(
            "Global admin should have Read permission on the Space NodeType hub path");
    }
}
