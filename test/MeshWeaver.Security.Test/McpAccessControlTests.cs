using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mcp;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests that MCP operations respect access control when invoked via API tokens.
/// Each token maps to a user identity, and MCP operations should only return data
/// the authenticated user is permitted to see — tokens get 1:1 same access rights as users.
/// </summary>
public class McpAccessControlTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan StepTimeout = 30.Seconds();

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
    private async Task<string> CreateApiToken(string userId, string userName)
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
            // Stamp MainNode = userId so SecurePersistence's IsSelfAccess shortcut
            // grants the owning user read access on the validation round-trip.
            // Without this, ValidateTokenRequest's `hub.GetMeshNode(self)` is
            // checked against an unauthenticated context, fails the RLS read,
            // and the validator returns "Token not found".
            MainNode = userId,
            Content = new ApiToken
            {
                TokenHash = hash,
                UserId = userId,
                UserName = userName,
                UserEmail = userId,
                Label = "Test",
                CreatedAt = DateTimeOffset.UtcNow,
                // Roles must include Api so the API-token gate
                // (SecurityService.GetEffectivePermissions's IsApiToken check)
                // doesn't strip the user's permissions to None.
                Roles = ["Editor"],
            }
        };

        await NodeFactory.CreateNode(tokenNode).Should().Within(StepTimeout).Emit();
        return rawToken;
    }

    /// <summary>
    /// Logs in a user via API token — sends ValidateTokenRequest through the message hub,
    /// same flow as UserContextMiddleware uses for Bearer tokens.
    /// </summary>
    private async Task LoginWithToken(string rawToken)
    {
        var response = await UserContextMiddleware.ValidateTokenViaHub(rawToken, Mesh)
            .Should().Within(StepTimeout).Emit();
        response.Should().NotBeNull("token validation should return a response");
        response!.Success.Should().BeTrue("token should be valid, got error: {0}", response.Error ?? "");

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = response.UserId ?? response.UserEmail!,
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
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Stay on the DevLogin admin context (set by MonolithMeshTestBase.InitializeAsync)
        // for the rest of the seed. The previous pattern minted a new "setup-admin"
        // user via runtime CreateNode + immediately switched the AccessContext to
        // it, but the AccessAssignment took a beat to propagate through the synced
        // query that SecurityService rides — so the very next CreateNode (the
        // SharedOrg root) hit "Access denied: Create permission required". Using
        // the DevLogin admin throughout setup is simpler and free of the race.

        // Create namespace nodes
        await SeedTopLevel(new MeshNode("SharedOrg")
        {
            Name = "Shared Organization",
            NodeType = "Group",
        });

        await NodeFactory.CreateNode(new MeshNode("Public", "SharedOrg")
        {
            Name = "Public Project",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Public\nInitial content." },
        }).Should().Within(StepTimeout).Emit();

        await NodeFactory.CreateNode(new MeshNode("Confidential", "SharedOrg")
        {
            Name = "Confidential Project",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Confidential" },
        }).Should().Within(StepTimeout).Emit();

        await SeedTopLevel(new MeshNode("PrivateOrg")
        {
            Name = "Private Organization",
            NodeType = "Group",
        });

        await NodeFactory.CreateNode(new MeshNode("Secret", "PrivateOrg")
        {
            Name = "Secret Data",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Secret" },
        }).Should().Within(StepTimeout).Emit();

        // Create API tokens for test users (while admin context is active)
        _tokenUser1 = await CreateApiToken(User1, "User One");
        _tokenUser2 = await CreateApiToken(User2, "User Two");

        // User1: Viewer on SharedOrg (can read), no access to PrivateOrg
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(User1, "Viewer", "SharedOrg"))
            .Should().Within(StepTimeout).Emit();

        // User2: Editor on SharedOrg, Admin on PrivateOrg
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(User2, "Editor", "SharedOrg"))
            .Should().Within(StepTimeout).Emit();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(User2, "Admin", "PrivateOrg"))
            .Should().Within(StepTimeout).Emit();

        // Break inheritance on SharedOrg/Confidential so parent roles don't propagate.
        // Policy is just a MeshNode at "{ns}/_Policy".
        await meshService.CreateNode(AssignmentNodeFactory.Policy("SharedOrg/Confidential",
            new PartitionAccessPolicy { BreaksInheritance = true }))
            .Should().Within(StepTimeout).Emit();

        // Re-grant User2 as Editor on Confidential (after inheritance break)
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(User2, "Editor", "SharedOrg/Confidential"))
            .Should().Within(StepTimeout).Emit();

        // Clear admin context so tests start clean — moved AFTER all the
        // AccessAssignment writes so the seeds run as the DevLogin admin
        // (who has Create permission everywhere) rather than as anonymous
        // (who fails the AccessAssignment Create access check).
        accessService.SetCircuitContext(null);

        // Wait for the runtime AccessAssignments AND the BreaksInheritance
        // policy on SharedOrg/Confidential to propagate through the synced
        // queries that SecurityService rides. We probe THREE signals:
        //   1. User1 has Read at SharedOrg (Viewer landed)
        //   2. User2 has Update at SharedOrg/Confidential (Editor re-grant +
        //      policy break landed)
        //   3. User1 does NOT have Read at SharedOrg/Confidential — i.e.,
        //      BreaksInheritance has actually flipped, dropping User1's
        //      inherited Viewer. Without (3), the test can race ahead while
        //      the policy synced query still has the empty initial emission,
        //      and User1 ends up reading Confidential through inheritance.
        // hub is Mesh — permission checks use hub.GetEffectivePermissions
        await Mesh.GetEffectivePermissions("SharedOrg", User1)
            .Should().Within(StepTimeout).Match(p => p.HasFlag(Permission.Read));
        await Mesh.GetEffectivePermissions("SharedOrg/Confidential", User2)
            .Should().Within(StepTimeout).Match(p => p.HasFlag(Permission.Update));
        await Mesh.GetEffectivePermissions("SharedOrg/Confidential", User1)
            .Should().Within(StepTimeout).Match(p => !p.HasFlag(Permission.Read));
        // 4. User2 has Read at PrivateOrg/Secret — the Admin-on-PrivateOrg
        //    assignment has propagated AND inherited down to the Secret node.
        //    Without this probe McpUpdate_User1CannotUpdatePrivateOrg_User2Can
        //    races ahead and the User2 read of PrivateOrg/Secret returns null
        //    because the AccessAssignment synced query hasn't landed yet.
        await Mesh.GetEffectivePermissions("PrivateOrg/Secret", User2)
            .Should().Within(StepTimeout).Match(p => p.HasFlag(Permission.Read));
    }

    /// <summary>
    /// Waits until the access-FILTERED query path (the path
    /// <c>McpMeshPlugin.Search</c> rides) reflects the supplied predicate for
    /// the CURRENT ambient access context. The per-result <c>RlsNodeValidator</c>
    /// is validated by the queried partition hub's own scoped
    /// <see cref="SecurityService"/> — distinct from the mesh-hub one settled
    /// by <see cref="SetupTestData"/>'s probes, with per-scope synced
    /// AccessAssignment/Policy queries that settle independently. Call after
    /// <c>LoginWithToken</c> so the wait runs under the same context the
    /// subsequent <c>plugin.Search</c> call uses.
    /// </summary>
    private async Task WaitForFilteredQuery(
        string query, Func<IReadOnlyList<MeshNode>, bool> until)
        => await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Should().Within(StepTimeout).Match(c => until(c.Items));

    [Fact(Timeout = 30000)]
    public async Task McpGet_User1CannotReadConfidentialNode_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 (Viewer) should NOT see the Confidential node (read denied by policy)
        await LoginWithToken(_tokenUser1!);
        var result1 = await plugin.Get("SharedOrg/Confidential").ToObservable()
            .Should().Within(StepTimeout).Emit();
        result1.Should().Contain("Not found",
            "User1 (Viewer) should not be able to read Confidential node");

        // User2 (Editor with explicit grant) should see the Confidential node
        await LoginWithToken(_tokenUser2!);
        var result2 = await plugin.Get("SharedOrg/Confidential").ToObservable()
            .Should().Within(StepTimeout).Emit();
        result2.Should().NotContain("Not found");
        result2.Should().Contain("Confidential Project");
    }

    [Fact(Timeout = 30000)]
    public async Task McpGet_User1CannotReadPrivateOrg_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 has no access to PrivateOrg at all
        await LoginWithToken(_tokenUser1!);
        var result1 = await plugin.Get("PrivateOrg/Secret").ToObservable()
            .Should().Within(StepTimeout).Emit();
        result1.Should().Contain("Not found",
            "User1 should not be able to read nodes in PrivateOrg");

        // User2 (Admin) should see the Secret node
        await LoginWithToken(_tokenUser2!);
        var result2 = await plugin.Get("PrivateOrg/Secret").ToObservable()
            .Should().Within(StepTimeout).Emit();
        result2.Should().NotContain("Not found");
        result2.Should().Contain("Secret Data");
    }

    [Fact(Timeout = 30000)]
    public async Task McpGet_User1CanReadPublicNode()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 (Viewer on SharedOrg) should see the Public node
        await LoginWithToken(_tokenUser1!);
        var result = await plugin.Get("SharedOrg/Public").ToObservable()
            .Should().Within(StepTimeout).Emit();
        result.Should().NotContain("Not found");
        result.Should().Contain("Public Project");
    }

    [Fact(Timeout = 30000)]
    public async Task McpSearch_User1SeesOnlyPermittedNodes()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 search under SharedOrg should only return Public, not Confidential
        await LoginWithToken(_tokenUser1!);
        // Wait until the filtered query path (same path plugin.Search rides)
        // has settled for User1's context — the partition hub's scoped
        // SecurityService lags the mesh-hub one SetupTestData probes, so
        // without this the search can still surface Confidential to the Viewer.
        await WaitForFilteredQuery("namespace:SharedOrg nodeType:Markdown",
            items => items.Any(n => n.Id == "Public")
                     && items.All(n => n.Id != "Confidential"));
        var result1 = await plugin.Search("nodeType:Markdown namespace:", "SharedOrg").ToObservable()
            .Should().Within(StepTimeout).Emit();
        result1.Should().Contain("Public");
        result1.Should().NotContain("Confidential",
            "User1 (Viewer) should not see Confidential project in search results");

        // User2 search under SharedOrg should return both
        await LoginWithToken(_tokenUser2!);
        await WaitForFilteredQuery("namespace:SharedOrg nodeType:Markdown",
            items => items.Any(n => n.Id == "Public")
                     && items.Any(n => n.Id == "Confidential"));
        var result2 = await plugin.Search("nodeType:Markdown namespace:", "SharedOrg").ToObservable()
            .Should().Within(StepTimeout).Emit();
        result2.Should().Contain("Public");
        result2.Should().Contain("Confidential");
    }

    [Fact(Timeout = 30000)]
    public async Task McpSearch_User1CannotSearchPrivateOrg()
    {
        await SetupTestData();
        var plugin = CreatePlugin();

        // User1 search under PrivateOrg should return nothing
        await LoginWithToken(_tokenUser1!);
        var result1 = await plugin.Search("namespace:", "PrivateOrg").ToObservable()
            .Should().Within(StepTimeout).Emit();
        result1.Should().NotContain("Secret",
            "User1 should not see any nodes from PrivateOrg");

        // User2 search under PrivateOrg should find the Secret node
        await LoginWithToken(_tokenUser2!);
        var result2 = await plugin.Search("namespace:", "PrivateOrg").ToObservable()
            .Should().Within(StepTimeout).Emit();
        result2.Should().Contain("Secret");
    }

    [Fact(Timeout = 30000)]
    public async Task McpUpdate_User1CannotUpdate_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();
        var options = Mesh.JsonSerializerOptions;

        // Query node as User2 (Editor on SharedOrg) — has read access
        await LoginWithToken(_tokenUser2!);
        var publicNode = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:SharedOrg/Public"))
            .Should().Within(StepTimeout)
            .Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Count > 0))
            .Items[0];
        publicNode.Should().NotBeNull();

        // User1 (Viewer on SharedOrg) should NOT be able to update the Public node
        await LoginWithToken(_tokenUser1!);
        var updatedNode = publicNode! with { Name = "Hacked by User1" };
        var updateJson = JsonSerializer.Serialize(new[] { updatedNode }, options);
        var updateResult1 = await plugin.Update(updateJson).ToObservable()
            .Should().Within(StepTimeout).Emit();
        updateResult1.Should().Contain("Error",
            "User1 (Viewer) should not be able to update nodes");

        // User2 (Editor on SharedOrg) should be able to update
        await LoginWithToken(_tokenUser2!);
        var updatedNode2 = publicNode with { Name = "Updated by User2" };
        var updateJson2 = JsonSerializer.Serialize(new[] { updatedNode2 }, options);
        var updateResult2 = await plugin.Update(updateJson2).ToObservable()
            .Should().Within(StepTimeout).Emit();
        updateResult2.Should().Contain("Updated");

        // Verify the update persisted
        await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:SharedOrg/Public"))
            .Should().Within(StepTimeout)
            .Match(c => c.Items.Any(n => n.Name == "Updated by User2"));
    }

    [Fact(Timeout = 30000)]
    public async Task McpUpdate_User1CannotUpdatePrivateOrg_User2Can()
    {
        await SetupTestData();
        var plugin = CreatePlugin();
        var options = Mesh.JsonSerializerOptions;

        // Query node as User2 (Admin on PrivateOrg) — has read access
        await LoginWithToken(_tokenUser2!);
        var secretNode = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:PrivateOrg/Secret"))
            .Should().Within(StepTimeout)
            .Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Count > 0))
            .Items[0];
        secretNode.Should().NotBeNull();

        // User1 should NOT be able to update Secret node (no permissions at all)
        await LoginWithToken(_tokenUser1!);
        var updatedNode = secretNode! with { Name = "Hacked by User1" };
        var updateJson = JsonSerializer.Serialize(new[] { updatedNode }, options);
        var updateResult = await plugin.Update(updateJson).ToObservable()
            .Should().Within(StepTimeout).Emit();
        updateResult.Should().Contain("Error",
            "User1 should not be able to update nodes in PrivateOrg");

        // Verify name was NOT changed (check as User2 who has access)
        await LoginWithToken(_tokenUser2!);
        var reloaded = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:PrivateOrg/Secret"))
            .Should().Within(StepTimeout)
            .Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Count > 0))
            .Items[0];
        reloaded!.Name.Should().Be("Secret Data");
    }
}
