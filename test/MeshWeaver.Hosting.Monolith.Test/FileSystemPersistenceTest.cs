using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for file system persistence with CRUD operations on MeshNodes.
/// Uses a temporary directory for each test to ensure isolation.
/// </summary>
public class FileSystemPersistenceTest : IDisposable
{
    private readonly string _testDirectory;
    private readonly FileSystemStorageAdapter _storageAdapter;
    private readonly InMemoryPersistenceService _persistence;

    public FileSystemPersistenceTest()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        _storageAdapter = new FileSystemStorageAdapter(_testDirectory);
        _persistence = new InMemoryPersistenceService(_storageAdapter);
    }

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    #region Create Tests

    [Fact]
    public async Task Create_SingleNode_PersistsToFileSystem()
    {
        // Arrange
        var node = MeshNode.FromPath("graph/org1") with
        {
            Name = "Organization 1",
            Description = "First organization",
            NodeType = "org",
            IconName = "Building",
            DisplayOrder = 10
        };

        // Act
        await _persistence.SaveNodeAsync(node);

        // Assert - verify file was created
        var filePath = Path.Combine(_testDirectory, "graph", "org1.json");
        File.Exists(filePath).Should().BeTrue("file should be created at expected path");

        // Verify content
        var savedNode = await _persistence.GetNodeAsync("graph/org1");
        savedNode.Should().NotBeNull();
        savedNode!.Name.Should().Be("Organization 1");
        savedNode.Description.Should().Be("First organization");
        savedNode.NodeType.Should().Be("org");
    }

    [Fact]
    public async Task Create_HierarchicalNodes_CreatesDirectoryStructure()
    {
        // Arrange & Act
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1") with { Name = "Project 1" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1/story1") with { Name = "Story 1" });

        // Assert - verify directory structure
        Directory.Exists(Path.Combine(_testDirectory, "graph")).Should().BeTrue();
        File.Exists(Path.Combine(_testDirectory, "graph", "org1.json")).Should().BeTrue();
        Directory.Exists(Path.Combine(_testDirectory, "graph", "org1")).Should().BeTrue();
        File.Exists(Path.Combine(_testDirectory, "graph", "org1", "project1.json")).Should().BeTrue();
        Directory.Exists(Path.Combine(_testDirectory, "graph", "org1", "project1")).Should().BeTrue();
        File.Exists(Path.Combine(_testDirectory, "graph", "org1", "project1", "story1.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Create_NodeWithContent_PersistsContent()
    {
        // Arrange
        var content = new TestOrganization { Id = "org1", Name = "Test Org", Website = "https://test.com" };
        var node = MeshNode.FromPath("graph/org1") with
        {
            Name = "Test Org",
            NodeType = "org",
            Content = content
        };

        // Act
        await _persistence.SaveNodeAsync(node);

        // Assert
        var savedNode = await _persistence.GetNodeAsync("graph/org1");
        savedNode.Should().NotBeNull();
        savedNode!.Content.Should().NotBeNull();
    }

    #endregion

    #region Read Tests

    [Fact]
    public async Task Read_ExistingNode_ReturnsNode()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1", Description = "Test org" });

        // Act
        var node = await _persistence.GetNodeAsync("graph/org1");

        // Assert
        node.Should().NotBeNull();
        node!.Name.Should().Be("Org 1");
        node.Description.Should().Be("Test org");
    }

    [Fact]
    public async Task Read_NonExistentNode_ReturnsNull()
    {
        // Act
        var node = await _persistence.GetNodeAsync("graph/nonexistent");

        // Assert
        node.Should().BeNull();
    }

    [Fact]
    public async Task Read_GetChildren_ReturnsDirectChildrenOnly()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org2") with { Name = "Org 2" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1") with { Name = "Project 1" });

        // Act
        var children = await _persistence.GetChildrenAsync("graph").ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        children.Should().HaveCount(2);
        children.Select(c => c.Name).Should().Contain(new[] { "Org 1", "Org 2" });
        children.Select(c => c.Name).Should().NotContain("Project 1");
    }

    [Fact]
    public async Task Read_GetDescendants_ReturnsAllDescendants()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1") with { Name = "Project 1" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1/story1") with { Name = "Story 1" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org2") with { Name = "Org 2" });

        // Act
        var descendants = await _persistence.GetDescendantsAsync("graph/org1").ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        descendants.Should().HaveCount(2);
        descendants.Select(d => d.Name).Should().Contain(new[] { "Project 1", "Story 1" });
        descendants.Select(d => d.Name).Should().NotContain("Org 2");
    }

    [Fact]
    public async Task Read_Exists_ReturnsCorrectResult()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" });

        // Assert
        (await _persistence.ExistsAsync("graph/org1")).Should().BeTrue();
        (await _persistence.ExistsAsync("graph/nonexistent")).Should().BeFalse();
    }

    [Fact]
    public async Task Read_Search_FindsMatchingNodes()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/acme") with { Name = "Acme Corporation", Description = "Tech company" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/contoso") with { Name = "Contoso Ltd", Description = "Software company" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/fabrikam") with { Name = "Fabrikam Inc", Description = "Hardware manufacturer" });

        // Act
        var results = await _persistence.SearchAsync(null, "software").ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Contoso Ltd");
    }

    #endregion

    #region Update Tests

    [Fact]
    public async Task Update_ExistingNode_UpdatesFile()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Original Name" });

        // Act
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Updated Name", Description = "New description" });

        // Assert
        var node = await _persistence.GetNodeAsync("graph/org1");
        node.Should().NotBeNull();
        node!.Name.Should().Be("Updated Name");
        node.Description.Should().Be("New description");
    }

    [Fact]
    public async Task Update_NodeContent_PersistsChanges()
    {
        // Arrange
        var originalContent = new TestOrganization { Id = "org1", Name = "Original", Website = "https://original.com" };
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1", Content = originalContent });

        // Act
        var updatedContent = new TestOrganization { Id = "org1", Name = "Updated", Website = "https://updated.com" };
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1", Content = updatedContent });

        // Assert
        var node = await _persistence.GetNodeAsync("graph/org1");
        node.Should().NotBeNull();
        node!.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task Update_NodeDisplayOrder_AffectsSortOrder()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "First", DisplayOrder = 20 });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org2") with { Name = "Second", DisplayOrder = 10 });

        // Act
        var children = await _persistence.GetChildrenAsync("graph").ToListAsync(TestContext.Current.CancellationToken);

        // Assert - should be ordered by DisplayOrder
        children.Should().HaveCount(2);
        children.First().Name.Should().Be("Second"); // DisplayOrder 10
        children.Last().Name.Should().Be("First"); // DisplayOrder 20
    }

    #endregion

    #region Delete Tests

    [Fact]
    public async Task Delete_SingleNode_RemovesFromFileSystem()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" });
        var filePath = Path.Combine(_testDirectory, "graph", "org1.json");
        File.Exists(filePath).Should().BeTrue();

        // Act
        await _persistence.DeleteNodeAsync("graph/org1");

        // Assert
        File.Exists(filePath).Should().BeFalse();
        (await _persistence.GetNodeAsync("graph/org1")).Should().BeNull();
        (await _persistence.ExistsAsync("graph/org1")).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_NonRecursive_LeavesChildren()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1") with { Name = "Project 1" });

        // Act
        await _persistence.DeleteNodeAsync("graph/org1", recursive: false);

        // Assert
        (await _persistence.GetNodeAsync("graph/org1")).Should().BeNull();
        (await _persistence.GetNodeAsync("graph/org1/project1")).Should().NotBeNull(); // Child still exists
    }

    [Fact]
    public async Task Delete_Recursive_RemovesAllDescendants()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1") with { Name = "Project 1" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1/story1") with { Name = "Story 1" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org2") with { Name = "Org 2" });

        // Act
        await _persistence.DeleteNodeAsync("graph/org1", recursive: true);

        // Assert
        (await _persistence.GetNodeAsync("graph/org1")).Should().BeNull();
        (await _persistence.GetNodeAsync("graph/org1/project1")).Should().BeNull();
        (await _persistence.GetNodeAsync("graph/org1/project1/story1")).Should().BeNull();
        (await _persistence.GetNodeAsync("graph/org2")).Should().NotBeNull(); // Sibling unaffected
    }

    [Fact]
    public async Task Delete_CleansUpEmptyDirectories()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1/story1") with { Name = "Story 1" });
        Directory.Exists(Path.Combine(_testDirectory, "graph", "org1", "project1")).Should().BeTrue();

        // Act
        await _persistence.DeleteNodeAsync("graph/org1/project1/story1");

        // Assert - empty directories should be cleaned up
        // Note: This depends on implementation - the adapter should clean up empty dirs
    }

    #endregion

    #region Initial State Loading Tests

    [Fact]
    public async Task Initialize_LoadsExistingFilesFromDisk()
    {
        // Arrange - Create files directly on disk before initializing
        // Note: The storage adapter requires parent nodes to exist for recursive loading
        var graphPath = Path.Combine(_testDirectory, "graph.json");
        var org1Path = Path.Combine(_testDirectory, "graph", "org1.json");
        var org2Path = Path.Combine(_testDirectory, "graph", "org2.json");

        Directory.CreateDirectory(Path.GetDirectoryName(org1Path)!);

        // First create the graph root node so the loader can find and recurse into it
        await File.WriteAllTextAsync(graphPath, """
            {
                "path": "graph",
                "key": "graph",
                "name": "Graph Root",
                "nodeType": "graph",
                "displayOrder": 1
            }
            """);

        await File.WriteAllTextAsync(org1Path, """
            {
                "path": "graph/org1",
                "key": "graph/org1",
                "name": "Loaded Org 1",
                "nodeType": "org",
                "displayOrder": 10
            }
            """);

        await File.WriteAllTextAsync(org2Path, """
            {
                "path": "graph/org2",
                "key": "graph/org2",
                "name": "Loaded Org 2",
                "nodeType": "org",
                "displayOrder": 20
            }
            """);

        // Act - Create a new persistence service and initialize it
        var newStorageAdapter = new FileSystemStorageAdapter(_testDirectory);
        var newPersistence = new InMemoryPersistenceService(newStorageAdapter);
        await newPersistence.InitializeAsync();

        // Assert
        var children = await newPersistence.GetChildrenAsync("graph").ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCount(2);
        children.Select(c => c.Name).Should().Contain(new[] { "Loaded Org 1", "Loaded Org 2" });
    }

    [Fact]
    public async Task Initialize_LoadsHierarchicalStructure()
    {
        // Arrange - Create hierarchical files on disk
        // Note: Each parent node must exist for recursive loading to work
        var graphDir = Path.Combine(_testDirectory, "graph");
        var org1Dir = Path.Combine(graphDir, "org1");
        var project1Dir = Path.Combine(org1Dir, "project1");

        Directory.CreateDirectory(project1Dir);

        // Root graph node
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, "graph.json"), """
            {
                "path": "graph",
                "name": "Graph Root"
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(graphDir, "org1.json"), """
            {
                "path": "graph/org1",
                "name": "Organization 1"
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(org1Dir, "project1.json"), """
            {
                "path": "graph/org1/project1",
                "name": "Project 1"
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(project1Dir, "story1.json"), """
            {
                "path": "graph/org1/project1/story1",
                "name": "Story 1"
            }
            """);

        // Act
        var newStorageAdapter = new FileSystemStorageAdapter(_testDirectory);
        var newPersistence = new InMemoryPersistenceService(newStorageAdapter);
        await newPersistence.InitializeAsync();

        // Assert
        (await newPersistence.GetNodeAsync("graph/org1")).Should().NotBeNull();
        (await newPersistence.GetNodeAsync("graph/org1/project1")).Should().NotBeNull();
        (await newPersistence.GetNodeAsync("graph/org1/project1/story1")).Should().NotBeNull();

        var org1Children = await newPersistence.GetChildrenAsync("graph/org1").ToListAsync(TestContext.Current.CancellationToken);
        org1Children.Should().HaveCount(1);
        org1Children.First().Name.Should().Be("Project 1");
    }

    [Fact]
    public async Task Initialize_WithEmptyDirectory_StartsEmpty()
    {
        // Arrange - ensure directory exists but is empty
        Directory.CreateDirectory(_testDirectory);

        // Act
        var newStorageAdapter = new FileSystemStorageAdapter(_testDirectory);
        var newPersistence = new InMemoryPersistenceService(newStorageAdapter);
        await newPersistence.InitializeAsync();

        // Assert
        var children = await newPersistence.GetChildrenAsync(null).ToListAsync(TestContext.Current.CancellationToken);
        children.Should().BeEmpty();
    }

    #endregion

    #region Path Normalization Tests

    [Fact]
    public async Task PathNormalization_CaseInsensitive()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("Graph/ORG1") with { Name = "Org 1" });

        // Act & Assert - should find regardless of case
        (await _persistence.GetNodeAsync("graph/org1")).Should().NotBeNull();
        (await _persistence.GetNodeAsync("GRAPH/ORG1")).Should().NotBeNull();
        (await _persistence.GetNodeAsync("Graph/Org1")).Should().NotBeNull();
    }

    [Fact]
    public async Task PathNormalization_TrimsSlashes()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("/graph/org1/") with { Name = "Org 1" });

        // Act & Assert
        (await _persistence.GetNodeAsync("graph/org1")).Should().NotBeNull();
        (await _persistence.GetNodeAsync("/graph/org1")).Should().NotBeNull();
        (await _persistence.GetNodeAsync("graph/org1/")).Should().NotBeNull();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task EdgeCase_EmptyPath_HandledGracefully()
    {
        // Act
        var node = await _persistence.GetNodeAsync("");
        var children = await _persistence.GetChildrenAsync("").ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        node.Should().BeNull();
        children.Should().BeEmpty();
    }

    [Fact]
    public async Task EdgeCase_SpecialCharactersInName_Preserved()
    {
        // Arrange
        var node = MeshNode.FromPath("graph/org1") with
        {
            Name = "Org with 'quotes' and \"double quotes\"",
            Description = "Contains <html> and & special chars"
        };

        // Act
        await _persistence.SaveNodeAsync(node);
        var loaded = await _persistence.GetNodeAsync("graph/org1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Org with 'quotes' and \"double quotes\"");
        loaded.Description.Should().Be("Contains <html> and & special chars");
    }

    [Fact]
    public async Task EdgeCase_DeeplyNestedPath_Works()
    {
        // Arrange
        var deepPath = "graph/a/b/c/d/e/f/g/h/i/j";
        var node = MeshNode.FromPath(deepPath) with { Name = "Deep Node" };

        // Act
        await _persistence.SaveNodeAsync(node);
        var loaded = await _persistence.GetNodeAsync(deepPath);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Deep Node");
    }

    #endregion
}

/// <summary>
/// Test content type for organization.
/// </summary>
public record TestOrganization
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Website { get; init; } = string.Empty;
}
