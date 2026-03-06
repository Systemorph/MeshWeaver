using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Blazor.AI;
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
/// Tests that MCP operations respect access control when invoked with different user contexts.
/// Simulates the API token flow: each token maps to a user identity, and MCP operations
/// should only return data the authenticated user is permitted to see.
/// </summary>
public class McpAccessControlTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    private const string User1 = "user1@example.com";
    private const string User2 = "user2@example.com";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder).AddGraph().AddRowLevelSecurity();
    }

    /// <summary>
    /// Sets the access context on the AccessService to simulate an authenticated API token user.
    /// Uses SetCircuitContext (not SetContext) because message handlers run on the hub's
    /// processing loop which has a different AsyncLocal context. CircuitContext is a
    /// persistent fallback that works across async boundaries.
    /// </summary>
    private void SetCurrentUser(string userId)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = userId,
            Name = userId,
            Email = userId,
        });
    }

    private McpMeshPlugin CreatePlugin()
        => new(Mesh);

    /// <summary>
    /// Sets up the test data:
    /// - "SharedOrg" namespace: User1=Viewer, User2=Editor
    /// - "SharedOrg/Public" node: visible to both
    /// - "SharedOrg/Confidential" node: User1 denied via PartitionAccessPolicy, User2 can access
    /// - "PrivateOrg" namespace: User2=Admin, User1 has no access
    /// - "PrivateOrg/Secret" node: invisible to User1, visible to User2
    /// </summary>
    private async Task SetupTestData()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Set up admin context for data seeding — NodeFactory.CreateNodeAsync sends a
        // CreateNodeRequest that goes through RlsNodeValidator, which requires permissions.
        await securityService.AddUserRoleAsync("setup-admin", "Admin", null, "system", TestTimeout);
        accessService.SetCircuitContext(new AccessContext { ObjectId = "setup-admin", Name = "Setup Admin" });

        // Create namespace nodes using the public NodeFactory API
        await NodeFactory.CreateNodeAsync(new MeshNode("SharedOrg")
        {
            Name = "Shared Organization",
            NodeType = "Group",
        }, ct: TestTimeout);

        await NodeFactory.CreateNodeAsync(new MeshNode("Public", "SharedOrg")
        {
            Name = "Public Project",
            NodeType = "Markdown",
        }, ct: TestTimeout);

        await NodeFactory.CreateNodeAsync(new MeshNode("Confidential", "SharedOrg")
        {
            Name = "Confidential Project",
            NodeType = "Markdown",
        }, ct: TestTimeout);

        await NodeFactory.CreateNodeAsync(new MeshNode("PrivateOrg")
        {
            Name = "Private Organization",
            NodeType = "Group",
        }, ct: TestTimeout);

        await NodeFactory.CreateNodeAsync(new MeshNode("Secret", "PrivateOrg")
        {
            Name = "Secret Data",
            NodeType = "Markdown",
        }, ct: TestTimeout);

        // Clear admin context so tests start clean
        accessService.SetCircuitContext(null);

        // User1: Viewer on SharedOrg (can read), no access to PrivateOrg
        await securityService.AddUserRoleAsync(User1, "Viewer", "SharedOrg", "system", TestTimeout);

        // User2: Editor on SharedOrg, Admin on PrivateOrg
        await securityService.AddUserRoleAsync(User2, "Editor", "SharedOrg", "system", TestTimeout);
        await securityService.AddUserRoleAsync(User2, "Admin", "PrivateOrg", "system", TestTimeout);

        // Break inheritance on SharedOrg/Confidential so parent roles don't propagate
        await securityService.SetPolicyAsync("SharedOrg/Confidential",
            new PartitionAccessPolicy
            {
                BreaksInheritance = true,
            }, TestTimeout);

        // Re-grant User2 as Editor on Confidential (after inheritance break)
        await securityService.AddUserRoleAsync(User2, "Editor", "SharedOrg/Confidential", "system", TestTimeout);
    }

    [Fact(Timeout = 15000)]
    public async Task McpGet_User1CannotReadConfidentialNode_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 (Viewer) should NOT see the Confidential node (read denied by policy)
        SetCurrentUser(User1);
        var result1 = await plugin.Get("SharedOrg/Confidential");
        result1.Should().Contain("Not found",
            "User1 (Viewer) should not be able to read Confidential node");

        // User2 (Editor with explicit grant) should see the Confidential node
        SetCurrentUser(User2);
        var result2 = await plugin.Get("SharedOrg/Confidential");
        result2.Should().NotContain("Not found");
        result2.Should().Contain("Confidential Project");
    }

    [Fact(Timeout = 15000)]
    public async Task McpGet_User1CannotReadPrivateOrg_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 has no access to PrivateOrg at all
        SetCurrentUser(User1);
        var result1 = await plugin.Get("PrivateOrg/Secret");
        result1.Should().Contain("Not found",
            "User1 should not be able to read nodes in PrivateOrg");

        // User2 (Admin) should see the Secret node
        SetCurrentUser(User2);
        var result2 = await plugin.Get("PrivateOrg/Secret");
        result2.Should().NotContain("Not found");
        result2.Should().Contain("Secret Data");
    }

    [Fact(Timeout = 15000)]
    public async Task McpGet_User1CanReadPublicNode()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 (Viewer on SharedOrg) should see the Public node
        SetCurrentUser(User1);
        var result = await plugin.Get("SharedOrg/Public");
        result.Should().NotContain("Not found");
        result.Should().Contain("Public Project");
    }

    [Fact(Timeout = 15000)]
    public async Task McpSearch_User1SeesOnlyPermittedNodes()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 search under SharedOrg should only return Public, not Confidential
        SetCurrentUser(User1);
        var result1 = await plugin.Search("nodeType:Markdown scope:children", "SharedOrg");
        result1.Should().Contain("Public");
        result1.Should().NotContain("Confidential",
            "User1 (Viewer) should not see Confidential project in search results");

        // User2 search under SharedOrg should return both
        SetCurrentUser(User2);
        var result2 = await plugin.Search("nodeType:Markdown scope:children", "SharedOrg");
        result2.Should().Contain("Public");
        result2.Should().Contain("Confidential");
    }

    [Fact(Timeout = 15000)]
    public async Task McpSearch_User1CannotSearchPrivateOrg()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 search under PrivateOrg should return nothing
        SetCurrentUser(User1);
        var result1 = await plugin.Search("scope:children", "PrivateOrg");
        result1.Should().NotContain("Secret",
            "User1 should not see any nodes from PrivateOrg");

        // User2 search under PrivateOrg should find the Secret node
        SetCurrentUser(User2);
        var result2 = await plugin.Search("scope:children", "PrivateOrg");
        result2.Should().Contain("Secret");
    }

    [Fact(Timeout = 15000)]
    public async Task McpUpdate_User1CannotUpdate_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();
        var options = Mesh.JsonSerializerOptions;

        // Get the Public node via query
        var publicNode = await MeshQuery.QueryAsync<MeshNode>("path:SharedOrg/Public scope:exact").FirstOrDefaultAsync();
        publicNode.Should().NotBeNull();

        // User1 (Viewer on SharedOrg) should NOT be able to update the Public node
        SetCurrentUser(User1);
        var updatedNode = publicNode! with { Name = "Hacked by User1" };
        var updateJson = JsonSerializer.Serialize(new[] { updatedNode }, options);
        var updateResult1 = await plugin.Update(updateJson);
        updateResult1.Should().Contain("Error",
            "User1 (Viewer) should not be able to update nodes");

        // User2 (Editor on SharedOrg) should be able to update
        SetCurrentUser(User2);
        var updatedNode2 = publicNode with { Name = "Updated by User2" };
        var updateJson2 = JsonSerializer.Serialize(new[] { updatedNode2 }, options);
        var updateResult2 = await plugin.Update(updateJson2);
        updateResult2.Should().Contain("Updated");

        // Verify the update persisted
        var reloaded = await MeshQuery.QueryAsync<MeshNode>("path:SharedOrg/Public scope:exact").FirstOrDefaultAsync();
        reloaded!.Name.Should().Be("Updated by User2");
    }

    [Fact(Timeout = 15000)]
    public async Task McpUpdate_User1CannotUpdatePrivateOrg_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();
        var options = Mesh.JsonSerializerOptions;

        var secretNode = await MeshQuery.QueryAsync<MeshNode>("path:PrivateOrg/Secret scope:exact").FirstOrDefaultAsync();
        secretNode.Should().NotBeNull();

        // User1 should NOT be able to update Secret node (no permissions at all)
        SetCurrentUser(User1);
        var updatedNode = secretNode! with { Name = "Hacked by User1" };
        var updateJson = JsonSerializer.Serialize(new[] { updatedNode }, options);
        var updateResult = await plugin.Update(updateJson);
        updateResult.Should().Contain("Error",
            "User1 should not be able to update nodes in PrivateOrg");

        // Verify name was NOT changed
        var reloaded = await MeshQuery.QueryAsync<MeshNode>("path:PrivateOrg/Secret scope:exact").FirstOrDefaultAsync();
        reloaded!.Name.Should().Be("Secret Data");
    }
}
