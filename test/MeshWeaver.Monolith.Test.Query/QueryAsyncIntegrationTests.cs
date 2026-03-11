using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Monolith.Test.Query;

public class QueryAsyncIntegrationTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    // Use unique prefixes per test to avoid cross-test contamination (shared mesh instance)
    private static string P([CallerMemberName] string name = "") => name;

    [Fact]
    public async Task QueryAsync_FilterByProperty_ReturnsMatchingNodes()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/phone") with { Name = "Phone", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/chair") with { Name = "Chair", NodeType = "Code" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p} nodeType:Markdown scope:descendants")).ToListAsync();

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Phone"]);
    }

    [Fact]
    public async Task QueryAsync_FilterWithTextSearch_ReturnsFuzzyMatches()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop") with { Name = "Gaming Laptop Pro" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/desktop") with { Name = "Desktop Computer" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p} laptop scope:descendants")).ToListAsync();

        results.Should().HaveCount(1);
        (results.First() as MeshNode)!.Name.Should().Be("Gaming Laptop Pro");
    }

    [Fact]
    public async Task QueryAsync_CombinedFilterAndSearch_ReturnsMatchingResults()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop1") with { Name = "Gaming Laptop", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop2") with { Name = "Business Laptop", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/chair") with { Name = "Gaming Chair", NodeType = "Code" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p} nodeType:Markdown gaming scope:descendants")).ToListAsync();

        results.Should().HaveCount(1);
        (results.First() as MeshNode)!.Name.Should().Be("Gaming Laptop");
    }

    [Fact]
    public async Task QueryAsync_ScopeDescendants_SearchesAllChildren()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(new MeshNode(p) { Name = "Root", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme/project") with { Name = "Project X", NodeType = "Code" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("other_desc/company") with { Name = "Other Company", NodeType = "Markdown" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p} nodeType:Markdown scope:descendants")).ToListAsync();

        results.Should().HaveCount(1);
        (results.First() as MeshNode)!.Name.Should().Be("Acme Corp");
    }

    [Fact]
    public async Task QueryAsync_ScopeAncestors_SearchesParentPaths()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(new MeshNode(p) { Name = "Organization Root", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme/project") with { Name = "Project X", NodeType = "Code" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p}/acme/project nodeType:Group scope:ancestors")).ToListAsync();

        results.Should().HaveCount(1);
        (results.First() as MeshNode)!.Name.Should().Be("Organization Root");
    }

    [Fact]
    public async Task QueryAsync_InOperator_MatchesMultipleValues()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/phone") with { Name = "Phone", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/chair") with { Name = "Chair", NodeType = "Code" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/food") with { Name = "Food", NodeType = "Notification" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p} nodeType:(Markdown OR Code) scope:descendants")).ToListAsync();

        results.Should().HaveCount(3);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Phone", "Chair"]);
    }

    [Fact]
    public async Task QueryAsync_LikeOperator_MatchesWildcard()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop-pro") with { Name = "Laptop Pro", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop-basic") with { Name = "Laptop Basic", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/desktop") with { Name = "Desktop Computer", NodeType = "Markdown" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p} name:*Laptop* scope:descendants")).ToListAsync();

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop Pro", "Laptop Basic"]);
    }

    [Fact]
    public async Task QueryAsync_OrLogic_MatchesEitherCondition()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/chair") with { Name = "Chair", NodeType = "Code" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/food") with { Name = "Food", NodeType = "Notification" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p} (nodeType:Markdown OR nodeType:Code) scope:descendants")).ToListAsync();

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Chair"]);
    }

    [Fact]
    public async Task QueryAsync_EmptyQuery_ReturnsAllAtPath()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/phone") with { Name = "Phone" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("other_empty/chair") with { Name = "Chair" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p}")).ToListAsync();

        // p node doesn't exist as a standalone node, so empty result for exact path
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_NotEqualOperator_ExcludesMatches()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/phone") with { Name = "Phone", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/chair") with { Name = "Chair", NodeType = "Code" });

        var results = await MeshQuery.QueryAsync<MeshNode>($"path:{p} -nodeType:Markdown scope:descendants").ToListAsync();

        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Chair");
    }

    #region Namespace Query Tests

    [Fact]
    public async Task QueryAsync_NamespaceWithoutScope_SearchesImmediateChildrenOnly()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/beta") with { Name = "Beta Inc", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme/project") with { Name = "Project X", NodeType = "Code" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("other_ns/company") with { Name = "Other Company", NodeType = "Markdown" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"namespace:{p}")).ToListAsync();

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Acme Corp", "Beta Inc"]);
    }

    [Fact]
    public async Task QueryAsync_NamespaceWithDescendants_SearchesRecursively()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme/project") with { Name = "Project X", NodeType = "Code" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme/project/task") with { Name = "Task A", NodeType = "Notification" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("other_nsDesc/company") with { Name = "Other Company", NodeType = "Markdown" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"namespace:{p} scope:descendants")).ToListAsync();

        results.Should().HaveCount(3);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Acme Corp", "Project X", "Task A"]);
    }

    [Fact]
    public async Task QueryAsync_NamespaceWithFilter_SearchesImmediateChildrenWithFilter()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/beta") with { Name = "Beta Inc", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/project") with { Name = "Org Project", NodeType = "Code" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme/child") with { Name = "Acme Child", NodeType = "Markdown" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"namespace:{p} nodeType:Markdown")).ToListAsync();

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Acme Corp", "Beta Inc"]);
    }

    [Fact]
    public async Task QueryAsync_ScopeChildren_SearchesImmediateChildrenOnly()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(new MeshNode(p) { Name = "Products", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop") with { Name = "Laptop", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/phone") with { Name = "Phone", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/laptop/accessories") with { Name = "Accessories", NodeType = "Markdown" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"namespace:{p}")).ToListAsync();

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Phone"]);
    }

    [Fact]
    public async Task QueryAsync_NamespaceWithScopeChildren_LimitsToImmediateChildren()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme") with { Name = "Acme Corp", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/beta") with { Name = "Beta Inc", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/acme/project") with { Name = "Project X", NodeType = "Code" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"namespace:{p}")).ToListAsync();

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Acme Corp", "Beta Inc"]);
    }

    #endregion

    #region Hierarchy Scope Tests (for Agent Discovery)

    [Fact]
    public async Task QueryAsync_ScopeHierarchy_FindsAgentUnderNodeType()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Project/TodoAgent") with { Name = "Project Task Agent", NodeType = "Agent" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/ProductLaunch") with { Name = "MeshFlow Product Launch", NodeType = $"{p}/Project" });

        var nodeResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p}/ProductLaunch scope:self")).ToListAsync();
        nodeResults.Should().HaveCount(1);
        var productLaunchNode = nodeResults.First() as MeshNode;
        productLaunchNode!.NodeType.Should().Be($"{p}/Project");

        var agentQuery = $"path:{p}/Project nodeType:Agent scope:hierarchy";
        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(agentQuery)).ToListAsync();

        agentResults.Should().HaveCount(1);
        var todoAgent = agentResults.First() as MeshNode;
        todoAgent!.Name.Should().Be("Project Task Agent");
    }

    [Fact]
    public async Task QueryAsync_ScopeHierarchy_FindsMultipleAgentsUnderNodeType()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Project/TodoAgent") with { Name = "Project Task Agent", NodeType = "Agent" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Project/ReportAgent") with { Name = "Project Report Agent", NodeType = "Agent" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Project/Todo") with { Name = "Todo NodeType", NodeType = "Markdown" });

        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p}/Project nodeType:Agent scope:hierarchy")).ToListAsync();

        agentResults.Should().HaveCount(2);
        agentResults.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Project Task Agent", "Project Report Agent"]);
    }

    [Fact]
    public async Task QueryAsync_ScopeHierarchy_IncludesSelfIfMatchesFilter()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Project") with { Name = "Project Agent", NodeType = "Agent" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Project/TodoAgent") with { Name = "Project Task Agent", NodeType = "Agent" });

        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p}/Project nodeType:Agent scope:hierarchy")).ToListAsync();

        agentResults.Should().HaveCount(2);
        agentResults.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Project Agent", "Project Task Agent"]);
    }

    [Fact]
    public async Task QueryAsync_ScopeHierarchy_FindsBothAncestorAndDescendantAgents()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Navigator") with { Name = "Navigator", NodeType = "Agent" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/ACME") with { Name = "ACME Organization", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/ACME/ACMEAgent") with { Name = "ACME Agent", NodeType = "Agent" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/ACME/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/ACME/Project/TodoAgent") with { Name = "Project Task Agent", NodeType = "Agent" });

        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p}/ACME/Project nodeType:Agent scope:hierarchy")).ToListAsync();
        var agentNames = agentResults.Cast<MeshNode>().Select(n => n.Name).ToList();

        agentNames.Should().Contain("Project Task Agent");
        agentNames.Should().Contain("Navigator");
    }

    [Fact]
    public async Task QueryAsync_ScopeMyselfAndAncestors_FindsAgentsAtExactAncestorPaths()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/ACME") with { Name = "ACME Root Agent", NodeType = "Agent" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/ACME/ProductLaunch") with { Name = "MeshFlow Product Launch", NodeType = "Markdown" });

        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p}/ACME/ProductLaunch nodeType:Agent scope:selfAndAncestors")).ToListAsync();

        agentResults.Should().HaveCount(1);
        var rootAgent = agentResults.First() as MeshNode;
        rootAgent!.Name.Should().Be("ACME Root Agent");
    }

    [Fact]
    public async Task QueryAsync_ScopeSelfAndAncestors_FindsChildrenOfAncestorPaths()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/ACME") with { Name = "ACME Root", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/ACME/GlobalAgent") with { Name = "Global Agent", NodeType = "Agent" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/ACME/ProductLaunch") with { Name = "MeshFlow Product Launch", NodeType = "Markdown" });

        var agentResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:{p}/ACME/ProductLaunch nodeType:Agent scope:selfAndAncestors")).ToListAsync();

        agentResults.Should().ContainSingle();
        agentResults.Cast<MeshNode>().Should().Contain(n => n.Name == "Global Agent");
    }

    #endregion

    #region DevLogin User Query Tests

    [Fact]
    public async Task QueryAsync_DevLogin_FindsUserNodesUnderUserNamespace()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Alice") with { Name = "Alice Chen", NodeType = "Markdown", Content = new { name = "Alice Chen" } });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Bob") with { Name = "Bob Wilson", NodeType = "Markdown", Content = new { name = "Bob Wilson" } });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Carol") with { Name = "Carol Martinez", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Public_Access") with { Name = "Public Access", NodeType = "Code" });

        var results = await MeshQuery.QueryAsync<MeshNode>($"nodeType:Markdown namespace:{p} scope:descendants").ToListAsync();

        results.Should().HaveCount(3);
        results.Select(n => n.Name).Should().Contain(["Alice Chen", "Bob Wilson", "Carol Martinez"]);
        results.Select(n => n.Name).Should().NotContain("Public Access");
    }

    [Fact]
    public async Task QueryAsync_DevLogin_NamespaceUserWithoutScope_FindsImmediateChildren()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Alice") with { Name = "Alice Chen", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Bob") with { Name = "Bob Wilson", NodeType = "Markdown" });

        var results = await MeshQuery.QueryAsync<MeshNode>($"nodeType:Markdown namespace:{p}").ToListAsync();

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Alice Chen", "Bob Wilson"]);
    }

    #endregion

    #region DevLogin Signin Tests

    [Fact]
    public async Task DevLogin_Signin_FindsUserByPath()
    {
        var p = P();
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/Roland") with
        {
            Name = "Roland Buergi",
            NodeType = "Markdown",
            Content = new { name = "Roland Buergi", email = "roland@example.com", role = "Admin" }
        });

        var node = await MeshQuery.QueryAsync<MeshNode>($"path:{p}/Roland scope:self").FirstOrDefaultAsync();

        node.Should().NotBeNull();
        node!.NodeType.Should().Be("Markdown");
        node.Id.Should().Be("Roland");
        node.Content.Should().NotBeNull();
    }

    #endregion
}
