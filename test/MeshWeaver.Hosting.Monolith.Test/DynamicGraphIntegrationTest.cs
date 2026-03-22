using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using Memex.Portal.Shared;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for dynamically configured Graph using real sample data.
/// Tests verify that the ACME/Project sample data, Organization type,
/// and built-in Markdown type work end-to-end with real messages.
/// </summary>
/// <remarks>
/// Uses AddPartitionedFileSystemPersistence with ACME sample data from samples/Graph/Data.
/// Node types used: Markdown (built-in), Organization (via AddOrganizationType), ACME/Project (from sample data).
/// </remarks>
[Collection("DynamicGraphIntegrationTests")]
public class DynamicGraphIntegrationTest : MonolithMeshTestBase
{
    private readonly string _cacheDirectory;
    // Unique suffix per test instance to avoid file system persistence collisions across runs
    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];

    public DynamicGraphIntegrationTest(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverDynamicGraphTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(TestPaths.SamplesGraphData)
            .AddOrganizationType()
            .AddAcme()
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas())
            .AddGraph()
            // Seed test hierarchy via AddMeshNodes (in-memory, not persisted to disk)
            .AddMeshNodes(
                new MeshNode(TestPartition) { Name = "Test Data", NodeType = "Markdown" },
                MeshNode.FromPath($"{TestPartition}/org1") with { Name = "Organization 1", NodeType = "Organization" },
                MeshNode.FromPath($"{TestPartition}/org2") with { Name = "Organization 2", NodeType = "Organization" },
                MeshNode.FromPath($"{TestPartition}/org1/proj1") with { Name = "Project 1", NodeType = "Markdown" },
                MeshNode.FromPath($"{TestPartition}/org1/proj2") with { Name = "Project 2", NodeType = "Markdown" },
                MeshNode.FromPath($"{TestPartition}/org1/proj1/item1") with { Name = "Item 1", NodeType = "Markdown" },
                MeshNode.FromPath($"{TestPartition}/org1/proj1/item2") with { Name = "Item 2", NodeType = "Markdown" }
            );
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_cacheDirectory))
            try { Directory.Delete(_cacheDirectory, recursive: true); } catch { }
    }

    #region Hub Initialization Tests

    [Fact(Timeout = 20000)]
    public async Task GraphHub_LoadsChildrenFromPersistence_AtInitialization()
    {
        var client = GetClient();
        var testDataAddress = new Address(TestPartition);

        // Initialize TestData hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(testDataAddress),
            TestContext.Current.CancellationToken);

        // Verify IMeshService finds the pre-seeded data
        var children = await MeshQuery.QueryAsync<MeshNode>($"namespace:{TestPartition}", null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCountGreaterThanOrEqualTo(2, "TestData should have at least 2 org children pre-seeded");
        children.Should().Contain(n => n.Path == $"{TestPartition}/org1");
        children.Should().Contain(n => n.Path == $"{TestPartition}/org2");
    }

    [Fact(Timeout = 20000)]
    public async Task OrgHub_LoadsChildrenFromPersistence_AtInitialization()
    {
        var client = GetClient();
        var testDataAddress = new Address(TestPartition);
        var orgAddress = new Address($"{TestPartition}/org1");

        // Initialize TestData hub first (required for routing to child hubs)
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(testDataAddress),
            TestContext.Current.CancellationToken);

        // Initialize org hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            TestContext.Current.CancellationToken);

        // Verify IMeshService finds the pre-seeded projects
        var children = await MeshQuery.QueryAsync<MeshNode>($"namespace:{TestPartition}/org1", null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCount(2, "org1 should have 2 project children pre-seeded");
        children.Should().Contain(n => n.Path == $"{TestPartition}/org1/proj1");
        children.Should().Contain(n => n.Path == $"{TestPartition}/org1/proj2");
    }

    [Fact(Timeout = 20000)]
    public async Task ProjectHub_LoadsChildrenFromPersistence_AtInitialization()
    {
        var client = GetClient();
        var testDataAddress = new Address(TestPartition);
        var projAddress = new Address($"{TestPartition}/org1/proj1");

        // Initialize TestData hub first (required for routing to child hubs)
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(testDataAddress),
            TestContext.Current.CancellationToken);

        // Initialize project hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(projAddress),
            TestContext.Current.CancellationToken);

        // Verify IMeshService finds the pre-seeded items
        var children = await MeshQuery.QueryAsync<MeshNode>($"namespace:{TestPartition}/org1/proj1", null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCount(2, "proj1 should have 2 item children pre-seeded");
        children.Should().Contain(n => n.Path == $"{TestPartition}/org1/proj1/item1");
        children.Should().Contain(n => n.Path == $"{TestPartition}/org1/proj1/item2");
    }

    #endregion

    #region ResolvePath Tests

    [Fact(Timeout = 20000)]
    public async Task ResolvePath_FindsPersistedNode_NotInConfig()
    {
        // Act
        var resolution = await PathResolver.ResolvePathAsync($"{TestPartition}/org1");

        // Assert
        resolution.Should().NotBeNull($"persistence has {TestPartition}/org1");
        resolution.Prefix.Should().Be($"{TestPartition}/org1");
        resolution.Remainder.Should().BeNull();
    }

    [Fact(Timeout = 20000)]
    public async Task ResolvePath_WalksUpHierarchy_FindsBestMatch()
    {
        // Act: resolve path that goes deeper than persisted (nonexistent/deep doesn't exist)
        var resolution = await PathResolver.ResolvePathAsync($"{TestPartition}/org1/proj1/nonexistent/deep");

        // Assert: should match TestData/org1/proj1 with remainder
        resolution.Should().NotBeNull();
        resolution.Prefix.Should().Be($"{TestPartition}/org1/proj1");
        resolution.Remainder.Should().Be("nonexistent/deep");
    }

    [Fact(Timeout = 20000)]
    public async Task ResolvePath_ReturnsExactMatch_WhenFullPathExists()
    {
        // Act
        var resolution = await PathResolver.ResolvePathAsync($"{TestPartition}/org1/proj1/item1");

        // Assert
        resolution.Should().NotBeNull();
        resolution.Prefix.Should().Be($"{TestPartition}/org1/proj1/item1");
        resolution.Remainder.Should().BeNull();
    }

    [Fact(Timeout = 20000)]
    public async Task ResolvePath_WithRemainder_ReturnsCorrectParts()
    {
        // Act: resolve path with additional segments beyond existing node
        var resolution = await PathResolver.ResolvePathAsync($"{TestPartition}/org1/proj1/item1/Overview");

        // Assert
        resolution.Should().NotBeNull();
        resolution.Prefix.Should().Be($"{TestPartition}/org1/proj1/item1");
        resolution.Remainder.Should().Be("Overview");
    }

    [Fact(Timeout = 20000)]
    public async Task ResolvePath_ReturnsNull_WhenNoMatchFound()
    {
        // Act: resolve path that doesn't exist anywhere
        var resolution = await PathResolver.ResolvePathAsync("nonexistent/path/here");

        // Assert
        resolution.Should().BeNull();
    }

    [Fact(Timeout = 20000)]
    public async Task ResolvePath_UnderscoreNamespaceedSegment_ParsesAsRemainder()
    {
        // Act: resolve path with underscore-prefixed segment (layout area)
        var resolution = await PathResolver.ResolvePathAsync($"{TestPartition}/_Nodes");

        // Assert: TestPartition is the address, "_Nodes" is the remainder (layout area)
        resolution.Should().NotBeNull();
        resolution.Prefix.Should().Be(TestPartition);
        resolution.Remainder.Should().Be("_Nodes");
    }

    #endregion

    #region Type Node Navigation Tests

    /// <summary>
    /// Navigating to Organization should resolve to the Organization node type.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ResolvePath_Organization_ResolvesToOrganizationNode()
    {
        // Arrange
        var pathResolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

        // Act
        var resolution = await pathResolver.ResolvePathAsync("Organization");

        // Assert
        resolution.Should().NotBeNull("Organization should be resolvable");
        resolution.Prefix.Should().Be("Organization");
        resolution.Remainder.Should().BeNull();
    }

    /// <summary>
    /// GetNodeAsync for Organization should return the NodeType definition node.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task GetNodeAsync_Organization_ReturnsNodeTypeDefinition()
    {
        // Act
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Organization", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        // Assert
        node.Should().NotBeNull("Organization node should exist");
        node!.Path.Should().Be("Organization");
        node.NodeType.Should().Be("NodeType");
        node.Content.Should().BeOfType<NodeTypeDefinition>();
    }

    /// <summary>
    /// Navigating to type paths should work for real type definitions.
    /// </summary>
    [Theory]
    [InlineData("Organization")]
    [InlineData("ACME/Project")]
    [InlineData("ACME/Project/Todo")]
    public async Task ResolvePath_TypePaths_ResolveCorrectly(string typePath)
    {
        // Arrange
        var pathResolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

        // Act
        var resolution = await pathResolver.ResolvePathAsync(typePath);

        // Assert
        resolution.Should().NotBeNull($"{typePath} should be resolvable");
        resolution.Prefix.Should().Be(typePath);
        resolution.Remainder.Should().BeNull();
    }

    /// <summary>
    /// Type nodes should exist in persistence and be retrievable.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task TypeNodes_ExistInPersistence()
    {
        // Assert that type nodes exist
        var orgType = await MeshQuery.QueryAsync<MeshNode>("path:Organization", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        orgType.Should().NotBeNull("Organization should exist in persistence");
        orgType!.NodeType.Should().Be("NodeType");

        var projectType = await MeshQuery.QueryAsync<MeshNode>("path:ACME/Project", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        projectType.Should().NotBeNull("ACME/Project should exist in persistence");
        projectType!.NodeType.Should().Be("NodeType");
    }

    #endregion

    #region MoveNodeAsync Tests

    /// <summary>
    /// Move single node to new path.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task MoveNodeAsync_MovesNodeToNewPath()
    {
        // Arrange - create a node to move (unique per run to avoid file system collisions)
        var src = $"{TestPartition}/movetest-{_uid}";
        var dst = $"{TestPartition}/movetest-renamed-{_uid}";
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(src) with { Name = "Move Test", NodeType = "Markdown" }, ct: TestContext.Current.CancellationToken);

        // Act
        var response = await Mesh.AwaitResponse<MoveNodeResponse>(new MoveNodeRequest(src, dst), o => o, TestContext.Current.CancellationToken);
        var moved = response.Message.Node;

        // Assert
        moved.Should().NotBeNull();
        moved!.Path.Should().Be(dst);
        moved.Name.Should().Be("Move Test");

        var oldNode = await MeshQuery.QueryAsync<MeshNode>($"path:{src}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        oldNode.Should().BeNull("Original node should be deleted");

        var newNode = await MeshQuery.QueryAsync<MeshNode>($"path:{dst}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        newNode.Should().NotBeNull("Node should exist at new path");
        newNode!.Name.Should().Be("Move Test");
    }

    /// <summary>
    /// Move node with descendants - all paths should be updated.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task MoveNodeAsync_MovesDescendantsWithUpdatedPaths()
    {
        // Arrange - create a hierarchy to move (unique per run)
        var parent = $"{TestPartition}/parent-{_uid}";
        var newParentPath = $"{TestPartition}/newparent-{_uid}";
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(parent) with { Name = "Parent", NodeType = "Markdown" }, ct: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{parent}/child1") with { Name = "Child 1", NodeType = "Markdown" }, ct: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{parent}/child2") with { Name = "Child 2", NodeType = "Markdown" }, ct: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{parent}/child1/grandchild") with { Name = "Grandchild", NodeType = "Markdown" }, ct: TestContext.Current.CancellationToken);

        // Act
        await Mesh.AwaitResponse<MoveNodeResponse>(new MoveNodeRequest(parent, newParentPath), o => o, TestContext.Current.CancellationToken);

        // Assert - old paths should not exist
        (await MeshQuery.QueryAsync<MeshNode>($"path:{parent}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        (await MeshQuery.QueryAsync<MeshNode>($"path:{parent}/child1", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        (await MeshQuery.QueryAsync<MeshNode>($"path:{parent}/child2", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        (await MeshQuery.QueryAsync<MeshNode>($"path:{parent}/child1/grandchild", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken)).Should().BeNull();

        // Assert - new paths should exist with correct data
        var newParent = await MeshQuery.QueryAsync<MeshNode>($"path:{newParentPath}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        newParent.Should().NotBeNull();
        newParent!.Name.Should().Be("Parent");

        var newChild1 = await MeshQuery.QueryAsync<MeshNode>($"path:{newParentPath}/child1", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        newChild1.Should().NotBeNull();
        newChild1!.Name.Should().Be("Child 1");

        var newChild2 = await MeshQuery.QueryAsync<MeshNode>($"path:{newParentPath}/child2", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        newChild2.Should().NotBeNull();
        newChild2!.Name.Should().Be("Child 2");

        var newGrandchild = await MeshQuery.QueryAsync<MeshNode>($"path:{newParentPath}/child1/grandchild", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        newGrandchild.Should().NotBeNull();
        newGrandchild!.Name.Should().Be("Grandchild");
    }

    /// <summary>
    /// Move node via MoveNodeRequest - verifies the node is moved to the new path.
    /// Note: Comment migration is handled internally by the persistence layer.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task MoveNodeAsync_MovesNodeViaRequest()
    {
        // Arrange - create node (unique per run)
        var src = $"{TestPartition}/commented-{_uid}";
        var dst = $"{TestPartition}/commented-moved-{_uid}";
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(src) with { Name = "Commented Node", NodeType = "Markdown" }, ct: TestContext.Current.CancellationToken);

        // Act - move via MoveNodeRequest
        var response = await Mesh.AwaitResponse<MoveNodeResponse>(new MoveNodeRequest(src, dst), o => o, TestContext.Current.CancellationToken);

        // Assert - node should be at new path
        response.Message.Success.Should().BeTrue("Move should succeed");
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.Path.Should().Be(dst);

        var movedNode = await MeshQuery.QueryAsync<MeshNode>($"path:{dst}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        movedNode.Should().NotBeNull("Node should exist at new path");

        var oldNode = await MeshQuery.QueryAsync<MeshNode>($"path:{src}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        oldNode.Should().BeNull("Node should not remain at old path");
    }

    /// <summary>
    /// Move node throws when source doesn't exist.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task MoveNodeAsync_ThrowsWhenSourceNotFound()
    {
        // Act - move via MoveNodeRequest (unique names to avoid filesystem collisions)
        var response = await Mesh.AwaitResponse<MoveNodeResponse>(new MoveNodeRequest($"{TestPartition}/nonexistent-{_uid}", $"{TestPartition}/newpath-{_uid}"), o => o, TestContext.Current.CancellationToken);

        // Assert - should fail with source not found
        response.Message.Success.Should().BeFalse("Move should fail when source doesn't exist");
        response.Message.Error.Should().Contain("not found");
    }

    /// <summary>
    /// Move node throws when target path already exists.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task MoveNodeAsync_ThrowsWhenTargetExists()
    {
        // Arrange (unique per run)
        var src = $"{TestPartition}/source-{_uid}";
        var dst = $"{TestPartition}/target-{_uid}";
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(src) with { Name = "Source", NodeType = "Markdown" }, ct: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(dst) with { Name = "Target", NodeType = "Markdown" }, ct: TestContext.Current.CancellationToken);

        // Act - move via MoveNodeRequest
        var response = await Mesh.AwaitResponse<MoveNodeResponse>(new MoveNodeRequest(src, dst), o => o, TestContext.Current.CancellationToken);

        // Assert - should fail because target already exists
        response.Message.Success.Should().BeFalse("Move should fail when target already exists");
        response.Message.Error.Should().Contain("already exists");
    }

    #endregion

    #region Default Layout Area Tests

    /// <summary>
    /// Tests that requesting the default layout area for an Organization node
    /// returns successfully without hanging.
    /// This validates that default views are properly configured for Organization type.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Organization_GetDefaultLayoutArea_DoesNotHang()
    {
        var orgAddress = new Address($"{TestPartition}/org1");

        // Get a client with data services configured
        var client = GetClient(c => c.AddData(data => data));

        // Initialize org hub - this should also set up default views
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            TestContext.Current.CancellationToken);

        // Act: Request the default layout area (Overview) using stream
        // This should not hang if default views are properly configured
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(orgAddress, reference);

        // Wait for the stream to emit a value (with timeout from test attribute)
        var value = await stream.Timeout(10.Seconds()).FirstAsync();

        // Assert
        value.Should().NotBe(default(JsonElement), "Default layout area should return content");
    }

    /// <summary>
    /// Tests that requesting an empty area (default view) for an Organization node works.
    /// When area is empty/null, the default view should be returned (Details).
    /// This matches the pattern used in LayoutTest.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Organization_GetEmptyArea_ReturnsDefaultView()
    {
        var orgAddress = new Address($"{TestPartition}/org1");

        // Get a client with data services configured
        var client = GetClient(c => c.AddData(data => data));

        // Initialize org hub - this should also set up default views
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            TestContext.Current.CancellationToken);

        // Act: Request empty area - should return default view (Details)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(string.Empty);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(orgAddress, reference);

        // Wait for the stream to emit a value
        var value = await stream.Timeout(10.Seconds()).FirstAsync();

        // Assert
        value.Should().NotBe(default(JsonElement), "Empty area should return default view content");
    }

    /// <summary>
    /// Tests that the Organization NodeType catalog renders correctly.
    /// When navigating to Organization and requesting the Search area,
    /// it should render a StackControl with Search that contains either
    /// organization thumbnails or "No items found" message.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task OrganizationType_GetCatalog_ShowsOrganizations()
    {
        // Arrange
        var typeOrgAddress = new Address("Organization");

        // Get a client with data services configured
        var client = GetClient(c => c.AddData(data => data));

        // Initialize Organization hub - this is a NodeType node
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(typeOrgAddress),
            TestContext.Current.CancellationToken);

        // Act: Request Search area directly (the default view for NodeType)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.SearchArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(typeOrgAddress, reference);

        // Wait for an emission that contains the expected search structure
        var values = await stream
            .Take(5)  // Take up to 5 emissions
            .TakeUntil(Observable.Timer(TimeSpan.FromSeconds(3)))  // Or timeout after 3s
            .ToList();

        Output.WriteLine($"Received {values.Count} emissions");

        // Find the last emission which should have the most complete data
        var lastValue = values.LastOrDefault();
        lastValue.Should().NotBeNull("Should receive at least one emission");

        // Convert to string to check catalog structure
        var json = lastValue!.Value.GetRawText();
        Output.WriteLine($"Last Catalog JSON (first 3000 chars): {json.Substring(0, Math.Min(3000, json.Length))}");

        // Log all emissions for debugging
        for (int i = 0; i < values.Count; i++)
        {
            var emissionJson = values[i].Value.GetRawText();
            Output.WriteLine($"Emission {i}: {emissionJson.Substring(0, Math.Min(500, emissionJson.Length))}...");
        }

        // The search should render as a MeshSearchControl
        var hasSearchStructure = json.Contains("Search") && json.Contains("MeshSearchControl");
        hasSearchStructure.Should().BeTrue($"Search should have MeshSearchControl. JSON: {json.Substring(0, Math.Min(1000, json.Length))}");

        // The MeshSearchControl should have the correct namespace and scope
        var hasCorrectQuery = json.Contains("namespace:Organization");
        hasCorrectQuery.Should().BeTrue($"Search should have namespace filter in query. JSON: {json.Substring(0, Math.Min(1000, json.Length))}");
    }

    /// <summary>
    /// Tests that QueryAsync with nodeType filter returns organizations.
    /// This tests the underlying query that the search uses.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task QueryAsync_NodeTypeOrg_ReturnsOrganizations()
    {
        // Act - query for all nodes with nodeType Organization
        var query = "nodeType:Organization scope:descendants";
        var nodes = await MeshQuery.QueryAsync<MeshNode>(query, null, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        foreach (var node in nodes)
            Output.WriteLine($"Found: {node.Path}");

        // Assert
        nodes.Should().NotBeEmpty("Query should return organizations");
        nodes.Should().Contain(n => n.Path == $"{TestPartition}/org1", "Should find org1");
        nodes.Should().Contain(n => n.Path == $"{TestPartition}/org2", "Should find org2");
    }

    #endregion

    #region Code Node Sibling Query Tests

    /// <summary>
    /// Verifies the parent path derivation logic used by CodeLayoutAreas.Overview.
    /// For a code node at "ACME/Project/_Source/code", the parent NodeType path should be "ACME/Project".
    /// </summary>
    [Theory]
    [InlineData("ACME/Project/_Source/code", "ACME/Project")]
    [InlineData("Organization/_Source/Organization", "Organization")]
    [InlineData("a/b/_Source/c", "a/b")]
    public void CodeNode_ParentPathParsing_StripsTwoSegments(string codePath, string expectedParent)
    {
        var segments = codePath.Split('/');
        var parentPath = segments.Length >= 3
            ? string.Join("/", segments.Take(segments.Length - 2))
            : codePath;

        parentPath.Should().Be(expectedParent,
            $"Stripping last 2 segments from '{codePath}' should yield the NodeType parent path");
    }

    /// <summary>
    /// Verifies that IMeshService with scope:descendants finds Code nodes that are 2 levels deep.
    /// Code nodes at "ACME/Project/_Source/code" are NOT immediate children of "ACME/Project" (they're
    /// grandchildren), so namespace: would miss them. scope:descendants is required.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task QueryAsync_ScopeDescendants_FindsCodeNodesUnderNodeType()
    {
        // Act: Query for Code nodes under ACME/Project using scope:descendants
        var query = "path:ACME/Project nodeType:Code scope:descendants";
        var nodes = await MeshQuery.QueryAsync<MeshNode>(query, null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var node in nodes)
            Output.WriteLine($"Found Code node: {node.Path} (NodeType={node.NodeType})");

        // Assert: should find the code nodes from ACME/Project/_Source/
        nodes.Should().NotBeEmpty("scope:descendants should find Code nodes 2 levels deep");
        nodes.Should().OnlyContain(n => n.NodeType == "Code", "All results should be Code nodes");
    }

    /// <summary>
    /// Verifies that namespace: does NOT find Code nodes (they are 2 levels deep).
    /// This confirms the bug that was fixed by switching to scope:descendants.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task QueryAsync_ScopeChildren_DoesNotFindCodeNodes()
    {
        // Act: Query for Code nodes under ACME/Project using namespace: (1 level deep only)
        var query = "namespace:ACME/Project nodeType:Code";
        var nodes = await MeshQuery.QueryAsync<MeshNode>(query, null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var node in nodes)
            Output.WriteLine($"Found with namespace: {node.Path}");

        // Assert: namespace: only checks 1 level deep — Code nodes are at depth 2 (ACME/Project/_Source/id)
        nodes.Should().BeEmpty("namespace: only finds immediate children; Code nodes are 2 levels deep");
    }

    /// <summary>
    /// Verifies that querying for all Code nodes under ACME/Project finds them.
    /// This is the same query pattern used by both CodeLayoutAreas.Overview (for siblings)
    /// and NodeTypeLayoutAreas.Overview (for code list in left menu).
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task QueryAsync_AcmeProject_HasCodeDescendants()
    {
        // Act
        var query = "path:ACME/Project nodeType:Code scope:descendants";
        var nodes = await MeshQuery.QueryAsync<MeshNode>(query, null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var node in nodes)
            Output.WriteLine($"ACME/Project -> Code node: {node.Path}");

        // Assert
        nodes.Should().NotBeEmpty("ACME/Project should have Code descendants from _Source/ directory");
        nodes.Should().OnlyContain(n => n.NodeType == "Code");
        nodes.Should().OnlyContain(n => n.Content is CodeConfiguration,
            "Code node Content should be CodeConfiguration");
    }

    #endregion
}

[CollectionDefinition("OrganizationsLayoutTests", DisableParallelization = true)]
public class OrganizationsLayoutTestsCollection { }

/// <summary>
/// Tests that use FileSystemPersistenceService to validate JSON deserialization with $type discriminator.
/// This replicates the exact production scenario where nodes and partition objects are read from disk.
/// </summary>
[Collection("DynamicGraphFileSystemPersistenceTests")]
public class DynamicGraphFileSystemPersistenceTest : MonolithMeshTestBase
{
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverFileSystemTests");
    private string? _testDirectory;

    private string GetOrCreateTestDirectory()
    {
        if (_testDirectory == null)
        {
            _testDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);
        }
        return _testDirectory;
    }

    public DynamicGraphFileSystemPersistenceTest(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>
    /// Creates the same structure as samples/Graph/Data on disk with actual JSON files.
    /// This tests the real FileSystemPersistenceService path with JSON deserialization.
    /// </summary>
    private void SetupOrganizationsStructureOnDisk(string dataDirectory)
    {
        // 1. Create Type/Organizations.json - the NodeType definition
        var typeDir = Path.Combine(dataDirectory, "Type");
        Directory.CreateDirectory(typeDir);

        var organizationsTypeJson = """
        {
          "id": "Organizations",
          "namespace": "Type",
          "name": "Organizations",
          "nodeType": "NodeType",
          "description": "Catalog of organizations",
          "iconName": "Building",
          "order": 8,
          "isPersistent": true,
          "content": {
            "$type": "NodeTypeDefinition",
            "id": "Organizations",
            "namespace": "Type",
            "displayName": "Organizations",
            "iconName": "Building",
            "description": "Catalog of organizations",
            "order": 8
          }
        }
        """;
        File.WriteAllText(Path.Combine(typeDir, "Organizations.json"), organizationsTypeJson);

        // 2. Create Type/Organizations/_Source/codeConfiguration.json - Code as child MeshNode
        var organizationsTypeDir = Path.Combine(typeDir, "Organizations");
        var codeDir = Path.Combine(organizationsTypeDir, "_Source");
        Directory.CreateDirectory(codeDir);

        var codeConfigJson = """
        {
          "id": "codeConfiguration",
          "namespace": "Type/Organizations/_Source",
          "name": "Code",
          "nodeType": "Code",
          "content": {
            "$type": "CodeConfiguration",
            "code": "public record Organizations { }"
          }
        }
        """;
        File.WriteAllText(Path.Combine(codeDir, "codeConfiguration.json"), codeConfigJson);

        // 3. Create Organizations.json - the instance node in root namespace
        var organizationsInstanceJson = """
        {
          "id": "Organizations",
          "name": "Organizations",
          "nodeType": "Type/Organizations",
          "description": "Catalog of organizations",
          "iconName": "Building",
          "order": 10,
          "isPersistent": true,
          "content": {}
        }
        """;
        File.WriteAllText(Path.Combine(dataDirectory, "Organizations.json"), organizationsInstanceJson);

        // 4. Create graph.json - the root node
        var graphJson = """
        {
          "id": "graph",
          "name": "Graph",
          "nodeType": "type/graph",
          "isPersistent": true
        }
        """;
        File.WriteAllText(Path.Combine(dataDirectory, "graph.json"), graphJson);

        // 5. Create type/graph - type definition for graph
        var typeGraphDir = Path.Combine(dataDirectory, "type");
        Directory.CreateDirectory(typeGraphDir);

        var graphTypeJson = """
        {
          "id": "graph",
          "namespace": "type",
          "name": "Graph",
          "nodeType": "NodeType",
          "isPersistent": true,
          "content": {
            "$type": "NodeTypeDefinition",
            "id": "graph",
            "namespace": "type",
            "displayName": "Graph",
            "configuration": "config => config"
          }
        }
        """;
        File.WriteAllText(Path.Combine(typeGraphDir, "graph.json"), graphTypeJson);

        var graphCodeDir = Path.Combine(typeGraphDir, "graph", "_Source");
        Directory.CreateDirectory(graphCodeDir);

        var graphCodeConfigJson = """
        {
          "id": "codeConfiguration",
          "namespace": "type/graph/_Source",
          "name": "Code",
          "nodeType": "Code",
          "content": {
            "$type": "CodeConfiguration",
            "code": "public record Graph { }"
          }
        }
        """;
        File.WriteAllText(Path.Combine(graphCodeDir, "codeConfiguration.json"), graphCodeConfigJson);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();

        // Create actual JSON files on disk - this is the key difference from InMemoryPersistenceService tests
        SetupOrganizationsStructureOnDisk(testDataDirectory);

        var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(testDataDirectory)
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory))
            .AddGraph();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (_testDirectory != null && Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Tests that NodeTypeService can find the NodeType node when reading from disk.
    /// The node.Content must be deserialized as NodeTypeDefinition (not JsonElement)
    /// for the check `node.Content is NodeTypeDefinition` to succeed.
    ///
    /// This validates that:
    /// 1. ITypeRegistry is available at mesh level
    /// 2. ObjectPolymorphicConverter is properly added to FileSystemStorageAdapter
    /// 3. $type discriminator is respected during JSON deserialization
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task FileSystem_PersistenceService_FindsNodeTypeNode_WithPolymorphicDeserialization()
    {
        // Act - this should find Type/Organizations by reading from disk
        var nodeTypeNode = await MeshQuery.QueryAsync<MeshNode>("path:Type/Organizations", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        // Assert
        nodeTypeNode.Should().NotBeNull(
            "PersistenceService should find the NodeType node from disk. " +
            "If null, the Content property was likely deserialized as JsonElement instead of NodeTypeDefinition.");
        nodeTypeNode!.Path.Should().Be("Type/Organizations");
        nodeTypeNode.NodeType.Should().Be("NodeType");

        // Critical: Content must be NodeTypeDefinition, not JsonElement
        nodeTypeNode.Content.Should().BeOfType<NodeTypeDefinition>(
            "The $type discriminator in the JSON should cause Content to be deserialized as NodeTypeDefinition. " +
            "If this fails, ITypeRegistry is not properly configured for FileSystemStorageAdapter.");
    }

    /// <summary>
    /// Tests that CodeConfiguration can be loaded from child MeshNodes under the Code path.
    /// Code is now stored as regular MeshNodes with nodeType="Code" and content=CodeConfiguration.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task FileSystem_CodeConfiguration_LoadedFromChildMeshNodes()
    {
        // Act - get children of the Code path
        var codeChildren = await MeshQuery.QueryAsync<MeshNode>("namespace:Type/Organizations/_Source", ct: TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        codeChildren.Should().NotBeEmpty("Code path should have child MeshNodes with CodeConfiguration");
        var codeNode = codeChildren.First();
        codeNode.NodeType.Should().Be("Code");
        codeNode.Content.Should().BeOfType<CodeConfiguration>(
            "Code child node Content should be deserialized as CodeConfiguration via $type discriminator");
        var codeConfig = (CodeConfiguration)codeNode.Content!;
        codeConfig.Code.Should().NotBeNullOrEmpty(
            "CodeConfiguration.Code should contain C# source code.");
    }

    /// <summary>
    /// Tests the complete flow: node loading, type compilation, and HubConfiguration setting.
    /// This is the end-to-end test for the production scenario.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task FileSystem_Organizations_GetsHubConfiguration_FromCompiledAssembly()
    {
        // Act - get the Organizations node (triggers on-demand compilation from disk files)
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Organizations", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        // Assert
        node.Should().NotBeNull("Organizations node should exist on disk");

        // Enrich via NodeTypeService to trigger compilation and populate HubConfiguration
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();
        node = await nodeTypeService.EnrichWithNodeTypeAsync(node!, TestContext.Current.CancellationToken);

        node.HubConfiguration.Should().NotBeNull(
            "Organizations node should have HubConfiguration from the compiled assembly. " +
            "If null, the on-demand compilation failed - likely because NodeTypeDefinition or CodeConfiguration " +
            "were not properly deserialized from JSON (returned as JsonElement instead).");
    }
}

[CollectionDefinition("DynamicGraphFileSystemPersistenceTests", DisableParallelization = true)]
public class DynamicGraphFileSystemPersistenceTestsCollection { }

/// <summary>
/// Collection definition for DynamicGraphIntegrationTests.
/// Ensures tests in this collection run serially to avoid test isolation issues.
/// </summary>
[CollectionDefinition("DynamicGraphIntegrationTests", DisableParallelization = true)]
public class DynamicGraphIntegrationTestsCollection
{
}

/// <summary>
/// Tests that use ACME/Project sample data to verify dynamic type compilation and querying.
/// </summary>
[Collection("SamplesGraphData")]
public class SamplesGraphDataTest : MonolithMeshTestBase
{
    private readonly string _cacheDirectory;
    private const string ProjectNodeTypePath = "ACME/Project";
    private const string TodoNodeTypePath = "ACME/Project/Todo";

    public SamplesGraphDataTest(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverSamplesTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(TestPaths.SamplesGraphData)
            .AddOrganizationType()
            .AddAcme()
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas())
            .AddGraph();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_cacheDirectory))
            try { Directory.Delete(_cacheDirectory, recursive: true); } catch { }
    }

    [Fact(Timeout = 20000)]
    public async Task Project_CanBeResolved()
    {
        var pathResolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = await pathResolver.ResolvePathAsync(ProjectNodeTypePath);
        resolution.Should().NotBeNull($"{ProjectNodeTypePath} should be resolvable");
        Output.WriteLine($"Resolved: Prefix={resolution!.Prefix}, Remainder={resolution.Remainder}");
    }

    [Fact(Timeout = 20000)]
    public async Task Project_QueryAsync_ScopeDescendants_FindsCodeNodes()
    {
        var queryString = $"namespace:{ProjectNodeTypePath}/_Source nodeType:Code";
        var results = await MeshQuery.QueryAsync<MeshNode>(queryString)
            .ToListAsync(TestContext.Current.CancellationToken);
        Output.WriteLine($"Query '{queryString}' returned {results.Count} results");
        foreach (var r in results)
            Output.WriteLine($"  {r.Path} ({r.NodeType})");
        results.Should().NotBeEmpty("Project should have Code nodes under _Source");
    }

    [Fact(Timeout = 20000)]
    public async Task Todo_QueryAsync_FindsCodeNodes()
    {
        var queryString = $"namespace:{TodoNodeTypePath}/_Source nodeType:Code";
        var results = await MeshQuery.QueryAsync<MeshNode>(queryString)
            .ToListAsync(TestContext.Current.CancellationToken);
        Output.WriteLine($"Query '{queryString}' returned {results.Count} results");
        foreach (var r in results)
            Output.WriteLine($"  {r.Path} ({r.NodeType})");
        results.Should().NotBeEmpty("Todo should have Code nodes under _Source");
    }

    [Fact(Timeout = 20000)]
    public async Task Project_PingRequest_ShouldNotDeadlock()
    {
        var client = GetClient();
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(new Address(ProjectNodeTypePath)),
            TestContext.Current.CancellationToken);
        response.Should().NotBeNull();
    }
}

[CollectionDefinition("SamplesGraphData", DisableParallelization = true)]
public class SamplesGraphDataTestsCollection { }
