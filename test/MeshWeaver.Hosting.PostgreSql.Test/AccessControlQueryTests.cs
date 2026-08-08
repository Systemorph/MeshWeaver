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

    private Task<List<object>> Query(PostgreSqlMeshQuery query, MeshQueryRequest request)
        => query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

    private Task Write(MeshNode node)
        => _fixture.StorageAdapter.Write(node, _options).Should().Within(30.Seconds()).Emit();

    private async Task SeedDataAndPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        // Seed nodes
        await Write(new MeshNode("Story1", "ACME/Project") { Name = "Story One", NodeType = "Story" });
        await Write(new MeshNode("Story2", "ACME/Project") { Name = "Story Two", NodeType = "Story" });
        await Write(new MeshNode("Alice", "ACME/Team") { Name = "Alice", NodeType = "Person" });
        await Write(new MeshNode("Project", "Contoso") { Name = "Contoso Project", NodeType = "Project" });

        // Grant access
        // alice has full access to ACME
        await ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        // bob only has access to ACME/Project
        await ac.Grant("ACME/Project", "bob", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        // Public (authenticated baseline) has access to Contoso
        await ac.Grant("Contoso", "Public", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        // Anonymous also has access to Contoso (for default/no-userId queries)
        await ac.Grant("Contoso", "Anonymous", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
    }

    [Fact]
    public async Task AliceSeesAllAcmeNodes()
    {
        await SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "alice");

        var results = await Query(query, request);

        results.Should().HaveCount(3);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo(new[] { "ACME/Project/Story1", "ACME/Project/Story2", "ACME/Team/Alice" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task BobSeesOnlyAcmeProjectNodes()
    {
        await SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "bob");

        var results = await Query(query, request);

        // Bob only has Read on ACME/Project, so sees Story1 and Story2
        // but NOT ACME/Team/Alice
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo(new[] { "ACME/Project/Story1", "ACME/Project/Story2" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task CharlieSeesNothing()
    {
        await SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "charlie");

        var results = await Query(query, request);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task DeniedSubtreeExcluded()
    {
        await SeedDataAndPermissions();
        var ac = _fixture.AccessControl;

        // Deny alice access to ACME/Team
        await ac.Grant("ACME/Team", "alice", "Read", isAllow: false, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "alice");

        var results = await Query(query, request);

        // Alice should see Story1 and Story2 but NOT Alice (ACME/Team denied)
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo(new[] { "ACME/Project/Story1", "ACME/Project/Story2" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task QueryWithoutUserIdDefaultsToAnonymousFiltering()
    {
        await SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // No userId - defaults to "Anonymous" user via GetEffectiveUserId.
        // Anonymous has Read on Contoso only, so querying all nodes should return only Contoso nodes.
        var request = MeshQueryRequest.FromQuery("scope:descendants");

        var results = await Query(query, request);

        results.Should().HaveCount(1);
        results.Cast<MeshNode>().Single().Path.Should().Be("Contoso/Project");
    }

    [Fact]
    public async Task PublicUserSeesOnlyPublicNodes()
    {
        await SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Explicit "Public" userId — should see Contoso nodes but NOT ACME nodes
        var request = MeshQueryRequest.FromQuery("scope:descendants", "Public");

        var results = await Query(query, request);

        results.Should().HaveCount(1);
        results.Cast<MeshNode>().Single().Path.Should().Be("Contoso/Project");
    }

    [Fact]
    public async Task AuthenticatedUserInheritsPublicAccess()
    {
        await SeedDataAndPermissions();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Alice has Read on ACME. Public has Read on Contoso.
        // Alice should see both ACME nodes AND Contoso nodes via Public inheritance.
        var request = MeshQueryRequest.FromQuery("scope:descendants", "alice");

        var results = await Query(query, request);

        results.Should().HaveCount(4);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo(new[] { "ACME/Project/Story1", "ACME/Project/Story2", "ACME/Team/Alice", "Contoso/Project" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task NestedGroupPermissionsExpandRecursively()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        // Seed nodes
        await Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });
        await Write(new MeshNode("Doc2", "ACME/Docs") { Name = "Doc Two", NodeType = "Document" });

        // Create nested groups: all-staff -> editors -> reviewers
        // reviewers contains dave
        await ac.AddGroupMemberAsync("reviewers", "dave", ct).Run().Should().Within(30.Seconds()).Emit();
        // editors contains the reviewers group
        await ac.AddGroupMemberAsync("editors", "reviewers", ct).Run().Should().Within(30.Seconds()).Emit();
        // all-staff contains the editors group
        await ac.AddGroupMemberAsync("all-staff", "editors", ct).Run().Should().Within(30.Seconds()).Emit();

        // Grant Read on ACME to all-staff group
        await ac.Grant("ACME", "all-staff", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();

        // dave should see ACME nodes via: all-staff -> editors -> reviewers -> dave
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "dave");

        var results = await Query(query, request);

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Path)
            .Should().BeEquivalentTo(new[] { "ACME/Docs/Doc1", "ACME/Docs/Doc2" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task NestedGroupDenyOverridesParentGroupAllow()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        // Seed nodes
        await Write(new MeshNode("Public", "ACME/Docs") { Name = "Public Doc", NodeType = "Document" });
        await Write(new MeshNode("Secret", "ACME/Secret") { Name = "Secret Doc", NodeType = "Document" });

        // Group: team contains eve
        await ac.AddGroupMemberAsync("team", "eve", ct).Run().Should().Within(30.Seconds()).Emit();

        // Allow team Read on ACME
        await ac.Grant("ACME", "team", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        // Deny eve specifically on ACME/Secret
        await ac.Grant("ACME/Secret", "eve", "Read", isAllow: false, ct).Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "eve");

        var results = await Query(query, request);

        // eve sees ACME/Docs/Public but NOT ACME/Secret/Secret (denied)
        results.Should().HaveCount(1);
        results.Cast<MeshNode>().Single().Path.Should().Be("ACME/Docs/Public");
    }

    /// <summary>
    /// 🔒 #953 — no node type is readable on the strength of its TYPE. The predicate used to OR in
    /// <c>EXISTS(node_type_permissions … public_read)</c>, which read a table nothing ever wrote;
    /// this pins the replacement invariant: with zero grants a reader sees nothing, whatever the
    /// node type. The types below are drawn from the ~24 that declared <c>WithPublicRead</c>.
    /// </summary>
    [Fact]
    public async Task WithoutGrants_NoNodeTypeIsVisible()
    {
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();

        await Write(new MeshNode("Space", "") { Name = "Space", NodeType = "NodeType" });
        await Write(new MeshNode("ACME", "") { Name = "ACME Corp", NodeType = "Space" });
        await Write(new MeshNode("Readme", "Private") { Name = "Readme", NodeType = "Markdown" });
        await Write(new MeshNode("Chat", "Private") { Name = "Chat", NodeType = "Thread" });
        await Write(new MeshNode("Lesson", "Private") { Name = "Lesson", NodeType = "Module" });

        // Query as an authenticated user with zero grants.
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("scope:descendants", "nobody");

        var results = await Query(query, request);

        results.Should().BeEmpty(
            "deny-by-default is the only floor — no node type carries an implicit read grant");
    }

    [Fact]
    public async Task PolicyCapsQuery_WriteDeniedByPolicy()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        // Seed nodes
        await Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });

        // Grant full access to alice at ACME
        await ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant("ACME", "alice", "Update", isAllow: true, ct).Should().Within(30.Seconds()).Emit();

        // Set read-only policy on ACME (deny Create, Update, Delete, Comment)
        await ac.SetPolicyAsync("ACME", create: false, update: false, delete: false, comment: false, ct: ct)
            .Run().Should().Within(30.Seconds()).Emit();

        // alice can still read (query sees Doc1 node)
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants nodeType:Document", "alice");
        var results = await Query(query, request);

        results.Should().HaveCount(1, "alice should still see Doc1 via Read permission");
        results.Cast<MeshNode>().Single().Path.Should().Be("ACME/Docs/Doc1");

        // But Update permission should be denied by the policy
        await ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Update", ct).Run()
            .Should().Within(30.Seconds()).Be(false);
        await ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Read", ct).Run()
            .Should().Within(30.Seconds()).Be(true);
    }

    [Fact]
    public async Task PolicyNodeVisibleButFilterableByNodeType()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        // Seed a regular node and a policy node
        await Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });
        await ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.SetPolicyAsync("ACME", create: false, update: false, delete: false, comment: false, ct: ct)
            .Run().Should().Within(30.Seconds()).Emit();

        // Unfiltered query includes the _Policy node at the SQL level
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants", "alice");
        var results = await Query(query, request);

        var nodeTypes = results.Cast<MeshNode>().Select(n => n.NodeType).ToList();
        nodeTypes.Should().Contain("PartitionAccessPolicy", "_Policy node is a regular mesh_node");
        nodeTypes.Should().Contain("Document");

        // Filtering by nodeType:Document excludes the _Policy node
        // (context-based ExcludeFromContext filtering is applied at the application layer)
        var filteredRequest = MeshQueryRequest.FromQuery("path:ACME scope:descendants nodeType:Document", "alice");
        var filteredResults = await Query(query, filteredRequest);

        filteredResults.Should().HaveCount(1);
        filteredResults.Cast<MeshNode>().Single().NodeType.Should().Be("Document");
    }

    [Fact]
    public async Task PolicyDeniesOnlyUpdate_ReadQueryStillWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        await Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });

        await ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant("ACME", "alice", "Update", isAllow: true, ct).Should().Within(30.Seconds()).Emit();

        // Deny only Update — Read should still work
        await ac.SetPolicyAsync("ACME", update: false, ct: ct).Run().Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants nodeType:Document", "alice");
        var results = await Query(query, request);

        results.Should().HaveCount(1, "alice should still see Doc1 via Read permission");
        await ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Read", ct).Run()
            .Should().Within(30.Seconds()).Be(true);
        await ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Update", ct).Run()
            .Should().Within(30.Seconds()).Be(false);
    }

    [Fact]
    public async Task PolicyDeniesRead_QueryReturnsNoResults()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        await Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });

        await ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant("ACME", "alice", "Update", isAllow: true, ct).Should().Within(30.Seconds()).Emit();

        // Deny Read — query should return nothing
        await ac.SetPolicyAsync("ACME", read: false, ct: ct).Run().Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants nodeType:Document", "alice");
        var results = await Query(query, request);

        results.Should().BeEmpty("Read denied by policy — alice cannot see any nodes");
    }

    [Fact]
    public async Task PerPermissionPolicy_GranularDenyPreservesOtherPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        await Write(new MeshNode("Doc1", "ACME/Docs") { Name = "Doc One", NodeType = "Document" });

        // Grant full access
        await ac.Grant("ACME", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant("ACME", "alice", "Create", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant("ACME", "alice", "Update", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant("ACME", "alice", "Delete", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant("ACME", "alice", "Comment", isAllow: true, ct).Should().Within(30.Seconds()).Emit();

        // Deny only Delete and Comment
        await ac.SetPolicyAsync("ACME", delete: false, comment: false, ct: ct).Run().Should().Within(30.Seconds()).Emit();

        await ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Read", ct).Run().Should().Within(30.Seconds()).Be(true);
        await ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Create", ct).Run().Should().Within(30.Seconds()).Be(true);
        await ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Update", ct).Run().Should().Within(30.Seconds()).Be(true);
        await ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Delete", ct).Run().Should().Within(30.Seconds()).Be(false);
        await ac.HasPermissionAsync("alice", "ACME/Docs/Doc1", "Comment", ct).Run().Should().Within(30.Seconds()).Be(false);
    }

    /// <summary>
    /// 🔒 #953 — the store/course paywall shape, which is what the removed node-type public-read
    /// term would have broken. A public surface everyone may read (<c>Store</c>, the projection of a
    /// <c>PartitionAccessPolicy { PublicRead = true }</c> <c>_Policy</c> — issue #603), gated content
    /// behind a DENY at a deeper prefix (<c>Store/Course1/Paid</c>), and one entitled buyer with an
    /// explicit allow at that prefix.
    ///
    /// <para><c>Course</c>, <c>Module</c>, <c>Exercise</c> and <c>Markdown</c> were all among the ~24
    /// types that declared <c>WithPublicRead</c>. A re-introduced <c>public_read OR …</c> term would
    /// short-circuit the longest-prefix fold and OR straight past the DENY, so every one of these
    /// negative assertions is a direct guard against re-granting paid content.</para>
    /// </summary>
    private async Task SeedPaywalledCourse()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var ac = _fixture.AccessControl;

        await Write(new MeshNode("Course1", "Store") { Name = "Course One", NodeType = "Course" });
        await Write(new MeshNode("Overview", "Store/Course1") { Name = "Overview", NodeType = "Markdown" });
        await Write(new MeshNode("Module1", "Store/Course1/Paid") { Name = "Module One", NodeType = "Module" });
        await Write(new MeshNode("Exercise1", "Store/Course1/Paid") { Name = "Exercise One", NodeType = "Exercise" });
        await Write(new MeshNode("Notes", "Store/Course1/Paid") { Name = "Notes", NodeType = "Markdown" });

        // The public surface — what a PublicRead _Policy at `Store` projects into
        // user_effective_permissions for the Public/Anonymous subjects.
        await ac.Grant("Store", "Public", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        // The paywall — a DENY at the deeper prefix; the per-subject longest-prefix fold makes it win.
        await ac.Grant("Store/Course1/Paid", "Public", "Read", isAllow: false, ct).Should().Within(30.Seconds()).Emit();
        // The entitlement — one explicit allow for the buyer at the gated prefix.
        await ac.Grant("Store/Course1/Paid", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
    }

    [Fact]
    public async Task PaywalledContent_StaysInvisibleToUnentitledReader()
    {
        await SeedPaywalledCourse();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var results = (await Query(query, MeshQueryRequest.FromQuery("scope:descendants", "bob")))
            .OfType<MeshNode>().Select(n => n.Path).ToList();

        results.Should().Contain("Store/Course1", "the course cover is the public surface");
        results.Should().Contain("Store/Course1/Overview", "the marketing page is the public surface");
        results.Should().NotContain("Store/Course1/Paid/Module1", "Module is paid content behind the deny");
        results.Should().NotContain("Store/Course1/Paid/Exercise1", "Exercise is paid content behind the deny");
        results.Should().NotContain("Store/Course1/Paid/Notes", "Markdown under the deny is paid content too");
    }

    [Fact]
    public async Task PaywalledContent_StaysInvisibleToAnonymous()
    {
        await SeedPaywalledCourse();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var results = (await Query(query, MeshQueryRequest.FromQuery("scope:descendants", WellKnownUsers.Anonymous)))
            .OfType<MeshNode>().Select(n => n.Path).ToList();

        results.Should().BeEmpty(
            "Anonymous does not inherit the Public baseline, so it sees neither the cover nor the paid content");
    }

    [Fact]
    public async Task PaywalledContent_VisibleToEntitledReader()
    {
        await SeedPaywalledCourse();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var results = (await Query(query, MeshQueryRequest.FromQuery("scope:descendants", "alice")))
            .OfType<MeshNode>().Select(n => n.Path).ToList();

        results.Should().Contain("Store/Course1/Paid/Module1", "alice bought the course");
        results.Should().Contain("Store/Course1/Paid/Exercise1");
        results.Should().Contain("Store/Course1/Paid/Notes");
    }

    /// <summary>
    /// 🔒 #953 — the anti-regression test. <c>node_type_permissions</c> still EXISTS for one release
    /// (a rolling deploy's older replicas still name it), so a populated row is exactly the state a
    /// naive "just wire the sync up" fix would produce. The predicate must ignore it completely.
    ///
    /// <para>Delete this test together with the table in the follow-up drop migration.</para>
    /// </summary>
    [Fact]
    public async Task PopulatedLegacyNodeTypePermissions_DoNotBypassThePaywallDeny()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPaywalledCourse();

        // Exactly what wiring the deleted SyncNodeTypePermissionsAsync back up would write.
        foreach (var nodeType in new[] { "Course", "Module", "Exercise", "Markdown" })
            await _fixture.DataSource.ExecuteNonQuery(
                $"INSERT INTO node_type_permissions (node_type, public_read) VALUES ('{nodeType}', true) "
                + "ON CONFLICT (node_type) DO UPDATE SET public_read = true", ct)
                .Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var results = (await Query(query, MeshQueryRequest.FromQuery("scope:descendants", "bob")))
            .OfType<MeshNode>().Select(n => n.Path).ToList();

        results.Should().NotContain("Store/Course1/Paid/Module1",
            "a public_read row must NOT override the paywall deny — that is the #953 breach");
        results.Should().NotContain("Store/Course1/Paid/Exercise1");
        results.Should().NotContain("Store/Course1/Paid/Notes");
    }
}
