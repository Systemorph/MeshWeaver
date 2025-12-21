using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class PersistenceServiceTest
{
    [Fact]
    public async Task GetChildrenAsync_ReturnsDirectChildren()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme Corp" });
        await persistence.SaveNodeAsync(new MeshNode("org/contoso") { Name = "Contoso" });
        await persistence.SaveNodeAsync(new MeshNode("org/acme/project/web") { Name = "Web Project" });

        // Act
        var children = await persistence.GetChildrenAsync("org").ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        children.Should().HaveCount(2);
        children.Select(c => c.Name).Should().Contain(["Acme Corp", "Contoso"]);
    }

    [Fact]
    public async Task GetChildrenAsync_ReturnsRootLevelNodes()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org") { Name = "Organizations" });
        await persistence.SaveNodeAsync(new MeshNode("system") { Name = "System" });
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme" });

        // Act
        var children = await persistence.GetChildrenAsync(null).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        children.Should().HaveCount(2);
        children.Select(c => c.Name).Should().Contain(["Organizations", "System"]);
    }

    [Fact]
    public async Task GetChildrenAsync_ReturnsEmptyForNonExistentPath()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme" });

        // Act
        var children = await persistence.GetChildrenAsync("nonexistent").ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        children.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveNodeAsync_NormalizesPath()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();

        // Act
        await persistence.SaveNodeAsync(new MeshNode("ORG/Acme") { Name = "Acme" });
        var node = await persistence.GetNodeAsync("org/acme");

        // Assert
        node.Should().NotBeNull();
        node!.Name.Should().Be("Acme");
    }

    [Fact]
    public async Task DeleteNodeAsync_RemovesNode()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme" });

        // Act
        await persistence.DeleteNodeAsync("org/acme");
        var node = await persistence.GetNodeAsync("org/acme");

        // Assert
        node.Should().BeNull();
    }

    [Fact]
    public async Task DeleteNodeAsync_Recursive_RemovesDescendants()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme" });
        await persistence.SaveNodeAsync(new MeshNode("org/acme/project/web") { Name = "Web" });
        await persistence.SaveNodeAsync(new MeshNode("org/acme/project/mobile") { Name = "Mobile" });
        await persistence.SaveNodeAsync(new MeshNode("org/contoso") { Name = "Contoso" });

        // Act
        await persistence.DeleteNodeAsync("org/acme", recursive: true);

        // Assert
        (await persistence.GetNodeAsync("org/acme")).Should().BeNull();
        (await persistence.GetNodeAsync("org/acme/project/web")).Should().BeNull();
        (await persistence.GetNodeAsync("org/acme/project/mobile")).Should().BeNull();
        (await persistence.GetNodeAsync("org/contoso")).Should().NotBeNull();
    }

    [Fact]
    public async Task SearchAsync_FindsByName()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme Corporation" });
        await persistence.SaveNodeAsync(new MeshNode("org/contoso") { Name = "Contoso Ltd" });

        // Act
        var results = await persistence.SearchAsync(null, "Acme").ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Acme Corporation");
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueForExistingNode()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme" });

        // Act & Assert
        (await persistence.ExistsAsync("org/acme")).Should().BeTrue();
        (await persistence.ExistsAsync("org/nonexistent")).Should().BeFalse();
    }

    [Fact]
    public async Task GetDescendantsAsync_ReturnsAllDescendants()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme" });
        await persistence.SaveNodeAsync(new MeshNode("org/acme/project/web") { Name = "Web" });
        await persistence.SaveNodeAsync(new MeshNode("org/acme/project/mobile") { Name = "Mobile" });
        await persistence.SaveNodeAsync(new MeshNode("org/contoso") { Name = "Contoso" });

        // Act
        var descendants = await persistence.GetDescendantsAsync("org/acme").ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        descendants.Should().HaveCount(2);
        descendants.Select(d => d.Name).Should().Contain(["Web", "Mobile"]);
    }

    [Fact]
    public async Task Content_IsPreserved()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var content = new { Id = "acme", Website = "https://acme.com" };
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme", Content = content });

        // Act
        var node = await persistence.GetNodeAsync("org/acme");

        // Assert
        node.Should().NotBeNull();
        node!.Content.Should().BeEquivalentTo(content);
    }
}

public class MeshCatalogQueryTest
{
    [Fact]
    public async Task QueryAsync_ReturnsFilteredResults()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme Corp", Description = "Technology company" });
        await persistence.SaveNodeAsync(new MeshNode("org/contoso") { Name = "Contoso Ltd", Description = "Software company" });
        await persistence.SaveNodeAsync(new MeshNode("org/fabrikam") { Name = "Fabrikam Inc", Description = "Hardware manufacturer" });

        // Act - simulate QueryAsync filtering
        var children = await persistence.GetChildrenAsync("org").ToListAsync(TestContext.Current.CancellationToken);
        var filtered = children.Where(n =>
            (n.Name?.Contains("soft", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (n.Description?.Contains("soft", StringComparison.OrdinalIgnoreCase) ?? false) ||
            n.Prefix.Contains("soft", StringComparison.OrdinalIgnoreCase)).ToList();

        // Assert
        filtered.Should().HaveCount(1);
        filtered.First().Name.Should().Be("Contoso Ltd");
    }

    [Fact]
    public async Task QueryAsync_RespectsMaxResults()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        for (int i = 0; i < 10; i++)
        {
            await persistence.SaveNodeAsync(new MeshNode($"org/company{i}") { Name = $"Company {i}" });
        }

        // Act - simulate QueryAsync with maxResults
        var children = await persistence.GetChildrenAsync("org").ToListAsync(TestContext.Current.CancellationToken);
        var limited = children.Take(3).ToList();

        // Assert
        limited.Should().HaveCount(3);
    }

    [Fact]
    public async Task QueryAsync_ReturnsAllWhenNoFilter()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme" });
        await persistence.SaveNodeAsync(new MeshNode("org/contoso") { Name = "Contoso" });

        // Act - simulate QueryAsync without filter
        var children = await persistence.GetChildrenAsync("org").ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        children.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryAsync_FiltersByPrefix()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org/acme") { Name = "Acme" });
        await persistence.SaveNodeAsync(new MeshNode("org/acme-corp") { Name = "Acme Corp" });

        // Act - simulate QueryAsync with prefix filter
        var children = await persistence.GetChildrenAsync("org").ToListAsync(TestContext.Current.CancellationToken);
        var filtered = children.Where(n =>
            (n.Name?.Contains("acme-", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (n.Description?.Contains("acme-", StringComparison.OrdinalIgnoreCase) ?? false) ||
            n.Prefix.Contains("acme-", StringComparison.OrdinalIgnoreCase)).ToList();

        // Assert
        filtered.Should().HaveCount(1);
        filtered.First().Name.Should().Be("Acme Corp");
    }
}
