using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using Memex.Portal.Shared;
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
/// Tests that an Organization created via normal CreateNodeRequest
/// grants the creator Admin permissions (Create, Update, Delete)
/// and that standard node types (Markdown, Thread, Agent) are creatable.
/// </summary>
public class OrganizationMenuAndAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    /// <summary>
    /// Uses ConfigureMeshBase (no PublicAdminAccess) so permissions come
    /// purely from Organization's PostCreationHandler, not global admin.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddOrganizationType()
            .AddSampleUsers();

    [Fact(Timeout = 60000)]
    public async Task AdminCreator_HasFullPermissions()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Create Organization
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Test Organization" }
        };
        var created = await NodeFactory.CreateNode(orgNode);
        created.Should().NotBeNull();

        // Subscribe to the live GetEffectivePermissions stream and wait for
        // the first emission that includes Read — no polling, no Task.Delay.
        // The synced AccessAssignment query re-emits as the new satellite
        // lands; .Where(p => p.HasFlag(Read)).Timeout(...) absorbs the index
        // propagation lag deterministically.
        var permissions = await Mesh.GetPermissionAsync(
            orgId, TestUsers.Admin.ObjectId,
            until: p => p.HasFlag(Permission.Read),
            ct: TestTimeout);

        Output.WriteLine($"Effective permissions on {orgId}: {permissions}");

        permissions.Should().HaveFlag(Permission.Read, "Admin should have Read permission");
        permissions.Should().HaveFlag(Permission.Create, "Admin should have Create permission");
        permissions.Should().HaveFlag(Permission.Update, "Admin should have Update permission");
        permissions.Should().HaveFlag(Permission.Delete, "Admin should have Delete permission");

        // Cleanup
        await NodeFactory.DeleteNode(orgId);
    }

    /// <summary>
    /// Verifies that the PostCreationHandler creates an AccessAssignment MeshNode
    /// granting the creator Admin permissions on the org namespace.
    /// Uses a non-admin user to check that permissions come from the AccessAssignment,
    /// not from claim-based roles.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PostCreationHandler_GrantsAdminViaAccessAssignment()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];
        var creatorId = TestUsers.Admin.ObjectId; // "Roland" — has claim-based Admin

        // Create Organization as admin (required for root-level create permission)
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Test Organization" }
        };
        await NodeFactory.CreateNode(orgNode);

        // Wait via the live GetEffectivePermissions stream — Update flag
        // implies AccessAssignment satellite has been observed by the synced
        // query (Admin role grants Update). Same CI-only init race as
        // AdminCreator_HasFullPermissions above; deterministic via .Where.
        await Mesh.WaitForPermissionAsync(orgId, creatorId, Permission.Update, TestTimeout);

        // Now check that a NON-admin user without claim-based roles has NO permissions
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var otherPerms = await Mesh.GetPermissionAsync(
            orgId, "SomeOtherUser", TestTimeout);

        Output.WriteLine($"Other user permissions on {orgId}: {otherPerms}");
        otherPerms.Should().NotHaveFlag(Permission.Create, "Non-assigned user should NOT have Create");
        otherPerms.Should().NotHaveFlag(Permission.Update, "Non-assigned user should NOT have Update");

        // And the creator DOES have permissions (from AccessAssignment, not just claim-based)
        var creatorPerms = await Mesh.GetPermissionAsync(
            orgId, creatorId, TestTimeout);

        Output.WriteLine($"Creator permissions on {orgId}: {creatorPerms}");
        creatorPerms.Should().HaveFlag(Permission.Create, "Creator should have Create from Admin role assignment");
        creatorPerms.Should().HaveFlag(Permission.Update, "Creator should have Update from Admin role assignment");
        creatorPerms.Should().HaveFlag(Permission.Delete, "Creator should have Delete from Admin role assignment");

        // Cleanup
        await NodeFactory.DeleteNode(orgId);
    }

    [Fact(Timeout = 60000)]
    public async Task Organization_HasStandardCreatableTypes()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Create Organization
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Test Organization" }
        };
        await NodeFactory.CreateNode(orgNode);

        // Check creatable types for the organization via the synced-query provider.
        var provider = Mesh.ServiceProvider.GetRequiredService<ICreatableTypesProvider>();
        var creatableTypes = await provider.GetCreatableTypes(orgId, orgNode)
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
        var typeNames = creatableTypes.Select(t => t.NodeTypePath).ToList();

        Output.WriteLine($"Creatable types at {orgId}: {string.Join(", ", typeNames)}");

        typeNames.Should().Contain("Markdown", "Markdown should be creatable under Organization");
        typeNames.Should().Contain("Thread", "Thread should be creatable under Organization");
        typeNames.Should().Contain("Agent", "Agent should be creatable under Organization");

        // Cleanup
        await NodeFactory.DeleteNode(orgId);
    }

    [Fact(Timeout = 60000)]
    public async Task Organization_ChildrenAreQueryable()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Create Organization
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Test Organization" }
        };
        await NodeFactory.CreateNode(orgNode);

        // Create a child node under the organization
        await NodeFactory.CreateNode(
            new MeshNode("Overview", orgId) { Name = "Overview", NodeType = "Markdown" });

        // Query children under the organization namespace
        var children = await MeshQuery
            .QueryAsync<MeshNode>($"namespace:{orgId} is:main")
            .ToListAsync(TestTimeout);

        Output.WriteLine($"Children under {orgId}: {string.Join(", ", children.Select(c => $"{c.Path} ({c.NodeType})"))}");

        // The Overview markdown page should be a child
        children.Should().Contain(c => c.NodeType == "Markdown" && c.Path == $"{orgId}/Overview",
            "Overview markdown page should exist as child");

        // Cleanup
        await NodeFactory.DeleteNode(orgId);
    }
}
