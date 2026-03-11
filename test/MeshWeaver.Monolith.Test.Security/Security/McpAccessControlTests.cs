using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Blazor.AI;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Monolith.Test.Security;

/// <summary>
/// Tests that MCP operations respect access control when invoked via API tokens.
/// Each token maps to a user identity, and MCP operations should only return data
/// the authenticated user is permitted to see — tokens get 1:1 same access rights as users.
/// </summary>
public class McpAccessControlTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    private const string User1 = "user1@example.com";
    private const string User2 = "user2@example.com";

    private string? _tokenUser1;
    private string? _tokenUser2;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMcp().AddRowLevelSecurity();

    /// <summary>
    /// Creates an API token for a user and stores it as a MeshNode.
    /// Returns the raw token string (mw_...) that can be used with LoginWithToken.
    /// </summary>
    private async Task<string> CreateApiTokenAsync(string userId, string userName)
    {
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = ValidateTokenRequest.TokenPrefix + Convert.ToBase64String(rawBytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var hash = ValidateTokenRequest.HashToken(rawToken);
        var hashPrefix = hash[..12];

        var tokenNode = new MeshNode(hashPrefix, "ApiToken")
        {
            Name = $"Token for {userName}",
            NodeType = "ApiToken",
            Content = new ApiToken
            {
                TokenHash = hash,
                UserId = userId,
                UserName = userName,
                UserEmail = userId,
                Label = "Test",
                CreatedAt = DateTimeOffset.UtcNow,
            }
        };

        await NodeFactory.CreateNodeAsync(tokenNode, ct: TestTimeout);
        return rawToken;
    }

    /// <summary>
    /// Logs in a user via API token — sends ValidateTokenRequest through the message hub,
    /// same flow as UserContextMiddleware uses for Bearer tokens.
    /// </summary>
    private async Task LoginWithToken(string rawToken)
    {
        var response = await UserContextMiddleware.ValidateTokenViaHubAsync(rawToken, Mesh);
        response.Should().NotBeNull("token validation should return a response");
        response!.Success.Should().BeTrue("token should be valid, got error: {0}", response.Error);

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = response.UserEmail!,
            Name = response.UserName ?? "",
            Email = response.UserEmail!,
        });
    }

    private McpMeshPlugin CreatePlugin()
        => new(Mesh);

    /// <summary>
    /// Sets up the test data and creates API tokens for both users.
    /// </summary>
    private async Task SetupTestData()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Admin context for data seeding
        await securityService.AddUserRoleAsync("setup-admin", "Admin", null, "system", TestTimeout);
        accessService.SetCircuitContext(new AccessContext { ObjectId = "setup-admin", Name = "Setup Admin" });

        // Create namespace nodes
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

        // Create API tokens for test users (while admin context is active)
        _tokenUser1 = await CreateApiTokenAsync(User1, "User One");
        _tokenUser2 = await CreateApiTokenAsync(User2, "User Two");

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

    [Fact(Timeout = 10000)]
    public async Task McpGet_User1CannotReadConfidentialNode_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 (Viewer) should NOT see the Confidential node (read denied by policy)
        await LoginWithToken(_tokenUser1!);
        var result1 = await plugin.Get("SharedOrg/Confidential");
        result1.Should().Contain("Not found",
            "User1 (Viewer) should not be able to read Confidential node");

        // User2 (Editor with explicit grant) should see the Confidential node
        await LoginWithToken(_tokenUser2!);
        var result2 = await plugin.Get("SharedOrg/Confidential");
        result2.Should().NotContain("Not found");
        result2.Should().Contain("Confidential Project");
    }

    [Fact(Timeout = 10000)]
    public async Task McpGet_User1CannotReadPrivateOrg_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 has no access to PrivateOrg at all
        await LoginWithToken(_tokenUser1!);
        var result1 = await plugin.Get("PrivateOrg/Secret");
        result1.Should().Contain("Not found",
            "User1 should not be able to read nodes in PrivateOrg");

        // User2 (Admin) should see the Secret node
        await LoginWithToken(_tokenUser2!);
        var result2 = await plugin.Get("PrivateOrg/Secret");
        result2.Should().NotContain("Not found");
        result2.Should().Contain("Secret Data");
    }

    [Fact(Timeout = 10000)]
    public async Task McpGet_User1CanReadPublicNode()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 (Viewer on SharedOrg) should see the Public node
        await LoginWithToken(_tokenUser1!);
        var result = await plugin.Get("SharedOrg/Public");
        result.Should().NotContain("Not found");
        result.Should().Contain("Public Project");
    }

    [Fact(Timeout = 10000)]
    public async Task McpSearch_User1SeesOnlyPermittedNodes()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 search under SharedOrg should only return Public, not Confidential
        await LoginWithToken(_tokenUser1!);
        var result1 = await plugin.Search("nodeType:Markdown namespace:", "SharedOrg");
        result1.Should().Contain("Public");
        result1.Should().NotContain("Confidential",
            "User1 (Viewer) should not see Confidential project in search results");

        // User2 search under SharedOrg should return both
        await LoginWithToken(_tokenUser2!);
        var result2 = await plugin.Search("nodeType:Markdown namespace:", "SharedOrg");
        result2.Should().Contain("Public");
        result2.Should().Contain("Confidential");
    }

    [Fact(Timeout = 10000)]
    public async Task McpSearch_User1CannotSearchPrivateOrg()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 search under PrivateOrg should return nothing
        await LoginWithToken(_tokenUser1!);
        var result1 = await plugin.Search("namespace:", "PrivateOrg");
        result1.Should().NotContain("Secret",
            "User1 should not see any nodes from PrivateOrg");

        // User2 search under PrivateOrg should find the Secret node
        await LoginWithToken(_tokenUser2!);
        var result2 = await plugin.Search("namespace:", "PrivateOrg");
        result2.Should().Contain("Secret");
    }

    [Fact(Timeout = 10000)]
    public async Task McpUpdate_User1CannotUpdate_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();
        var options = Mesh.JsonSerializerOptions;

        // Query node as User2 (Editor on SharedOrg) — has read access
        await LoginWithToken(_tokenUser2!);
        var publicNode = await MeshQuery.QueryAsync<MeshNode>("path:SharedOrg/Public scope:exact").FirstOrDefaultAsync();
        publicNode.Should().NotBeNull();

        // User1 (Viewer on SharedOrg) should NOT be able to update the Public node
        await LoginWithToken(_tokenUser1!);
        var updatedNode = publicNode! with { Name = "Hacked by User1" };
        var updateJson = JsonSerializer.Serialize(new[] { updatedNode }, options);
        var updateResult1 = await plugin.Update(updateJson);
        updateResult1.Should().Contain("Error",
            "User1 (Viewer) should not be able to update nodes");

        // User2 (Editor on SharedOrg) should be able to update
        await LoginWithToken(_tokenUser2!);
        var updatedNode2 = publicNode with { Name = "Updated by User2" };
        var updateJson2 = JsonSerializer.Serialize(new[] { updatedNode2 }, options);
        var updateResult2 = await plugin.Update(updateJson2);
        updateResult2.Should().Contain("Updated");

        // Verify the update persisted
        var reloaded = await MeshQuery.QueryAsync<MeshNode>("path:SharedOrg/Public scope:exact").FirstOrDefaultAsync();
        reloaded!.Name.Should().Be("Updated by User2");
    }

    [Fact(Timeout = 10000)]
    public async Task McpUpdate_User1CannotUpdatePrivateOrg_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();
        var options = Mesh.JsonSerializerOptions;

        // Query node as User2 (Admin on PrivateOrg) — has read access
        await LoginWithToken(_tokenUser2!);
        var secretNode = await MeshQuery.QueryAsync<MeshNode>("path:PrivateOrg/Secret scope:exact").FirstOrDefaultAsync();
        secretNode.Should().NotBeNull();

        // User1 should NOT be able to update Secret node (no permissions at all)
        await LoginWithToken(_tokenUser1!);
        var updatedNode = secretNode! with { Name = "Hacked by User1" };
        var updateJson = JsonSerializer.Serialize(new[] { updatedNode }, options);
        var updateResult = await plugin.Update(updateJson);
        updateResult.Should().Contain("Error",
            "User1 should not be able to update nodes in PrivateOrg");

        // Verify name was NOT changed (check as User2 who has access)
        await LoginWithToken(_tokenUser2!);
        var reloaded = await MeshQuery.QueryAsync<MeshNode>("path:PrivateOrg/Secret scope:exact").FirstOrDefaultAsync();
        reloaded!.Name.Should().Be("Secret Data");
    }
}
