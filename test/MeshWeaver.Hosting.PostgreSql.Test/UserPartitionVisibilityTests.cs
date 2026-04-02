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

    private async Task SeedTwoUserPartitionsAsync()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;
        var ac = _fixture.AccessControl;

        // Register User as a public-read node type (profile visible, content private)
        await ac.SyncNodeTypePermissionsAsync([
            new NodeTypePermission("User", PublicRead: true)
        ], TestContext.Current.CancellationToken);

        // Create user nodes
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

        // Create private content in Alice's partition
        await adapter.WriteAsync(new MeshNode("SecretProject", "User/Alice")
        {
            Name = "Alice's Secret Project",
            NodeType = "Markdown"
        }, _options, TestContext.Current.CancellationToken);

        await adapter.WriteAsync(new MeshNode("Notes", "User/Alice")
        {
            Name = "Alice's Private Notes",
            NodeType = "Markdown"
        }, _options, TestContext.Current.CancellationToken);

        // Create private content in Bob's partition
        await adapter.WriteAsync(new MeshNode("MyWork", "User/Bob")
        {
            Name = "Bob's Work",
            NodeType = "Markdown"
        }, _options, TestContext.Current.CancellationToken);

        // Grant each user full access to their own namespace
        foreach (var perm in new[] { "Read", "Create", "Update", "Delete", "Comment", "Execute" })
        {
            await ac.GrantAsync("User/Alice", "Alice", perm, isAllow: true, TestContext.Current.CancellationToken);
            await ac.GrantAsync("User/Bob", "Bob", perm, isAllow: true, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task BobCannotSeeAlicePartitionContent()
    {
        await SeedTwoUserPartitionsAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:User/Alice scope:descendants", "Bob");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        // Bob should see zero content nodes — subnodes require explicit access
        results
            .Where(n => n.NodeType != "User")
            .Should().BeEmpty("Bob must not see Alice's partition content");
    }

    [Fact]
    public async Task AliceCanSeeOwnPartitionContent()
    {
        await SeedTwoUserPartitionsAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:User/Alice scope:descendants", "Alice");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results.Should().Contain(n => n.Name == "Alice's Secret Project");
        results.Should().Contain(n => n.Name == "Alice's Private Notes");
    }

    [Fact]
    public async Task AliceCannotSeeBobPartitionContent()
    {
        await SeedTwoUserPartitionsAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:User/Bob scope:descendants", "Alice");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results
            .Where(n => n.NodeType != "User")
            .Should().BeEmpty("Alice must not see Bob's partition content");
    }

    [Fact]
    public async Task BobCanSeeOwnPartitionContent()
    {
        await SeedTwoUserPartitionsAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:User/Bob scope:descendants", "Bob");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results.Should().Contain(n => n.Name == "Bob's Work");
    }

    [Fact]
    public async Task UnknownUserCannotSeeAnyPartitionContent()
    {
        await SeedTwoUserPartitionsAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Charlie has no grants at all
        var request = MeshQueryRequest.FromQuery("path:User scope:descendants", "Charlie");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        // Charlie should only see User nodes (public-read), not any partition content
        results.Where(n => n.NodeType != "User")
            .Should().BeEmpty("Unknown user must not see any partition content");
    }

    [Fact]
    public async Task ExplicitGrant_MakesPartitionContentVisible()
    {
        await SeedTwoUserPartitionsAsync();
        var ac = _fixture.AccessControl;
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Initially Bob cannot see Alice's project
        var request = MeshQueryRequest.FromQuery("path:User/Alice/SecretProject", "Bob");
        var before = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) before.Add(node);
        }
        before.Should().BeEmpty("Bob has no access before explicit grant");

        // Alice shares her project with Bob
        await ac.GrantAsync("User/Alice/SecretProject", "Bob", "Read", isAllow: true,
            TestContext.Current.CancellationToken);

        // Now Bob can see it
        var after = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) after.Add(node);
        }

        after.Should().HaveCount(1);
        after[0].Name.Should().Be("Alice's Secret Project");
    }
}
