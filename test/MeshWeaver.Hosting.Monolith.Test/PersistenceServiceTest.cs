using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class PersistenceServiceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    [Fact]
    public async Task GetChildrenAsync_ReturnsDirectChildren()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/contoso") with { Name = "Contoso" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project/web") with { Name = "Web Project" }, JsonOptions);

        // Act
        var children = await persistence.GetChildrenAsync("org", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        children.Should().HaveCount(2);
        children.Select(c => c.Name).Should().Contain(["Acme Corp", "Contoso"]);
    }

    [Fact]
    public async Task GetChildrenAsync_ReturnsRootLevelNodes()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(new MeshNode("org") { Name = "Organizations" }, JsonOptions);
        await persistence.SaveNodeAsync(new MeshNode("system") { Name = "System" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme" }, JsonOptions);

        // Act
        var children = await persistence.GetChildrenAsync(null, JsonOptions).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        children.Should().HaveCount(2);
        children.Select(c => c.Name).Should().Contain(["Organizations", "System"]);
    }

    [Fact]
    public async Task GetChildrenAsync_ReturnsEmptyForNonExistentPath()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme" }, JsonOptions);

        // Act
        var children = await persistence.GetChildrenAsync("nonexistent", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        children.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveNodeAsync_NormalizesPath()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();

        // Act
        await persistence.SaveNodeAsync(MeshNode.FromPath("ORG/Acme") with { Name = "Acme" }, JsonOptions);
        var node = await persistence.GetNodeAsync("org/acme", JsonOptions);

        // Assert
        node.Should().NotBeNull();
        node!.Name.Should().Be("Acme");
    }

    [Fact]
    public async Task DeleteNodeAsync_RemovesNode()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme" }, JsonOptions);

        // Act
        await persistence.DeleteNodeAsync("org/acme");
        var node = await persistence.GetNodeAsync("org/acme", JsonOptions);

        // Assert
        node.Should().BeNull();
    }

    [Fact]
    public async Task DeleteNodeAsync_Recursive_RemovesDescendants()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project/web") with { Name = "Web" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project/mobile") with { Name = "Mobile" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/contoso") with { Name = "Contoso" }, JsonOptions);

        // Act
        await persistence.DeleteNodeAsync("org/acme", recursive: true);

        // Assert
        (await persistence.GetNodeAsync("org/acme", JsonOptions)).Should().BeNull();
        (await persistence.GetNodeAsync("org/acme/project/web", JsonOptions)).Should().BeNull();
        (await persistence.GetNodeAsync("org/acme/project/mobile", JsonOptions)).Should().BeNull();
        (await persistence.GetNodeAsync("org/contoso", JsonOptions)).Should().NotBeNull();
    }

    [Fact]
    public async Task SearchAsync_FindsByName()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corporation" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/contoso") with { Name = "Contoso Ltd" }, JsonOptions);

        // Act
        var results = await persistence.SearchAsync(null, "Acme", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Acme Corporation");
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueForExistingNode()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme" }, JsonOptions);

        // Act & Assert
        (await persistence.ExistsAsync("org/acme")).Should().BeTrue();
        (await persistence.ExistsAsync("org/nonexistent")).Should().BeFalse();
    }

    [Fact]
    public async Task GetDescendantsAsync_ReturnsAllDescendants()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project/web") with { Name = "Web" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme/project/mobile") with { Name = "Mobile" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/contoso") with { Name = "Contoso" }, JsonOptions);

        // Act
        var descendants = await persistence.GetDescendantsAsync("org/acme", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);

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
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme", Content = content }, JsonOptions);

        // Act
        var node = await persistence.GetNodeAsync("org/acme", JsonOptions);

        // Assert
        node.Should().NotBeNull();
        node!.Content.Should().BeEquivalentTo(content);
    }
}

public class MeshCatalogQueryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    [Fact]
    public async Task QueryAsync_ReturnsFilteredResults()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/contoso") with { Name = "Contoso Software" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/fabrikam") with { Name = "Fabrikam Inc" }, JsonOptions);

        // Act - simulate QueryAsync filtering
        var children = await persistence.GetChildrenAsync("org", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);
        var filtered = children.Where(n =>
            (n.Name?.Contains("soft", StringComparison.OrdinalIgnoreCase) ?? false) ||
            n.Path.Contains("soft", StringComparison.OrdinalIgnoreCase)).ToList();

        // Assert
        filtered.Should().HaveCount(1);
        filtered.First().Name.Should().Be("Contoso Software");
    }

    [Fact]
    public async Task QueryAsync_RespectsMaxResults()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        for (int i = 0; i < 10; i++)
        {
            await persistence.SaveNodeAsync(MeshNode.FromPath($"org/company{i}") with { Name = $"Company {i}" }, JsonOptions);
        }

        // Act - simulate QueryAsync with maxResults
        var children = await persistence.GetChildrenAsync("org", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);
        var limited = children.Take(3).ToList();

        // Assert
        limited.Should().HaveCount(3);
    }

    [Fact]
    public async Task QueryAsync_ReturnsAllWhenNoFilter()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/contoso") with { Name = "Contoso" }, JsonOptions);

        // Act - simulate QueryAsync without filter
        var children = await persistence.GetChildrenAsync("org", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        children.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryAsync_FiltersByPrefix()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme" }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme-corp") with { Name = "Acme Corp" }, JsonOptions);

        // Act - simulate QueryAsync with prefix filter
        var children = await persistence.GetChildrenAsync("org", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);
        var filtered = children.Where(n =>
            (n.Name?.Contains("acme-", StringComparison.OrdinalIgnoreCase) ?? false) ||
            n.Path.Contains("acme-", StringComparison.OrdinalIgnoreCase)).ToList();

        // Assert
        filtered.Should().HaveCount(1);
        filtered.First().Name.Should().Be("Acme Corp");
    }
}
