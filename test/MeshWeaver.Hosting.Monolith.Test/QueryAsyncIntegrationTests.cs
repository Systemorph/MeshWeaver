using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class QueryAsyncIntegrationTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected IPersistenceService Persistence => Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
    protected IMeshQuery MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

    [Fact]
    public async Task QueryAsync_FilterByProperty_ReturnsMatchingNodes()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Gaming Laptop Pro" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/desktop") with { Name = "Desktop Computer" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop1") with { Name = "Gaming Laptop", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop2") with { Name = "Business Laptop", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Gaming Chair", NodeType = "Furniture" });

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
        await Persistence.SaveNodeAsync(new MeshNode("org") { Name = "Organization", NodeType = "container" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("other/company") with { Name = "Other Company", NodeType = "company" });

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
        await Persistence.SaveNodeAsync(new MeshNode("org") { Name = "Organization Root", NodeType = "root" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/food") with { Name = "Food", NodeType = "Groceries" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop-pro") with { Name = "Laptop Pro", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop-basic") with { Name = "Laptop Basic", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/desktop") with { Name = "Desktop Computer", NodeType = "Electronics" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/food") with { Name = "Food", NodeType = "Groceries" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("other/chair") with { Name = "Chair" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with { Name = "Beta Inc", NodeType = "company" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("other/company") with { Name = "Other Company", NodeType = "company" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project/task") with { Name = "Task A", NodeType = "task" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("other/company") with { Name = "Other Company", NodeType = "company" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with { Name = "Beta Inc", NodeType = "company" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/project") with { Name = "Org Project", NodeType = "project" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/child") with { Name = "Acme Child", NodeType = "company" });

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
        await Persistence.SaveNodeAsync(new MeshNode("products") { Name = "Products", NodeType = "container" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop/accessories") with { Name = "Accessories", NodeType = "Electronics" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with { Name = "Beta Inc", NodeType = "company" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });

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
    /// - ACME/ProductLaunch has nodeType="ACME/Software/Project"
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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project") with
        {
            Name = "Project",
            NodeType = "NodeType"
        });

        // Agent defined as a child of the NodeType
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project/TodoAgent") with
        {
            Name = "Project Task Agent",
            NodeType = "Agent"
        });

        // Instance of the NodeType
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/ProductLaunch") with
        {
            Name = "MeshFlow Product Launch",
            NodeType = "ACME/Software/Project"  // This points to the NodeType
        });

        // Act - Simulate the agent discovery flow
        // Step 1: Get the ProductLaunch node to find its NodeType
        var nodeQuery = "path:ACME/Software/ProductLaunch scope:self";
        var nodeResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(nodeQuery)).ToListAsync();
        nodeResults.Should().HaveCount(1);
        var productLaunchNode = nodeResults.First() as MeshNode;
        productLaunchNode!.NodeType.Should().Be("ACME/Software/Project");

        // Step 2: Query for agents under the NodeType with scope:hierarchy
        var nodeTypePath = productLaunchNode.NodeType;
        var agentQuery = $"path:{nodeTypePath} nodeType:Agent scope:hierarchy";
        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(agentQuery)).ToListAsync();

        // Assert - Should find the TodoAgent
        agentResults.Should().HaveCount(1);
        var todoAgent = agentResults.First() as MeshNode;
        todoAgent!.Name.Should().Be("Project Task Agent");
        todoAgent.Path.Should().Be("ACME/Software/Project/TodoAgent");
    }

    [Fact]
    public async Task QueryAsync_ScopeHierarchy_FindsMultipleAgentsUnderNodeType()
    {
        // Arrange - Multiple agents under a NodeType
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project") with
        {
            Name = "Project",
            NodeType = "NodeType"
        });

        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project/TodoAgent") with
        {
            Name = "Project Task Agent",
            NodeType = "Agent"
        });

        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project/ReportAgent") with
        {
            Name = "Project Report Agent",
            NodeType = "Agent"
        });

        // A non-agent child should not be included
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project/Todo") with
        {
            Name = "Todo NodeType",
            NodeType = "NodeType"
        });

        // Act
        var agentQuery = "path:ACME/Software/Project nodeType:Agent scope:hierarchy";
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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project") with
        {
            Name = "Project Agent",
            NodeType = "Agent"  // The path itself is an Agent
        });

        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project/TodoAgent") with
        {
            Name = "Project Task Agent",
            NodeType = "Agent"
        });

        // Act
        var agentQuery = "path:ACME/Software/Project nodeType:Agent scope:hierarchy";
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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Navigator") with
        {
            Name = "Navigator",
            NodeType = "Agent"
        });

        // ACME namespace agent
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME") with
        {
            Name = "ACME Organization",
            NodeType = "Organization"
        });

        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/ACMEAgent") with
        {
            Name = "ACME Agent",
            NodeType = "Agent"
        });

        // NodeType at ACME/Project
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project") with
        {
            Name = "Project",
            NodeType = "NodeType"
        });

        // Agent under the NodeType
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project/TodoAgent") with
        {
            Name = "Project Task Agent",
            NodeType = "Agent"
        });

        // Act - Query with hierarchy scope from ACME/Project
        // Should find: Navigator (root), ACME/ACMEAgent (ancestor's child via hierarchy), ACME/Project/TodoAgent (descendant)
        var agentQuery = "path:ACME/Software/Project nodeType:Agent scope:hierarchy";
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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME") with
        {
            Name = "ACME Root Agent",
            NodeType = "Agent"  // The ACME node itself is an Agent
        });

        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/ProductLaunch") with
        {
            Name = "MeshFlow Product Launch",
            NodeType = "Project"
        });

        // Act - Query for agents looking up the tree from ProductLaunch
        // This checks: ACME/ProductLaunch (self), ACME (parent)
        var agentQuery = "path:ACME/Software/ProductLaunch nodeType:Agent scope:selfAndAncestors";
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

        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME") with
        {
            Name = "ACME Root",
            NodeType = "Organization"
        });

        // GlobalAgent is a CHILD of ACME - should be found when searching from ACME/ProductLaunch
        // because ACME is an ancestor, and we search children of ancestors
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/GlobalAgent") with
        {
            Name = "Global Agent",
            NodeType = "Agent"
        });

        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/ProductLaunch") with
        {
            Name = "MeshFlow Product Launch",
            NodeType = "Project"
        });

        // Act - Query for agents looking up the tree
        // This checks children of: ACME/ProductLaunch (self), ACME (parent), and root
        // GlobalAgent is a child of ACME, so it should be found
        var agentQuery = "path:ACME/Software/ProductLaunch nodeType:Agent scope:selfAndAncestors";
        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(agentQuery)).ToListAsync();

        // Assert - Should find GlobalAgent because we search children of ancestors
        agentResults.Should().ContainSingle();
        agentResults.Cast<MeshNode>().Should().Contain(n => n.Name == "Global Agent");
    }

    #endregion
}
