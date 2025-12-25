using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class QueryAsyncIntegrationTests
{
    private readonly InMemoryPersistenceService _persistence = new();

    [Fact]
    public async Task QueryAsync_FilterByProperty_ReturnsMatchingNodes()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/laptop") with { Name = "Laptop", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/phone") with { Name = "Phone", NodeType = "Electronics" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("products/chair") with { Name = "Chair", NodeType = "Furniture" });

        // Act - need to use descendants scope to search child nodes
        var results = await _persistence.QueryAsync("nodeType==Electronics;$scope=descendants", "products").ToListAsync();

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

        // Act - need descendants scope to search child nodes
        var results = await _persistence.QueryAsync("$search=laptop;$scope=descendants", "products").ToListAsync();

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

        // Act - need descendants scope to search child nodes
        var results = await _persistence.QueryAsync("nodeType==Electronics;$search=gaming;$scope=descendants", "products").ToListAsync();

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
        var results = await _persistence.QueryAsync("nodeType==company;$scope=descendants", "org").ToListAsync();

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
        var results = await _persistence.QueryAsync("nodeType==root;$scope=ancestors", "org/acme/project").ToListAsync();

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

        // Act - need descendants scope to search child nodes
        var results = await _persistence.QueryAsync("nodeType=in=(Electronics,Furniture);$scope=descendants", "products").ToListAsync();

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

        // Act - need descendants scope to search child nodes
        var results = await _persistence.QueryAsync("name=like=*Laptop*;$scope=descendants", "products").ToListAsync();

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

        // Act - need descendants scope to search child nodes
        var results = await _persistence.QueryAsync("nodeType==Electronics,nodeType==Furniture;$scope=descendants", "products").ToListAsync();

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

        // Act - Empty query should return the node at the exact path only
        var results = await _persistence.QueryAsync("", "products").ToListAsync();

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
        var results = await _persistence.QueryAsync("nodeType!=Electronics;$scope=descendants", "products").ToListAsync();

        // Assert
        results.Should().HaveCount(1);
        var node = results.First() as MeshNode;
        node!.Name.Should().Be("Chair");
    }
}
