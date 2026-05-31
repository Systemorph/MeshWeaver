using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that access control in the User namespace works correctly with PostgreSQL:
/// 1) User/<name> (the User node itself) is visible to any authenticated user via public-read.
/// 2) User/<name>/<subnode> is visible ONLY to the owner (<name>) â€” not to other users.
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

    private List<MeshNode> Query(PostgreSqlMeshQuery query, MeshQueryRequest request)
        => query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit()
            .OfType<MeshNode>().ToList();

    private void SeedUserNamespaceData()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;
        var ac = _fixture.AccessControl;

        // Register User as a public-read node type
        ac.SyncNodeTypePermissionsAsync([
            new NodeTypePermission("User", PublicRead: true)
        ], ct).Run().Should().Within(30.Seconds()).Emit();

        // Seed User nodes
        adapter.Write(new MeshNode("Alice", "User")
        {
            Name = "Alice",
            NodeType = "User"
        }, _options).Should().Within(30.Seconds()).Emit();

        adapter.Write(new MeshNode("Bob", "User")
        {
            Name = "Bob",
            NodeType = "User"
        }, _options).Should().Within(30.Seconds()).Emit();

        // Seed a subnode under Alice's user namespace
        adapter.Write(new MeshNode("MyProject", "User/Alice")
        {
            Name = "Alice's Project",
            NodeType = "Markdown"
        }, _options).Should().Within(30.Seconds()).Emit();

        // Grant Alice Admin (full) access to her own user namespace (simulates UserScopeGrantHandler)
        foreach (var perm in new[] { "Read", "Create", "Update", "Delete", "Comment", "Execute" })
        {
            ac.Grant("User/Alice", "Alice", perm, isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        }

        // Grant Bob Admin (full) access to his own user namespace
        foreach (var perm in new[] { "Read", "Create", "Update", "Delete", "Comment", "Execute" })
        {
            ac.Grant("User/Bob", "Bob", perm, isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        }
    }

    [Fact]
    public void UserNode_IsVisibleToAnyAuthenticatedUser()
    {
        SeedUserNamespaceData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Bob queries for User/Alice â€” should be visible via public-read on User nodeType
        var request = MeshQueryRequest.FromQuery("path:User/Alice", "Bob");
        var results = Query(query, request);

        results.Should().HaveCount(1, "User/Alice should be visible to Bob via public-read on User nodeType");
        results[0].Path.Should().Be("User/Alice");
        results[0].Name.Should().Be("Alice");
    }

    [Fact]
    public void UserSubnode_IsNotVisibleToOtherUsers()
    {
        SeedUserNamespaceData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Bob queries for User/Alice/MyProject â€” should NOT be visible (no access grant)
        var request = MeshQueryRequest.FromQuery("path:User/Alice/MyProject", "Bob");
        var results = Query(query, request);

        results.Should().BeEmpty(
            "User/Alice/MyProject should NOT be visible to Bob â€” " +
            "subnodes under a User namespace require explicit access");
    }

    [Fact]
    public void UserSubnode_IsVisibleToOwner()
    {
        SeedUserNamespaceData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Alice queries for her own subnode â€” should be visible (she has Read on User/Alice)
        var request = MeshQueryRequest.FromQuery("path:User/Alice/MyProject", "Alice");
        var results = Query(query, request);

        results.Should().HaveCount(1, "User/Alice/MyProject should be visible to Alice (owner)");
        results[0].Path.Should().Be("User/Alice/MyProject");
        results[0].Name.Should().Be("Alice's Project");
    }

    [Fact]
    public void UserSubnode_BecomesVisibleAfterExplicitGrant()
    {
        SeedUserNamespaceData();
        var ac = _fixture.AccessControl;
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Verify Bob cannot see it initially
        var request = MeshQueryRequest.FromQuery("path:User/Alice/MyProject", "Bob");
        var resultsBefore = Query(query, request);
        resultsBefore.Should().BeEmpty("Bob should not see Alice's subnode before access grant");

        // Grant Bob explicit Read access to User/Alice/MyProject
        ac.Grant("User/Alice/MyProject", "Bob", "Read", isAllow: true, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        // Now Bob should see it
        var resultsAfter = Query(query, request);

        resultsAfter.Should().HaveCount(1,
            "User/Alice/MyProject should be visible to Bob after explicit access grant");
        resultsAfter[0].Path.Should().Be("User/Alice/MyProject");
    }

    [Fact]
    public void UserNamespace_DescendantQuery_OwnerSeesSubnodes_OtherUserDoesNot()
    {
        SeedUserNamespaceData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Alice queries descendants of User/Alice — should see MyProject
        var aliceRequest = MeshQueryRequest.FromQuery("path:User/Alice scope:descendants", "Alice");
        var aliceResults = Query(query, aliceRequest);
        aliceResults.Should().Contain(n => n.Path == "User/Alice/MyProject",
            "Alice should see her own subnodes in descendant queries");

        // Bob queries descendants of User/Alice — should NOT see MyProject
        var bobRequest = MeshQueryRequest.FromQuery("path:User/Alice scope:descendants", "Bob");
        var bobResults = Query(query, bobRequest);
        bobResults.Should().NotContain(n => n.Path == "User/Alice/MyProject",
            "Bob should NOT see Alice's subnodes in descendant queries without explicit access");
    }

    [Fact]
    public void UserNamespace_GroupMembership_GrantsAccessToSubnode()
    {
        SeedUserNamespaceData();
        var ac = _fixture.AccessControl;
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Create a group and add Bob as member
        ac.AddGroupMemberAsync("alice-collaborators", "Bob", TestContext.Current.CancellationToken)
            .Run().Should().Within(30.Seconds()).Emit();

        // Grant the group Read access to Alice's subnode
        ac.Grant("User/Alice/MyProject", "alice-collaborators", "Read", isAllow: true, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        // Bob should now see it via group membership
        var request = MeshQueryRequest.FromQuery("path:User/Alice/MyProject", "Bob");
        var results = Query(query, request);

        results.Should().HaveCount(1,
            "User/Alice/MyProject should be visible to Bob via group membership access grant");
        results[0].Path.Should().Be("User/Alice/MyProject");
    }
}
