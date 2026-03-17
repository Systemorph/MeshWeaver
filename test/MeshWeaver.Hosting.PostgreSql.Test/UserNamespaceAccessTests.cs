using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that access control in the User namespace works correctly with PostgreSQL:
/// 1) User/<name> (the User node itself) is visible to any authenticated user via public-read.
/// 2) User/<name>/<subnode> is visible ONLY to the owner (<name>) — not to other users.
/// 3) When an explicit access grant is added for <subnode>, it becomes visible to others.
/// </summary>
[Collection("PostgreSql")]
public class UserNamespaceAccessTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public UserNamespaceAccessTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task SeedUserNamespaceDataAsync()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;
        var ac = _fixture.AccessControl;

        // Register User as a public-read node type
        await ac.SyncNodeTypePermissionsAsync([
            new NodeTypePermission("User", PublicRead: true)
        ], TestContext.Current.CancellationToken);

        // Seed User nodes
        await adapter.WriteAsync(new MeshNode("Alice", "User")
        {
            Name = "Alice",
            NodeType = "User"
        }, _options, TestContext.Current.CancellationToken);

        await adapter.WriteAsync(new MeshNode("Bob", "User")
        {
            Name = "Bob",
            NodeType = "User"
        }, _options, TestContext.Current.CancellationToken);

        // Seed a subnode under Alice's user namespace
        await adapter.WriteAsync(new MeshNode("MyProject", "User/Alice")
        {
            Name = "Alice's Project",
            NodeType = "Markdown"
        }, _options, TestContext.Current.CancellationToken);

        // Grant Alice access to her own user namespace (simulates UserScopeGrantHandler)
        await ac.GrantAsync("User/Alice", "Alice", "Read", isAllow: true, TestContext.Current.CancellationToken);

        // Grant Bob access to his own user namespace
        await ac.GrantAsync("User/Bob", "Bob", "Read", isAllow: true, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UserNode_IsVisibleToAnyAuthenticatedUser()
    {
        await SeedUserNamespaceDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Bob queries for User/Alice — should be visible via public-read on User nodeType
        var request = MeshQueryRequest.FromQuery("path:User/Alice", "Bob");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results.Should().HaveCount(1, "User/Alice should be visible to Bob via public-read on User nodeType");
        results[0].Path.Should().Be("User/Alice");
        results[0].Name.Should().Be("Alice");
    }

    [Fact]
    public async Task UserSubnode_IsNotVisibleToOtherUsers()
    {
        await SeedUserNamespaceDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Bob queries for User/Alice/MyProject — should NOT be visible (no access grant)
        var request = MeshQueryRequest.FromQuery("path:User/Alice/MyProject", "Bob");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results.Should().BeEmpty(
            "User/Alice/MyProject should NOT be visible to Bob — " +
            "subnodes under a User namespace require explicit access");
    }

    [Fact]
    public async Task UserSubnode_IsVisibleToOwner()
    {
        await SeedUserNamespaceDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Alice queries for her own subnode — should be visible (she has Read on User/Alice)
        var request = MeshQueryRequest.FromQuery("path:User/Alice/MyProject", "Alice");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results.Should().HaveCount(1, "User/Alice/MyProject should be visible to Alice (owner)");
        results[0].Path.Should().Be("User/Alice/MyProject");
        results[0].Name.Should().Be("Alice's Project");
    }

    [Fact]
    public async Task UserSubnode_BecomesVisibleAfterExplicitGrant()
    {
        await SeedUserNamespaceDataAsync();
        var ac = _fixture.AccessControl;
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Verify Bob cannot see it initially
        var request = MeshQueryRequest.FromQuery("path:User/Alice/MyProject", "Bob");
        var resultsBefore = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) resultsBefore.Add(node);
        }
        resultsBefore.Should().BeEmpty("Bob should not see Alice's subnode before access grant");

        // Grant Bob explicit Read access to User/Alice/MyProject
        await ac.GrantAsync("User/Alice/MyProject", "Bob", "Read", isAllow: true, TestContext.Current.CancellationToken);

        // Now Bob should see it
        var resultsAfter = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) resultsAfter.Add(node);
        }

        resultsAfter.Should().HaveCount(1,
            "User/Alice/MyProject should be visible to Bob after explicit access grant");
        resultsAfter[0].Path.Should().Be("User/Alice/MyProject");
    }

    [Fact]
    public async Task UserNamespace_DescendantQuery_OwnerSeesSubnodes_OtherUserDoesNot()
    {
        await SeedUserNamespaceDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Alice queries descendants of User/Alice — should see MyProject
        var aliceRequest = MeshQueryRequest.FromQuery("path:User/Alice scope:descendants", "Alice");
        var aliceResults = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(aliceRequest, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) aliceResults.Add(node);
        }
        aliceResults.Should().Contain(n => n.Path == "User/Alice/MyProject",
            "Alice should see her own subnodes in descendant queries");

        // Bob queries descendants of User/Alice — should NOT see MyProject
        var bobRequest = MeshQueryRequest.FromQuery("path:User/Alice scope:descendants", "Bob");
        var bobResults = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(bobRequest, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) bobResults.Add(node);
        }
        bobResults.Should().NotContain(n => n.Path == "User/Alice/MyProject",
            "Bob should NOT see Alice's subnodes in descendant queries without explicit access");
    }

    [Fact]
    public async Task UserNamespace_GroupMembership_GrantsAccessToSubnode()
    {
        await SeedUserNamespaceDataAsync();
        var ac = _fixture.AccessControl;
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Create a group and add Bob as member
        await ac.AddGroupMemberAsync("alice-collaborators", "Bob", TestContext.Current.CancellationToken);

        // Grant the group Read access to Alice's subnode
        await ac.GrantAsync("User/Alice/MyProject", "alice-collaborators", "Read", isAllow: true, TestContext.Current.CancellationToken);

        // Bob should now see it via group membership
        var request = MeshQueryRequest.FromQuery("path:User/Alice/MyProject", "Bob");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results.Should().HaveCount(1,
            "User/Alice/MyProject should be visible to Bob via group membership access grant");
        results[0].Path.Should().Be("User/Alice/MyProject");
    }
}
