using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that queries respect access control (user sees only permitted nodes).
/// </summary>
[Collection("PostgreSql")]
public class AccessControlQueryTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public AccessControlQueryTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task SeedDataAndPermissionsAsync()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;
        var ac = _fixture.AccessControl;

        // Seed nodes
        await adapter.WriteAsync(new MeshNode("Story1", "ACME/Project")
        {
            Name = "Story One",
            NodeType = "Story"
        }, _options);
        await adapter.WriteAsync(new MeshNode("Story2", "ACME/Project")
        {
            Name = "Story Two",
            NodeType = "Story"
        }, _options);
        await adapter.WriteAsync(new MeshNode("Alice", "ACME/Team")
        {
            Name = "Alice",
            NodeType = "Person"
        }, _options);
        await adapter.WriteAsync(new MeshNode("Project", "Contoso")
        {
            Name = "Contoso Project",
            NodeType = "Project"
        }, _options);

        // Grant access
        // alice has full access to ACME
        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true);

        // bob only has access to ACME/Project
        await ac.GrantAsync("ACME/Project", "bob", "Read", isAllow: true);

        // Public has access to Contoso
        await ac.GrantAsync("Contoso", "Public", "Read", isAllow: true);
    }

    [Fact]
    public async Task AliceSeesAllAcmeNodes()
    {
        await SeedDataAndPermissionsAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "alice");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(3);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo("ACME/Project/Story1", "ACME/Project/Story2", "ACME/Team/Alice");
    }

    [Fact]
    public async Task BobSeesOnlyAcmeProjectNodes()
    {
        await SeedDataAndPermissionsAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "bob");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        // Bob only has Read on ACME/Project, so sees Story1 and Story2
        // but NOT ACME/Team/Alice
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo("ACME/Project/Story1", "ACME/Project/Story2");
    }

    [Fact]
    public async Task CharlieSeesNothing()
    {
        await SeedDataAndPermissionsAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "charlie");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task DeniedSubtreeExcluded()
    {
        await SeedDataAndPermissionsAsync();
        var ac = _fixture.AccessControl;

        // Deny alice access to ACME/Team
        await ac.GrantAsync("ACME/Team", "alice", "Read", isAllow: false);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "alice");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        // Alice should see Story1 and Story2 but NOT Alice (ACME/Team denied)
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo("ACME/Project/Story1", "ACME/Project/Story2");
    }

    [Fact]
    public async Task QueryWithoutUserIdDefaultsToPublicFiltering()
    {
        await SeedDataAndPermissionsAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // No userId - defaults to "Public" user via GetEffectiveUserId.
        // Public has Read on Contoso only, so querying all nodes should return only Contoso nodes.
        var request = MeshQueryRequest.FromQuery("scope:descendants");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(1);
        results.Cast<MeshNode>().Single().Path.Should().Be("Contoso/Project");
    }

    [Fact]
    public async Task PublicUserSeesOnlyPublicNodes()
    {
        await SeedDataAndPermissionsAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Explicit "Public" userId — should see Contoso nodes but NOT ACME nodes
        var request = MeshQueryRequest.FromQuery("scope:descendants", "Public");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(1);
        results.Cast<MeshNode>().Single().Path.Should().Be("Contoso/Project");
    }

    [Fact]
    public async Task AuthenticatedUserInheritsPublicAccess()
    {
        await SeedDataAndPermissionsAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Alice has Read on ACME. Public has Read on Contoso.
        // Alice should see both ACME nodes AND Contoso nodes via Public inheritance.
        var request = MeshQueryRequest.FromQuery("scope:descendants", "alice");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(4);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo("ACME/Project/Story1", "ACME/Project/Story2", "ACME/Team/Alice", "Contoso/Project");
    }

    [Fact]
    public async Task NestedGroupPermissionsExpandRecursively()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;
        var ac = _fixture.AccessControl;

        // Seed nodes
        await adapter.WriteAsync(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" }, _options);
        await adapter.WriteAsync(new MeshNode("Doc2", "ACME/Docs") { Name = "Doc Two", NodeType = "Document" }, _options);

        // Create nested groups: all-staff -> editors -> reviewers
        // reviewers contains dave
        await ac.AddGroupMemberAsync("reviewers", "dave");
        // editors contains the reviewers group
        await ac.AddGroupMemberAsync("editors", "reviewers");
        // all-staff contains the editors group
        await ac.AddGroupMemberAsync("all-staff", "editors");

        // Grant Read on ACME to all-staff group
        await ac.GrantAsync("ACME", "all-staff", "Read", isAllow: true);

        // dave should see ACME nodes via: all-staff -> editors -> reviewers -> dave
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "dave");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo("ACME/Docs/Doc1", "ACME/Docs/Doc2");
    }

    [Fact]
    public async Task NestedGroupDenyOverridesParentGroupAllow()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;
        var ac = _fixture.AccessControl;

        // Seed nodes
        await adapter.WriteAsync(new MeshNode("Public", "ACME/Docs") { Name = "Public Doc", NodeType = "Document" }, _options);
        await adapter.WriteAsync(new MeshNode("Secret", "ACME/Secret") { Name = "Secret Doc", NodeType = "Document" }, _options);

        // Group: team contains eve
        await ac.AddGroupMemberAsync("team", "eve");

        // Allow team Read on ACME
        await ac.GrantAsync("ACME", "team", "Read", isAllow: true);
        // Deny eve specifically on ACME/Secret
        await ac.GrantAsync("ACME/Secret", "eve", "Read", isAllow: false);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "eve");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        // eve sees ACME/Docs/Public but NOT ACME/Secret/Secret (denied)
        results.Should().HaveCount(1);
        results.Cast<MeshNode>().Single().Path.Should().Be("ACME/Docs/Public");
    }

    [Fact]
    public async Task NodeTypeDefinitionsAlwaysVisible()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        // Seed a NodeType definition and a regular node — no access grants at all
        await adapter.WriteAsync(new MeshNode("Organization", "") { Name = "Organization", NodeType = "NodeType" }, _options);
        await adapter.WriteAsync(new MeshNode("ACME", "") { Name = "ACME Corp", NodeType = "Organization" }, _options);
        await adapter.WriteAsync(new MeshNode("Secret", "Private") { Name = "Secret", NodeType = "Document" }, _options);

        // Query as unknown user with zero grants
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("scope:descendants", "nobody");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        var paths = results.Cast<MeshNode>().Select(n => n.Path).ToList();
        paths.Should().Contain("Organization", "NodeType definitions are always publicly readable");
        paths.Should().NotContain("ACME", "Organization instances require explicit grants");
        paths.Should().NotContain("Private/Secret", "Regular nodes require explicit grants");
    }
}
