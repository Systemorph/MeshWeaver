using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class QueryAsyncIntegrationTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{

    [Fact]
    public async Task QueryAsync_FilterByProperty_ReturnsMatchingNodes()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products nodeType:Electronics scope:descendants";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Phone"]);
    }

    [Fact]
    public async Task QueryAsync_FilterWithTextSearch_ReturnsFuzzyMatches()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Gaming Laptop Pro" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/desktop") with { Name = "Desktop Computer" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products laptop scope:descendants";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(1);
        var node = results.First() as MeshNode;
        node!.Name.Should().Be("Gaming Laptop Pro");
    }

    [Fact]
    public async Task QueryAsync_CombinedFilterAndSearch_ReturnsMatchingResults()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop1") with { Name = "Gaming Laptop", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop2") with { Name = "Business Laptop", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Gaming Chair", NodeType = "Furniture" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products nodeType:Electronics gaming scope:descendants";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(1);
        var node = results.First() as MeshNode;
        node!.Name.Should().Be("Gaming Laptop");
    }

    [Fact]
    public async Task QueryAsync_ScopeDescendants_SearchesAllChildren()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(new MeshNode("org") { Name = "Organization", NodeType = "container" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("other/company") with { Name = "Other Company", NodeType = "company" });

        // Act
        var query = "path:org nodeType:company scope:descendants";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(1);
        var node = results.First() as MeshNode;
        node!.Name.Should().Be("Acme Corp");
    }

    [Fact]
    public async Task QueryAsync_ScopeAncestors_SearchesParentPaths()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(new MeshNode("org") { Name = "Organization Root", NodeType = "root" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });

        // Act
        var query = "path:org/acme/project nodeType:root scope:ancestors";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(1);
        var node = results.First() as MeshNode;
        node!.Name.Should().Be("Organization Root");
    }

    [Fact]
    public async Task QueryAsync_InOperator_MatchesMultipleValues()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/food") with { Name = "Food", NodeType = "Groceries" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products nodeType:(Electronics OR Furniture) scope:descendants";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(3);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Phone", "Chair"]);
    }

    [Fact]
    public async Task QueryAsync_LikeOperator_MatchesWildcard()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop-pro") with { Name = "Laptop Pro", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop-basic") with { Name = "Laptop Basic", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/desktop") with { Name = "Desktop Computer", NodeType = "Electronics" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products name:*Laptop* scope:descendants";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop Pro", "Laptop Basic"]);
    }

    [Fact]
    public async Task QueryAsync_OrLogic_MatchesEitherCondition()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/food") with { Name = "Food", NodeType = "Groceries" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products (nodeType:Electronics OR nodeType:Furniture) scope:descendants";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Chair"]);
    }

    [Fact]
    public async Task QueryAsync_EmptyQuery_ReturnsAllAtPath()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("other/chair") with { Name = "Chair" });

        // Act - Empty query with path should return the node at the exact path only
        var query = "path:products";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert - products node doesn't exist, so empty result for exact path
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_NotEqualOperator_ExcludesMatches()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });

        // Act
        var query = "path:products -nodeType:Electronics scope:descendants";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(1);
        var node = results.First() as MeshNode;
        node!.Name.Should().Be("Chair");
    }

    #region Namespace Query Tests

    [Fact]
    public async Task QueryAsync_NamespaceWithoutScope_SearchesImmediateChildrenOnly()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/beta") with { Name = "Beta Inc", NodeType = "company" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("other/company") with { Name = "Other Company", NodeType = "company" });

        // Act - namespace:org without scope defaults to children (immediate only, not recursive)
        var query = "namespace:org";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert - should find only immediate children under org (acme, beta), not nested (project) or other
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Acme Corp", "Beta Inc"]);
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Project X");
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Other Company");
    }

    [Fact]
    public async Task QueryAsync_NamespaceWithDescendants_SearchesRecursively()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme/project/task") with { Name = "Task A", NodeType = "task" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("other/company") with { Name = "Other Company", NodeType = "company" });

        // Act - namespace:org with scope:descendants should find all nested nodes
        var query = "namespace:org scope:descendants";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert - should find all nested nodes under org, but not other
        results.Should().HaveCount(3);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Acme Corp", "Project X", "Task A"]);
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Other Company");
    }

    [Fact]
    public async Task QueryAsync_NamespaceWithFilter_SearchesImmediateChildrenWithFilter()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/beta") with { Name = "Beta Inc", NodeType = "company" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/project") with { Name = "Org Project", NodeType = "project" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme/child") with { Name = "Acme Child", NodeType = "company" });

        // Act - namespace:org with filter searches immediate children only and applies filter
        var query = "namespace:org nodeType:company";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert - should find only immediate children that match filter (acme, beta), not nested (child)
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Acme Corp", "Beta Inc"]);
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Acme Child");
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Org Project");
    }

    [Fact]
    public async Task QueryAsync_ScopeChildren_SearchesImmediateChildrenOnly()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(new MeshNode("products") { Name = "Products", NodeType = "container" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("products/laptop/accessories") with { Name = "Accessories", NodeType = "Electronics" });

        // Act - path:products with scope:children should find immediate children only
        var query = "path:products scope:children";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert - should find laptop and phone, not accessories (nested) or products itself
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Phone"]);
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Accessories");
    }

    [Fact]
    public async Task QueryAsync_NamespaceWithScopeChildren_LimitsToImmediateChildren()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/beta") with { Name = "Beta Inc", NodeType = "company" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });

        // Act - namespace:org with scope:children limits to immediate children
        var query = "namespace:org scope:children";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert - should find only immediate children (acme, beta), not nested (project)
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Acme Corp", "Beta Inc"]);
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Project X");
    }

    #endregion

    #region Hierarchy Scope Tests (for Agent Discovery)

    /// <summary>
    /// Tests the scenario from samples/Graph/Data:
    /// - ACME/Project is a NodeType
    /// - ACME/Project/TodoAgent is an Agent defined under that NodeType
    /// - ACME/ProductLaunch has nodeType="ACME/Project"
    ///
    /// When querying for agents at ACME/ProductLaunch, we should:
    /// 1. Get the node's NodeType (ACME/Project)
    /// 2. Query for agents with scope:hierarchy (ancestors + self + descendants)
    /// 3. Find TodoAgent
    /// </summary>
    [Fact]
    public async Task QueryAsync_ScopeHierarchy_FindsAgentUnderNodeType()
    {
        // Arrange - Set up the sample data structure
        // NodeType definition at ACME/Project
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project") with
        {
            Name = "Project",
            NodeType = "NodeType"
        });

        // Agent defined as a child of the NodeType
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/TodoAgent") with
        {
            Name = "Project Task Agent",
            NodeType = "Agent"
        });

        // Instance of the NodeType
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/ProductLaunch") with
        {
            Name = "MeshFlow Product Launch",
            NodeType = "ACME/Project"  // This points to the NodeType
        });

        // Act - Simulate the agent discovery flow
        // Step 1: Get the ProductLaunch node to find its NodeType
        var nodeQuery = "path:ACME/ProductLaunch scope:self";
        var nodeResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(nodeQuery)).ToListAsync();
        nodeResults.Should().HaveCount(1);
        var productLaunchNode = nodeResults.First() as MeshNode;
        productLaunchNode!.NodeType.Should().Be("ACME/Project");

        // Step 2: Query for agents under the NodeType with scope:hierarchy
        var nodeTypePath = productLaunchNode.NodeType;
        var agentQuery = $"path:{nodeTypePath} nodeType:Agent scope:hierarchy";
        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(agentQuery)).ToListAsync();

        // Assert - Should find the TodoAgent
        agentResults.Should().HaveCount(1);
        var todoAgent = agentResults.First() as MeshNode;
        todoAgent!.Name.Should().Be("Project Task Agent");
        todoAgent.Path.Should().Be("ACME/Project/TodoAgent");
    }

    [Fact]
    public async Task QueryAsync_ScopeHierarchy_FindsMultipleAgentsUnderNodeType()
    {
        // Arrange - Multiple agents under a NodeType
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project") with
        {
            Name = "Project",
            NodeType = "NodeType"
        });

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/TodoAgent") with
        {
            Name = "Project Task Agent",
            NodeType = "Agent"
        });

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/ReportAgent") with
        {
            Name = "Project Report Agent",
            NodeType = "Agent"
        });

        // A non-agent child should not be included
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/Todo") with
        {
            Name = "Todo NodeType",
            NodeType = "NodeType"
        });

        // Act
        var agentQuery = "path:ACME/Project nodeType:Agent scope:hierarchy";
        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(agentQuery)).ToListAsync();

        // Assert - Should find both agents but not the Todo NodeType
        agentResults.Should().HaveCount(2);
        agentResults.Cast<MeshNode>().Select(n => n.Name)
            .Should().Contain(["Project Task Agent", "Project Report Agent"]);
        agentResults.Cast<MeshNode>().Select(n => n.Name)
            .Should().NotContain("Todo NodeType");
    }

    [Fact]
    public async Task QueryAsync_ScopeHierarchy_IncludesSelfIfMatchesFilter()
    {
        // Arrange - The NodeType path itself could also be an agent
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project") with
        {
            Name = "Project Agent",
            NodeType = "Agent"  // The path itself is an Agent
        });

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/TodoAgent") with
        {
            Name = "Project Task Agent",
            NodeType = "Agent"
        });

        // Act
        var agentQuery = "path:ACME/Project nodeType:Agent scope:hierarchy";
        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(agentQuery)).ToListAsync();

        // Assert - Should find both the path itself (if it's an agent) and its children
        agentResults.Should().HaveCount(2);
        agentResults.Cast<MeshNode>().Select(n => n.Name)
            .Should().Contain(["Project Agent", "Project Task Agent"]);
    }

    /// <summary>
    /// Tests that scope:hierarchy finds BOTH:
    /// 1. Agents at ancestor paths (e.g., root-level Navigator)
    /// 2. Agents at descendant paths (e.g., ACME/Project/TodoAgent)
    /// This is crucial for agent discovery - we want type-specific agents AND inherited global agents.
    /// </summary>
    [Fact]
    public async Task QueryAsync_ScopeHierarchy_FindsBothAncestorAndDescendantAgents()
    {
        // Arrange
        // Root-level agent (at namespace root)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("Navigator") with
        {
            Name = "Navigator",
            NodeType = "Agent"
        });

        // ACME namespace agent
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME") with
        {
            Name = "ACME Organization",
            NodeType = "Organization"
        });

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/ACMEAgent") with
        {
            Name = "ACME Agent",
            NodeType = "Agent"
        });

        // NodeType at ACME/Project
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project") with
        {
            Name = "Project",
            NodeType = "NodeType"
        });

        // Agent under the NodeType
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/TodoAgent") with
        {
            Name = "Project Task Agent",
            NodeType = "Agent"
        });

        // Act - Query with hierarchy scope from ACME/Project
        // Should find: Navigator (root), ACME/ACMEAgent (ancestor's child via hierarchy), ACME/Project/TodoAgent (descendant)
        var agentQuery = "path:ACME/Project nodeType:Agent scope:hierarchy";
        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(agentQuery)).ToListAsync();

        // Assert - Should find agents both above and below ACME/Project
        var agentNames = agentResults.Cast<MeshNode>().Select(n => n.Name).ToList();

        // Must find the child agent
        agentNames.Should().Contain("Project Task Agent", "Should find child agent under NodeType");

        // Must find root-level agent
        agentNames.Should().Contain("Navigator", "Should find root-level agent via hierarchy");
    }

    [Fact]
    public async Task QueryAsync_ScopeMyselfAndAncestors_FindsAgentsAtExactAncestorPaths()
    {
        // Arrange - An agent defined at an exact ancestor path
        // Note: myselfAndAncestors only checks exact paths (self, parent, grandparent, etc.)
        // It does NOT check children of ancestors

        // Root agent at exact path "ACME"
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME") with
        {
            Name = "ACME Root Agent",
            NodeType = "Agent"  // The ACME node itself is an Agent
        });

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/ProductLaunch") with
        {
            Name = "MeshFlow Product Launch",
            NodeType = "Project"
        });

        // Act - Query for agents looking up the tree from ProductLaunch
        // This checks: ACME/ProductLaunch (self), ACME (parent)
        var agentQuery = "path:ACME/ProductLaunch nodeType:Agent scope:selfAndAncestors";
        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(agentQuery)).ToListAsync();

        // Assert - Should find the ACME root agent because it's at the exact parent path
        agentResults.Should().HaveCount(1);
        var rootAgent = agentResults.First() as MeshNode;
        rootAgent!.Name.Should().Be("ACME Root Agent");
        rootAgent.Path.Should().Be("ACME");
    }

    [Fact]
    public async Task QueryAsync_ScopeSelfAndAncestors_FindsChildrenOfAncestorPaths()
    {
        // Arrange - This test verifies that selfAndAncestors DOES find
        // children at each ancestor level (for agent discovery)

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME") with
        {
            Name = "ACME Root",
            NodeType = "Organization"
        });

        // GlobalAgent is a CHILD of ACME - should be found when searching from ACME/ProductLaunch
        // because ACME is an ancestor, and we search children of ancestors
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/GlobalAgent") with
        {
            Name = "Global Agent",
            NodeType = "Agent"
        });

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/ProductLaunch") with
        {
            Name = "MeshFlow Product Launch",
            NodeType = "Project"
        });

        // Act - Query for agents looking up the tree
        // This checks children of: ACME/ProductLaunch (self), ACME (parent), and root
        // GlobalAgent is a child of ACME, so it should be found
        var agentQuery = "path:ACME/ProductLaunch nodeType:Agent scope:selfAndAncestors";
        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(agentQuery)).ToListAsync();

        // Assert - Should find GlobalAgent because we search children of ancestors
        agentResults.Should().ContainSingle();
        agentResults.Cast<MeshNode>().Should().Contain(n => n.Name == "Global Agent");
    }

    #endregion

    #region DevLogin User Query Tests

    [Fact]
    public async Task QueryAsync_DevLogin_FindsUserNodesUnderUserNamespace()
    {
        // Arrange - replicate the User folder from samples/Graph/Data
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("User/Alice") with
        {
            Name = "Alice Chen",
            NodeType = "User",
            Content = new { name = "Alice Chen", email = "alice.chen@example.com", role = "Senior Software Engineer" }
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("User/Bob") with
        {
            Name = "Bob Wilson",
            NodeType = "User",
            Content = new { name = "Bob Wilson", email = "bob.wilson@example.com", role = "Product Manager" }
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("User/Carol") with
        {
            Name = "Carol Martinez",
            NodeType = "User",
            Content = new { name = "Carol Martinez", email = "carol.martinez@example.com" }
        });
        // Non-User node under User namespace (should be excluded by nodeType filter)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("User/Public_Access") with
        {
            Name = "Public Access",
            NodeType = "AccessAssignment",
            Content = new { accessObject = "Public", roles = new[] { new { role = "Viewer" } } }
        });

        // Act - this is the query used by DevLogin.razor
        var query = "nodeType:User namespace:User scope:descendants";
        var results = await MeshQuery.QueryAsync<MeshNode>(query).ToListAsync();

        // Assert
        results.Should().HaveCount(3);
        results.Select(n => n.Name).Should().Contain(["Alice Chen", "Bob Wilson", "Carol Martinez"]);
        results.Select(n => n.Name).Should().NotContain("Public Access");
    }

    [Fact]
    public async Task QueryAsync_DevLogin_NamespaceUserWithoutScope_FindsImmediateChildren()
    {
        // Arrange - same data but test without explicit scope (defaults to Children)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("User/Alice") with
        {
            Name = "Alice Chen",
            NodeType = "User"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("User/Bob") with
        {
            Name = "Bob Wilson",
            NodeType = "User"
        });

        // Act - namespace:User without scope defaults to Children
        var query = "nodeType:User namespace:User";
        var results = await MeshQuery.QueryAsync<MeshNode>(query).ToListAsync();

        // Assert - Children scope should still find immediate children
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Alice Chen", "Bob Wilson"]);
    }

    #endregion

    #region DevLogin Signin Tests

    [Fact]
    public async Task DevLogin_Signin_FindsUserByPath()
    {
        // Arrange - save a User node like in samples/Graph/Data
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("User/Roland") with
        {
            Name = "Roland Buergi",
            NodeType = "User",
            Content = new { name = "Roland Buergi", email = "roland@example.com", role = "Admin" }
        });

        // Act - this is the exact query from DevAuthController.Login
        var personId = "Roland";
        var node = await MeshQuery.QueryAsync<MeshNode>($"path:User/{personId} scope:self").FirstOrDefaultAsync();

        // Assert
        node.Should().NotBeNull();
        node!.NodeType.Should().Be("User");
        node.Id.Should().Be("Roland");
        node.Content.Should().NotBeNull();
    }

    #endregion
}
