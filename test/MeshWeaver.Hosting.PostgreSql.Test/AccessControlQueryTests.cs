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

    private List<object> Query(PostgreSqlMeshQuery query, MeshQueryRequest request)
        => query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

    private void Write(MeshNode node)
        => _fixture.StorageAdapter.Write(node, _options).Should().Within(30.Seconds()).Emit();

    private void SeedDataAndPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        // Seed nodes
        Write(new MeshNode("Story1", "ACME/Project") { Name = "Story One", NodeType = "Story" });
        Write(new MeshNode("Story2", "ACME/Project") { Name = "Story Two", NodeType = "Story" });
        Write(new MeshNode("Alice", "ACME/Team") { Name = "Alice", NodeType = "Person" });
        Write(new MeshNode("Project", "Contoso") { Name = "Contoso Project", NodeType = "Project" });

        // Grant access
        // alice has full access to ACME
        ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        // bob only has access to ACME/Project
        ac.Grant("ACME/Project", "bob", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        // Public (authenticated baseline) has access to Contoso
        ac.Grant("Contoso", "Public", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        // Anonymous also has access to Contoso (for default/no-userId queries)
        ac.Grant("Contoso", "Anonymous", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
    }

    [Fact]
    public void AliceSeesAllAcmeNodes()
    {
        SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "alice");

        var results = Query(query, request);

        results.Should().HaveCount(3);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo(new[] { "ACME/Project/Story1", "ACME/Project/Story2", "ACME/Team/Alice" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void BobSeesOnlyAcmeProjectNodes()
    {
        SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "bob");

        var results = Query(query, request);

        // Bob only has Read on ACME/Project, so sees Story1 and Story2
        // but NOT ACME/Team/Alice
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo(new[] { "ACME/Project/Story1", "ACME/Project/Story2" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void CharlieSeesNothing()
    {
        SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "charlie");

        var results = Query(query, request);

        results.Should().BeEmpty();
    }

    [Fact]
    public void DeniedSubtreeExcluded()
    {
        SeedDataAndPermissions();
        var ac = _fixture.AccessControl;

        // Deny alice access to ACME/Team
        ac.Grant("ACME/Team", "alice", "Read", isAllow: false, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "alice");

        var results = Query(query, request);

        // Alice should see Story1 and Story2 but NOT Alice (ACME/Team denied)
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo(new[] { "ACME/Project/Story1", "ACME/Project/Story2" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void QueryWithoutUserIdDefaultsToAnonymousFiltering()
    {
        SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // No userId - defaults to "Anonymous" user via GetEffectiveUserId.
        // Anonymous has Read on Contoso only, so querying all nodes should return only Contoso nodes.
        var request = MeshQueryRequest.FromQuery("scope:descendants");

        var results = Query(query, request);

        results.Should().HaveCount(1);
        results.Cast<MeshNode>().Single().Path.Should().Be("Contoso/Project");
    }

    [Fact]
    public void PublicUserSeesOnlyPublicNodes()
    {
        SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Explicit "Public" userId — should see Contoso nodes but NOT ACME nodes
        var request = MeshQueryRequest.FromQuery("scope:descendants", "Public");

        var results = Query(query, request);

        results.Should().HaveCount(1);
        results.Cast<MeshNode>().Single().Path.Should().Be("Contoso/Project");
    }

    [Fact]
    public void AuthenticatedUserInheritsPublicAccess()
    {
        SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Alice has Read on ACME. Public has Read on Contoso.
        // Alice should see both ACME nodes AND Contoso nodes via Public inheritance.
        var request = MeshQueryRequest.FromQuery("scope:descendants", "alice");

        var results = Query(query, request);

        results.Should().HaveCount(4);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo(new[] { "ACME/Project/Story1", "ACME/Project/Story2", "ACME/Team/Alice", "Contoso/Project" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void NestedGroupPermissionsExpandRecursively()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        // Seed nodes
        Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });
        Write(new MeshNode("Doc2", "ACME/Docs") { Name = "Doc Two", NodeType = "Document" });

        // Create nested groups: all-staff -> editors -> reviewers
        // reviewers contains dave
        ac.AddGroupMemberAsync("reviewers", "dave", ct).Run().Should().Within(30.Seconds()).Emit();
        // editors contains the reviewers group
        ac.AddGroupMemberAsync("editors", "reviewers", ct).Run().Should().Within(30.Seconds()).Emit();
        // all-staff contains the editors group
        ac.AddGroupMemberAsync("all-staff", "editors", ct).Run().Should().Within(30.Seconds()).Emit();

        // Grant Read on ACME to all-staff group
        ac.Grant("ACME", "all-staff", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();

        // dave should see ACME nodes via: all-staff -> editors -> reviewers -> dave
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "dave");

        var results = Query(query, request);

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo(new[] { "ACME/Docs/Doc1", "ACME/Docs/Doc2" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void NestedGroupDenyOverridesParentGroupAllow()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        // Seed nodes
        Write(new MeshNode("Public", "ACME/Docs") { Name = "Public Doc", NodeType = "Document" });
        Write(new MeshNode("Secret", "ACME/Secret") { Name = "Secret Doc", NodeType = "Document" });

        // Group: team contains eve
        ac.AddGroupMemberAsync("team", "eve", ct).Run().Should().Within(30.Seconds()).Emit();

        // Allow team Read on ACME
        ac.Grant("ACME", "team", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        // Deny eve specifically on ACME/Secret
        ac.Grant("ACME/Secret", "eve", "Read", isAllow: false, ct).Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "eve");

        var results = Query(query, request);

        // eve sees ACME/Docs/Public but NOT ACME/Secret/Secret (denied)
        results.Should().HaveCount(1);
        results.Cast<MeshNode>().Single().Path.Should().Be("ACME/Docs/Public");
    }

    [Fact]
    public void NodeTypeDefinitionsAlwaysVisible()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();

        // Register NodeType as public-read (normally done by AddGraph() at startup)
        _fixture.AccessControl.SyncNodeTypePermissionsAsync(
            [new MeshWeaver.Mesh.Security.NodeTypePermission("NodeType", PublicRead: true)], ct)
            .Run().Should().Within(30.Seconds()).Emit();

        // Seed a NodeType definition and a regular node — no access grants at all
        Write(new MeshNode("Space", "") { Name = "Space", NodeType = "NodeType" });
        Write(new MeshNode("ACME", "") { Name = "ACME Corp", NodeType = "Space" });
        Write(new MeshNode("Secret", "Private") { Name = "Secret", NodeType = "Document" });

        // Query as unknown user with zero grants
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("scope:descendants", "nobody");

        var results = Query(query, request);

        var paths = results.Cast<MeshNode>().Select(n => n.Path).ToList();
        paths.Should().Contain("Space", "NodeType definitions are always publicly readable");
        paths.Should().NotContain("ACME", "Space instances require explicit grants");
        paths.Should().NotContain("Private/Secret", "Regular nodes require explicit grants");
    }

    [Fact]
    public void PolicyCapsQuery_WriteDeniedByPolicy()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        // Seed nodes
        Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });

        // Grant full access to alice at ACME
        ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        ac.Grant("ACME", "alice", "Update", isAllow: true, ct).Should().Within(30.Seconds()).Emit();

        // Set read-only policy on ACME (deny Create, Update, Delete, Comment)
        ac.SetPolicyAsync("ACME", create: false, update: false, delete: false, comment: false, ct: ct)
            .Run().Should().Within(30.Seconds()).Emit();

        // alice can still read (query sees Doc1 node)
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants nodeType:Document", "alice");
        var results = Query(query, request);

        results.Should().HaveCount(1, "alice should still see Doc1 via Read permission");
        results.Cast<MeshNode>().Single().Path.Should().Be("ACME/Docs/Doc1");

        // But Update permission should be denied by the policy
        ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Update", ct).Run()
            .Should().Within(30.Seconds()).Be(false);
        ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Read", ct).Run()
            .Should().Within(30.Seconds()).Be(true);
    }

    [Fact]
    public void PolicyNodeVisibleButFilterableByNodeType()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        // Seed a regular node and a policy node
        Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });
        ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        ac.SetPolicyAsync("ACME", create: false, update: false, delete: false, comment: false, ct: ct)
            .Run().Should().Within(30.Seconds()).Emit();

        // Unfiltered query includes the _Policy node at the SQL level
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "alice");
        var results = Query(query, request);

        var nodeTypes = results.Cast<MeshNode>().Select(n => n.NodeType).ToList();
        nodeTypes.Should().Contain("PartitionAccessPolicy", "_Policy node is a regular mesh_node");
        nodeTypes.Should().Contain("Document");

        // Filtering by nodeType:Document excludes the _Policy node
        // (context-based ExcludeFromContext filtering is applied at the application layer)
        var filteredRequest = MeshQueryRequest.FromQuery("path:ACME scope:descendants nodeType:Document", "alice");
        var filteredResults = Query(query, filteredRequest);

        filteredResults.Should().HaveCount(1);
        filteredResults.Cast<MeshNode>().Single().NodeType.Should().Be("Document");
    }

    [Fact]
    public void PolicyDeniesOnlyUpdate_ReadQueryStillWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });

        ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        ac.Grant("ACME", "alice", "Update", isAllow: true, ct).Should().Within(30.Seconds()).Emit();

        // Deny only Update — Read should still work
        ac.SetPolicyAsync("ACME", update: false, ct: ct).Run().Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants nodeType:Document", "alice");
        var results = Query(query, request);

        results.Should().HaveCount(1, "alice should still see Doc1 via Read permission");
        ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Read", ct).Run()
            .Should().Within(30.Seconds()).Be(true);
        ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Update", ct).Run()
            .Should().Within(30.Seconds()).Be(false);
    }

    [Fact]
    public void PolicyDeniesRead_QueryReturnsNoResults()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });

        ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        ac.Grant("ACME", "alice", "Update", isAllow: true, ct).Should().Within(30.Seconds()).Emit();

        // Deny Read — query should return nothing
        ac.SetPolicyAsync("ACME", read: false, ct: ct).Run().Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants nodeType:Document", "alice");
        var results = Query(query, request);

        results.Should().BeEmpty("Read denied by policy — alice cannot see any nodes");
    }

    [Fact]
    public void PerPermissionPolicy_GranularDenyPreservesOtherPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });

        // Grant full access
        ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        ac.Grant("ACME", "alice", "Create", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        ac.Grant("ACME", "alice", "Update", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        ac.Grant("ACME", "alice", "Delete", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        ac.Grant("ACME", "alice", "Comment", isAllow: true, ct).Should().Within(30.Seconds()).Emit();

        // Deny only Delete and Comment
        ac.SetPolicyAsync("ACME", delete: false, comment: false, ct: ct).Run().Should().Within(30.Seconds()).Emit();

        ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Read", ct).Run().Should().Within(30.Seconds()).Be(true);
        ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Create", ct).Run().Should().Within(30.Seconds()).Be(true);
        ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Update", ct).Run().Should().Within(30.Seconds()).Be(true);
        ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Delete", ct).Run().Should().Within(30.Seconds()).Be(false);
        ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Comment", ct).Run().Should().Within(30.Seconds()).Be(false);
    }

    /// <summary>
    /// Seeds the node_type_permissions table with public-read entries for User and Space.
    /// </summary>
    private void SeedPublicReadPermissions()
    {
        var ac = _fixture.AccessControl;
        ac.SyncNodeTypePermissionsAsync([
            new NodeTypePermission("User", PublicRead: true),
            new NodeTypePermission("Space", PublicRead: true)
        ], TestContext.Current.CancellationToken).Run().Should().Within(30.Seconds()).Emit();
    }

    [Fact]
    public void PublicReadNodeTypes_VisibleWithoutExplicitGrants()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        SeedPublicReadPermissions();

        // Seed User and Space nodes (public-read types) plus a regular node
        Write(new MeshNode("Roland", "User") { Name = "Roland", NodeType = "User" });
        Write(new MeshNode("Acme") { Name = "Acme Corp", NodeType = "Space" });
        Write(new MeshNode("Secret", "Private") { Name = "Secret Doc", NodeType = "Document" });

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Query as unprivileged user with NO explicit grants
        var request = MeshQueryRequest.FromQuery("scope:descendants", "alice");
        var results = Query(query, request).OfType<MeshNode>().ToList();

        var paths = results.Select(n => n.Path).ToList();
        paths.Should().Contain("User/Roland", "User nodes are publicly readable");
        paths.Should().Contain("Acme", "Space nodes are publicly readable");
        paths.Should().NotContain("Private/Secret", "Document nodes still require explicit grants");
    }

    [Fact]
    public void PublicReadNodeTypes_NotVisibleToAnonymous()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        SeedPublicReadPermissions();

        Write(new MeshNode("Roland", "User") { Name = "Roland", NodeType = "User" });

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("scope:descendants", WellKnownUsers.Anonymous);
        var results = Query(query, request).OfType<MeshNode>().ToList();

        results.Should().BeEmpty("Anonymous users should not see public-read nodes without explicit grants");
    }

    [Fact]
    public void PublicReadNodeTypes_QueryByNodeType()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        SeedPublicReadPermissions();

        Write(new MeshNode("Roland", "User") { Name = "Roland", NodeType = "User" });
        Write(new MeshNode("Alice", "User") { Name = "Alice", NodeType = "User" });
        Write(new MeshNode("Acme") { Name = "Acme", NodeType = "Space" });

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Query by nodeType:User as unprivileged user
        var request = MeshQueryRequest.FromQuery("nodeType:User", "bob");
        var results = Query(query, request).OfType<MeshNode>().ToList();

        results.Should().HaveCount(2, "Both User nodes should be publicly readable");
        results.Select(n => n.Path).Should().BeEquivalentTo(new[] { "User/Roland", "User/Alice" }, JsonSerializerOptions.Default);
    }
}
