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
/// Verifies that users cannot see other users' partition data in PostgreSQL.
/// Each user has Admin on their own User/<name> namespace.
/// Subnodes under User/<name>/ must be invisible to other users.
/// </summary>
[Collection("PostgreSql")]
public class UserPartitionVisibilityTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public UserPartitionVisibilityTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private List<MeshNode> Query(PostgreSqlMeshQuery query, MeshQueryRequest request)
        => query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit()
            .OfType<MeshNode>().ToList();

    private void SeedTwoUserPartitions()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;
        var ac = _fixture.AccessControl;

        // Register User as a public-read node type (profile visible, content private)
        ac.SyncNodeTypePermissionsAsync([
            new NodeTypePermission("User", PublicRead: true)
        ], ct).Run().Should().Within(30.Seconds()).Emit();

        // Create user nodes
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

        // Create private content in Alice's partition
        adapter.Write(new MeshNode("SecretProject", "User/Alice")
        {
            Name = "Alice's Secret Project",
            NodeType = "Markdown"
        }, _options).Should().Within(30.Seconds()).Emit();

        adapter.Write(new MeshNode("Notes", "User/Alice")
        {
            Name = "Alice's Private Notes",
            NodeType = "Markdown"
        }, _options).Should().Within(30.Seconds()).Emit();

        // Create private content in Bob's partition
        adapter.Write(new MeshNode("MyWork", "User/Bob")
        {
            Name = "Bob's Work",
            NodeType = "Markdown"
        }, _options).Should().Within(30.Seconds()).Emit();

        // Grant each user full access to their own namespace
        foreach (var perm in new[] { "Read", "Create", "Update", "Delete", "Comment", "Execute" })
        {
            ac.Grant("User/Alice", "Alice", perm, isAllow: true, ct).Should().Within(30.Seconds()).Emit();
            ac.Grant("User/Bob", "Bob", perm, isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        }
    }

    [Fact]
    public void BobCannotSeeAlicePartitionContent()
    {
        SeedTwoUserPartitions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:User/Alice scope:descendants", "Bob");
        var results = Query(query, request);

        // Bob should see zero content nodes â€” subnodes require explicit access
        results
            .Where(n => n.NodeType != "User")
            .Should().BeEmpty("Bob must not see Alice's partition content");
    }

    [Fact]
    public void AliceCanSeeOwnPartitionContent()
    {
        SeedTwoUserPartitions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:User/Alice scope:descendants", "Alice");
        var results = Query(query, request);

        results.Should().Contain(n => n.Name == "Alice's Secret Project");
        results.Should().Contain(n => n.Name == "Alice's Private Notes");
    }

    [Fact]
    public void AliceCannotSeeBobPartitionContent()
    {
        SeedTwoUserPartitions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:User/Bob scope:descendants", "Alice");
        var results = Query(query, request);

        results
            .Where(n => n.NodeType != "User")
            .Should().BeEmpty("Alice must not see Bob's partition content");
    }

    [Fact]
    public void BobCanSeeOwnPartitionContent()
    {
        SeedTwoUserPartitions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:User/Bob scope:descendants", "Bob");
        var results = Query(query, request);

        results.Should().Contain(n => n.Name == "Bob's Work");
    }

    [Fact]
    public void UnknownUserCannotSeeAnyPartitionContent()
    {
        SeedTwoUserPartitions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Charlie has no grants at all
        var request = MeshQueryRequest.FromQuery("path:User scope:descendants", "Charlie");
        var results = Query(query, request);

        // Charlie should only see User nodes (public-read), not any partition content
        results.Where(n => n.NodeType != "User")
            .Should().BeEmpty("Unknown user must not see any partition content");
    }

    [Fact]
    public void ExplicitGrant_MakesPartitionContentVisible()
    {
        SeedTwoUserPartitions();
        var ac = _fixture.AccessControl;
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Initially Bob cannot see Alice's project
        var request = MeshQueryRequest.FromQuery("path:User/Alice/SecretProject", "Bob");
        var before = Query(query, request);
        before.Should().BeEmpty("Bob has no access before explicit grant");

        // Alice shares her project with Bob
        ac.Grant("User/Alice/SecretProject", "Bob", "Read", isAllow: true, ct: TestContext.Current.CancellationToken).Should().Within(30.Seconds()).Emit();

        // Now Bob can see it
        var after = Query(query, request);

        after.Should().HaveCount(1);
        after[0].Name.Should().Be("Alice's Secret Project");
    }
}
