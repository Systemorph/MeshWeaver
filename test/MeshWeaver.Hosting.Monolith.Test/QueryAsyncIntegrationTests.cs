using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class QueryAsyncIntegrationTests
{
    private readonly InMemoryPersistenceService _persistence = new();
    private readonly IMeshQuery _meshQuery;

    public QueryAsyncIntegrationTests()
    {
        _meshQuery = new InMemoryMeshQuery(_persistence);
    }

    [Fact]
    public async Task QueryAsync_FilterByProperty_ReturnsMatchingNodes()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products nodeType:Electronics scope:descendants";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Phone"]);
    }

    [Fact]
    public async Task QueryAsync_FilterWithTextSearch_ReturnsFuzzyMatches()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Gaming Laptop Pro", Description = "High performance gaming laptop" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/desktop") with { Name = "Desktop Computer", Description = "Standard desktop" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products laptop scope:descendants";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(1);
        var node = results.First() as MeshNode;
        node!.Name.Should().Be("Gaming Laptop Pro");
    }

    [Fact]
    public async Task QueryAsync_CombinedFilterAndSearch_ReturnsMatchingResults()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop1") with { Name = "Gaming Laptop", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop2") with { Name = "Business Laptop", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Gaming Chair", NodeType = "Furniture" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products nodeType:Electronics gaming scope:descendants";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(1);
        var node = results.First() as MeshNode;
        node!.Name.Should().Be("Gaming Laptop");
    }

    [Fact]
    public async Task QueryAsync_ScopeDescendants_SearchesAllChildren()
    {
        // Arrange
        await _persistence.SaveNodeAsync(new MeshNode("org") { Name = "Organization", NodeType = "container" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("other/company") with { Name = "Other Company", NodeType = "company" });

        // Act
        var query = "path:org nodeType:company scope:descendants";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(1);
        var node = results.First() as MeshNode;
        node!.Name.Should().Be("Acme Corp");
    }

    [Fact]
    public async Task QueryAsync_ScopeAncestors_SearchesParentPaths()
    {
        // Arrange
        await _persistence.SaveNodeAsync(new MeshNode("org") { Name = "Organization Root", NodeType = "root" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });

        // Act
        var query = "path:org/acme/project nodeType:root scope:ancestors";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(1);
        var node = results.First() as MeshNode;
        node!.Name.Should().Be("Organization Root");
    }

    [Fact]
    public async Task QueryAsync_InOperator_MatchesMultipleValues()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/food") with { Name = "Food", NodeType = "Groceries" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products nodeType:(Electronics OR Furniture) scope:descendants";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(3);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Phone", "Chair"]);
    }

    [Fact]
    public async Task QueryAsync_LikeOperator_MatchesWildcard()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop-pro") with { Name = "Laptop Pro", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop-basic") with { Name = "Laptop Basic", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/desktop") with { Name = "Desktop Computer", NodeType = "Electronics" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products name:*Laptop* scope:descendants";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop Pro", "Laptop Basic"]);
    }

    [Fact]
    public async Task QueryAsync_OrLogic_MatchesEitherCondition()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/food") with { Name = "Food", NodeType = "Groceries" });

        // Act - use path: in query string with scope:descendants
        var query = "path:products (nodeType:Electronics OR nodeType:Furniture) scope:descendants";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Chair"]);
    }

    [Fact]
    public async Task QueryAsync_EmptyQuery_ReturnsAllAtPath()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("other/chair") with { Name = "Chair" });

        // Act - Empty query with path should return the node at the exact path only
        var query = "path:products";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert - products node doesn't exist, so empty result for exact path
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_NotEqualOperator_ExcludesMatches()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });

        // Act
        var query = "path:products -nodeType:Electronics scope:descendants";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

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
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with { Name = "Beta Inc", NodeType = "company" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("other/company") with { Name = "Other Company", NodeType = "company" });

        // Act - namespace:org without scope defaults to children (immediate only, not recursive)
        var query = "namespace:org";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

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
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project/task") with { Name = "Task A", NodeType = "task" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("other/company") with { Name = "Other Company", NodeType = "company" });

        // Act - namespace:org with scope:descendants should find all nested nodes
        var query = "namespace:org scope:descendants";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert - should find all nested nodes under org, but not other
        results.Should().HaveCount(3);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Acme Corp", "Project X", "Task A"]);
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Other Company");
    }

    [Fact]
    public async Task QueryAsync_NamespaceWithFilter_SearchesImmediateChildrenWithFilter()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with { Name = "Beta Inc", NodeType = "company" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/project") with { Name = "Org Project", NodeType = "project" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/child") with { Name = "Acme Child", NodeType = "company" });

        // Act - namespace:org with filter searches immediate children only and applies filter
        var query = "namespace:org nodeType:company";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

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
        await _persistence.SaveNodeAsync(new MeshNode("products") { Name = "Products", NodeType = "container" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop/accessories") with { Name = "Accessories", NodeType = "Electronics" });

        // Act - path:products with scope:children should find immediate children only
        var query = "path:products scope:children";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert - should find laptop and phone, not accessories (nested) or products itself
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Laptop", "Phone"]);
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Accessories");
    }

    [Fact]
    public async Task QueryAsync_NamespaceWithScopeChildren_LimitsToImmediateChildren()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "company" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with { Name = "Beta Inc", NodeType = "company" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project") with { Name = "Project X", NodeType = "project" });

        // Act - namespace:org with scope:children limits to immediate children
        var query = "namespace:org scope:children";
        var results = await _meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        // Assert - should find only immediate children (acme, beta), not nested (project)
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain(["Acme Corp", "Beta Inc"]);
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Project X");
    }

    #endregion
}
