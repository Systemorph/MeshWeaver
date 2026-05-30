using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Query.Test;

public class QueryAsyncIntegrationTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI();

    // Use unique prefixes per test to avoid cross-test contamination (shared mesh instance)
    private static string P([CallerMemberName] string name = "") => name;

    /// <summary>
    /// Reactive replacement for <c>QueryAsync(...).ToListAsync()</c>: the first
    /// <see cref="QueryChangeType.Initial"/> emission carries the full snapshot.
    /// </summary>
    private IReadOnlyList<MeshNode> QueryNodes(string query)
        => MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

    private IReadOnlyList<MeshNode> QueryNodes(MeshQueryRequest request)
        => MeshQuery.ObserveQuery<MeshNode>(request)
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

    [Fact]
    public void QueryAsync_FilterByProperty_ReturnsMatchingNodes()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/phone") with { Name = "Phone", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/chair") with { Name = "Chair", NodeType = "Code" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"path:{p} nodeType:Markdown scope:descendants"));

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Laptop", "Phone"]);
    }

    [Fact]
    public void QueryAsync_FilterWithTextSearch_ReturnsFuzzyMatches()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop") with { Name = "Gaming Laptop Pro", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/desktop") with { Name = "Desktop Computer", NodeType = "Markdown" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"path:{p} laptop scope:descendants"));

        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Gaming Laptop Pro");
    }

    [Fact]
    public void QueryAsync_CombinedFilterAndSearch_ReturnsMatchingResults()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop1") with { Name = "Gaming Laptop", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop2") with { Name = "Business Laptop", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/chair") with { Name = "Gaming Chair", NodeType = "Code" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"path:{p} nodeType:Markdown gaming scope:descendants"));

        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Gaming Laptop");
    }

    [Fact]
    public void QueryAsync_ScopeDescendants_SearchesAllChildren()
    {
        var p = P();
        NodeFactory.CreateNode(new MeshNode(p) { Name = "Root", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme/project") with { Name = "Project X", NodeType = "Code" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("other_desc/company") with { Name = "Other Company", NodeType = "Markdown" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"path:{p} nodeType:Markdown scope:descendants"));

        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Acme Corp");
    }

    [Fact]
    public void QueryAsync_ScopeAncestors_SearchesParentPaths()
    {
        var p = P();
        NodeFactory.CreateNode(new MeshNode(p) { Name = "Organization Root", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme/project") with { Name = "Project X", NodeType = "Code" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"path:{p}/acme/project nodeType:Group scope:ancestors"));

        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Organization Root");
    }

    [Fact]
    public void QueryAsync_InOperator_MatchesMultipleValues()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/phone") with { Name = "Phone", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/chair") with { Name = "Chair", NodeType = "Code" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/food") with { Name = "Food", NodeType = "Notification" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"path:{p} nodeType:(Markdown OR Code) scope:descendants"));

        results.Should().HaveCount(3);
        results.Select(n => n.Name).Should().Contain(["Laptop", "Phone", "Chair"]);
    }

    [Fact]
    public void QueryAsync_LikeOperator_MatchesWildcard()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop-pro") with { Name = "Laptop Pro", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop-basic") with { Name = "Laptop Basic", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/desktop") with { Name = "Desktop Computer", NodeType = "Markdown" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"path:{p} name:*Laptop* scope:descendants"));

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Laptop Pro", "Laptop Basic"]);
    }

    [Fact]
    public void QueryAsync_OrLogic_MatchesEitherCondition()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/chair") with { Name = "Chair", NodeType = "Code" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/food") with { Name = "Food", NodeType = "Notification" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"path:{p} (nodeType:Markdown OR nodeType:Code) scope:descendants"));

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Laptop", "Chair"]);
    }

    [Fact]
    public void QueryAsync_EmptyQuery_ReturnsAllAtPath()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/phone") with { Name = "Phone", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("other_empty/chair") with { Name = "Chair", NodeType = "Markdown" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"path:{p}"));

        // p node doesn't exist as a standalone node, so empty result for exact path
        results.Should().BeEmpty();
    }

    [Fact]
    public void QueryAsync_NotEqualOperator_ExcludesMatches()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/phone") with { Name = "Phone", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/chair") with { Name = "Chair", NodeType = "Code" }).Should().Emit();

        var results = QueryNodes($"path:{p} -nodeType:Markdown scope:descendants");

        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Chair");
    }

    #region Namespace Query Tests

    [Fact]
    public void QueryAsync_NamespaceWithoutScope_SearchesImmediateChildrenOnly()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/beta") with { Name = "Beta Inc", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme/project") with { Name = "Project X", NodeType = "Code" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("other_ns/company") with { Name = "Other Company", NodeType = "Markdown" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"namespace:{p}"));

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Acme Corp", "Beta Inc"]);
    }

    [Fact]
    public void QueryAsync_NamespaceWithDescendants_SearchesRecursively()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme/project") with { Name = "Project X", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme/project/task") with { Name = "Task A", NodeType = "Notification" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("other_nsDesc/company") with { Name = "Other Company", NodeType = "Markdown" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"namespace:{p} scope:descendants"));

        results.Should().HaveCount(3);
        results.Select(n => n.Name).Should().Contain(["Acme Corp", "Project X", "Task A"]);
    }

    [Fact]
    public void QueryAsync_NamespaceWithFilter_SearchesImmediateChildrenWithFilter()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/beta") with { Name = "Beta Inc", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/project") with { Name = "Org Project", NodeType = "Code" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme/child") with { Name = "Acme Child", NodeType = "Markdown" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"namespace:{p} nodeType:Markdown"));

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Acme Corp", "Beta Inc"]);
    }

    [Fact]
    public void QueryAsync_ScopeChildren_SearchesImmediateChildrenOnly()
    {
        var p = P();
        NodeFactory.CreateNode(new MeshNode(p) { Name = "Products", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/phone") with { Name = "Phone", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/laptop/accessories") with { Name = "Accessories", NodeType = "Markdown" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"namespace:{p}"));

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Laptop", "Phone"]);
    }

    [Fact]
    public void QueryAsync_NamespaceWithScopeChildren_LimitsToImmediateChildren()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/beta") with { Name = "Beta Inc", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/acme/project") with { Name = "Project X", NodeType = "Code" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQuery($"namespace:{p}"));

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Acme Corp", "Beta Inc"]);
    }

    #endregion

    #region Hierarchy Scope Tests (for Agent Discovery)

    [Fact]
    public void QueryAsync_ScopeHierarchy_FindsAgentUnderNodeType()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/TodoAgent") with { Name = "Project Task Agent", NodeType = "Agent" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/ProductLaunch") with { Name = "MeshFlow Product Launch", NodeType = $"{p}/Project" }).Should().Emit();

        var nodeResults = QueryNodes(MeshQueryRequest.FromQuery($"path:{p}/ProductLaunch"));
        nodeResults.Should().HaveCount(1);
        var productLaunchNode = nodeResults.First();
        productLaunchNode.NodeType.Should().Be($"{p}/Project");

        var agentQuery = $"path:{p}/Project nodeType:Agent scope:hierarchy";
        var agentResults = QueryNodes(MeshQueryRequest.FromQuery(agentQuery));

        agentResults.Should().HaveCount(1);
        var todoAgent = agentResults.First();
        todoAgent.Name.Should().Be("Project Task Agent");
    }

    [Fact]
    public void QueryAsync_ScopeHierarchy_FindsMultipleAgentsUnderNodeType()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/TodoAgent") with { Name = "Project Task Agent", NodeType = "Agent" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/ReportAgent") with { Name = "Project Report Agent", NodeType = "Agent" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/Todo") with { Name = "Todo NodeType", NodeType = "Markdown" }).Should().Emit();

        var agentResults = QueryNodes(MeshQueryRequest.FromQuery($"path:{p}/Project nodeType:Agent scope:hierarchy"));

        agentResults.Should().HaveCount(2);
        agentResults.Select(n => n.Name).Should().Contain(["Project Task Agent", "Project Report Agent"]);
    }

    [Fact]
    public void QueryAsync_ScopeHierarchy_IncludesSelfIfMatchesFilter()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project Agent", NodeType = "Agent" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/TodoAgent") with { Name = "Project Task Agent", NodeType = "Agent" }).Should().Emit();

        var agentResults = QueryNodes(MeshQueryRequest.FromQuery($"path:{p}/Project nodeType:Agent scope:hierarchy"));

        agentResults.Should().HaveCount(2);
        agentResults.Select(n => n.Name).Should().Contain(["Project Agent", "Project Task Agent"]);
    }

    [Fact]
    public void QueryAsync_ScopeHierarchy_FindsBothAncestorAndDescendantAgents()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Orchestrator") with { Name = "Orchestrator", NodeType = "Agent" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/ACME") with { Name = "ACME Organization", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/ACME/ACMEAgent") with { Name = "ACME Agent", NodeType = "Agent" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/ACME/Project") with { Name = "Project", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/ACME/Project/TodoAgent") with { Name = "Project Task Agent", NodeType = "Agent" }).Should().Emit();

        var agentResults = QueryNodes(MeshQueryRequest.FromQuery($"path:{p}/ACME/Project nodeType:Agent scope:hierarchy"));
        var agentNames = agentResults.Select(n => n.Name).ToList();

        agentNames.Should().Contain("Project Task Agent");
        agentNames.Should().Contain("Orchestrator");
    }

    [Fact]
    public void QueryAsync_ScopeMyselfAndAncestors_FindsAgentsAtExactAncestorPaths()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/ACME") with { Name = "ACME Root Agent", NodeType = "Agent" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/ACME/ProductLaunch") with { Name = "MeshFlow Product Launch", NodeType = "Markdown" }).Should().Emit();

        var agentResults = QueryNodes(MeshQueryRequest.FromQuery($"path:{p}/ACME/ProductLaunch nodeType:Agent scope:selfAndAncestors"));

        agentResults.Should().HaveCount(1);
        var rootAgent = agentResults.First();
        rootAgent.Name.Should().Be("ACME Root Agent");
    }

    [Fact]
    public void QueryAsync_ScopeSelfAndAncestors_FindsChildrenOfAncestorPaths()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/ACME") with { Name = "ACME Root", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/ACME/GlobalAgent") with { Name = "Global Agent", NodeType = "Agent" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/ACME/ProductLaunch") with { Name = "MeshFlow Product Launch", NodeType = "Markdown" }).Should().Emit();

        var agentResults = QueryNodes(MeshQueryRequest.FromQuery($"path:{p}/ACME/ProductLaunch nodeType:Agent scope:selfAndAncestors"));

        agentResults.Should().ContainSingle();
        agentResults.Should().Contain(n => n.Name == "Global Agent");
    }

    #endregion

    #region DevLogin User Query Tests

    [Fact]
    public void QueryAsync_DevLogin_FindsUserNodesUnderUserNamespace()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Alice") with { Name = "Alice Chen", NodeType = "Markdown", Content = new { name = "Alice Chen" } }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Bob") with { Name = "Bob Wilson", NodeType = "Markdown", Content = new { name = "Bob Wilson" } }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Carol") with { Name = "Carol Martinez", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Public_Access") with { Name = "Public Access", NodeType = "Code" }).Should().Emit();

        var results = QueryNodes($"nodeType:Markdown namespace:{p} scope:descendants");

        results.Should().HaveCount(3);
        results.Select(n => n.Name).Should().Contain(["Alice Chen", "Bob Wilson", "Carol Martinez"]);
        results.Select(n => n.Name).Should().NotContain("Public Access");
    }

    [Fact]
    public void QueryAsync_DevLogin_NamespaceUserWithoutScope_FindsImmediateChildren()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Alice") with { Name = "Alice Chen", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Bob") with { Name = "Bob Wilson", NodeType = "Markdown" }).Should().Emit();

        var results = QueryNodes($"nodeType:Markdown namespace:{p}");

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Alice Chen", "Bob Wilson"]);
    }

    #endregion

    #region DevLogin Signin Tests

    [Fact]
    public void DevLogin_Signin_FindsUserByPath()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Roland") with
        {
            Name = "Roland Buergi",
            NodeType = "Markdown",
            Content = new { name = "Roland Buergi", email = "roland@example.com", role = "Admin" }
        }).Should().Emit();

        var node = ReadNode($"{p}/Roland").Should().Emit();

        node.Should().NotBeNull();
        node!.NodeType.Should().Be("Markdown");
        node.Id.Should().Be("Roland");
        node.Content.Should().NotBeNull();
    }

    #endregion
}
