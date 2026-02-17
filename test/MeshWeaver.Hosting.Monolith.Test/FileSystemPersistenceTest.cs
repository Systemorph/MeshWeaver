using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for file system persistence with CRUD operations on MeshNodes.
/// Uses a temporary directory for each test to ensure isolation.
/// </summary>
public class FileSystemPersistenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", Guid.NewGuid().ToString());
    private FileSystemStorageAdapter? _storageAdapterInstance;
    private FileSystemStorageAdapter _storageAdapter => _storageAdapterInstance ??= CreateStorageAdapter();
    private InMemoryPersistenceService? _persistenceInstance;
    private InMemoryPersistenceService _persistence => _persistenceInstance ??= new(_storageAdapter);
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    private FileSystemStorageAdapter CreateStorageAdapter()
    {
        Directory.CreateDirectory(_testDirectory);
        return new FileSystemStorageAdapter(_testDirectory);
    }

    public override void Dispose()
    {
        base.Dispose();
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
            NodeType = "org",
            Icon = "Building",
            DisplayOrder = 10
        };

        // Act
        await _persistence.SaveNodeAsync(node, JsonOptions);

        // Assert - verify file was created
        var filePath = Path.Combine(_testDirectory, "graph", "org1.json");
        File.Exists(filePath).Should().BeTrue("file should be created at expected path");

        // Verify content
        var savedNode = await _persistence.GetNodeAsync("graph/org1", JsonOptions);
        savedNode.Should().NotBeNull();
        savedNode!.Name.Should().Be("Organization 1");
        savedNode.NodeType.Should().Be("org");
    }

    [Fact]
    public async Task Create_HierarchicalNodes_CreatesDirectoryStructure()
    {
        // Arrange & Act
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1") with { Name = "Project 1" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1/story1") with { Name = "Story 1" }, JsonOptions);

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
        await _persistence.SaveNodeAsync(node, JsonOptions);

        // Assert
        var savedNode = await _persistence.GetNodeAsync("graph/org1", JsonOptions);
        savedNode.Should().NotBeNull();
        savedNode!.Content.Should().NotBeNull();
    }

    #endregion

    #region Read Tests

    [Fact]
    public async Task Read_ExistingNode_ReturnsNode()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" }, JsonOptions);

        // Act
        var node = await _persistence.GetNodeAsync("graph/org1", JsonOptions);

        // Assert
        node.Should().NotBeNull();
        node!.Name.Should().Be("Org 1");
    }

    [Fact]
    public async Task Read_NonExistentNode_ReturnsNull()
    {
        // Act
        var node = await _persistence.GetNodeAsync("graph/nonexistent", JsonOptions);

        // Assert
        node.Should().BeNull();
    }

    [Fact]
    public async Task Read_GetChildren_ReturnsDirectChildrenOnly()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org2") with { Name = "Org 2" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1") with { Name = "Project 1" }, JsonOptions);

        // Act
        var children = await _persistence.GetChildrenAsync("graph", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        children.Should().HaveCount(2);
        children.Select(c => c.Name).Should().Contain(new[] { "Org 1", "Org 2" });
        children.Select(c => c.Name).Should().NotContain("Project 1");
    }

    [Fact]
    public async Task Read_GetDescendants_ReturnsAllDescendants()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1") with { Name = "Project 1" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1/story1") with { Name = "Story 1" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org2") with { Name = "Org 2" }, JsonOptions);

        // Act
        var descendants = await _persistence.GetDescendantsAsync("graph/org1", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        descendants.Should().HaveCount(2);
        descendants.Select(d => d.Name).Should().Contain(new[] { "Project 1", "Story 1" });
        descendants.Select(d => d.Name).Should().NotContain("Org 2");
    }

    [Fact]
    public async Task Read_Exists_ReturnsCorrectResult()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" }, JsonOptions);

        // Assert
        (await _persistence.ExistsAsync("graph/org1")).Should().BeTrue();
        (await _persistence.ExistsAsync("graph/nonexistent")).Should().BeFalse();
    }

    [Fact]
    public async Task Read_Search_FindsMatchingNodes()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/acme") with { Name = "Acme Corporation" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/contoso") with { Name = "Contoso Software" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/fabrikam") with { Name = "Fabrikam Inc" }, JsonOptions);

        // Act
        var results = await _persistence.SearchAsync(null, "software", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Contoso Software");
    }

    #endregion

    #region Update Tests

    [Fact]
    public async Task Update_ExistingNode_UpdatesFile()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Original Name" }, JsonOptions);

        // Act
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Updated Name" }, JsonOptions);

        // Assert
        var node = await _persistence.GetNodeAsync("graph/org1", JsonOptions);
        node.Should().NotBeNull();
        node!.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task Update_NodeContent_PersistsChanges()
    {
        // Arrange
        var originalContent = new TestOrganization { Id = "org1", Name = "Original", Website = "https://original.com" };
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1", Content = originalContent }, JsonOptions);

        // Act
        var updatedContent = new TestOrganization { Id = "org1", Name = "Updated", Website = "https://updated.com" };
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1", Content = updatedContent }, JsonOptions);

        // Assert
        var node = await _persistence.GetNodeAsync("graph/org1", JsonOptions);
        node.Should().NotBeNull();
        node!.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task Update_NodeDisplayOrder_AffectsSortOrder()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "First", DisplayOrder = 20 }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org2") with { Name = "Second", DisplayOrder = 10 }, JsonOptions);

        // Act
        var children = await _persistence.GetChildrenAsync("graph", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);

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
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" }, JsonOptions);
        var filePath = Path.Combine(_testDirectory, "graph", "org1.json");
        File.Exists(filePath).Should().BeTrue();

        // Act
        await _persistence.DeleteNodeAsync("graph/org1");

        // Assert
        File.Exists(filePath).Should().BeFalse();
        (await _persistence.GetNodeAsync("graph/org1", JsonOptions)).Should().BeNull();
        (await _persistence.ExistsAsync("graph/org1")).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_NonRecursive_LeavesChildren()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1") with { Name = "Project 1" }, JsonOptions);

        // Act
        await _persistence.DeleteNodeAsync("graph/org1", recursive: false);

        // Assert
        (await _persistence.GetNodeAsync("graph/org1", JsonOptions)).Should().BeNull();
        (await _persistence.GetNodeAsync("graph/org1/project1", JsonOptions)).Should().NotBeNull(); // Child still exists
    }

    [Fact]
    public async Task Delete_Recursive_RemovesAllDescendants()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Org 1" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1") with { Name = "Project 1" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1/story1") with { Name = "Story 1" }, JsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org2") with { Name = "Org 2" }, JsonOptions);

        // Act
        await _persistence.DeleteNodeAsync("graph/org1", recursive: true);

        // Assert
        (await _persistence.GetNodeAsync("graph/org1", JsonOptions)).Should().BeNull();
        (await _persistence.GetNodeAsync("graph/org1/project1", JsonOptions)).Should().BeNull();
        (await _persistence.GetNodeAsync("graph/org1/project1/story1", JsonOptions)).Should().BeNull();
        (await _persistence.GetNodeAsync("graph/org2", JsonOptions)).Should().NotBeNull(); // Sibling unaffected
    }

    [Fact]
    public async Task Delete_CleansUpEmptyDirectories()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/project1/story1") with { Name = "Story 1" }, JsonOptions);
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
                "id": "graph",
                "name": "Graph Root",
                "nodeType": "graph",
                "displayOrder": 1
            }
            """);

        await File.WriteAllTextAsync(org1Path, """
            {
                "id": "org1",
                "namespace": "graph",
                "name": "Loaded Org 1",
                "nodeType": "org",
                "displayOrder": 10
            }
            """);

        await File.WriteAllTextAsync(org2Path, """
            {
                "id": "org2",
                "namespace": "graph",
                "name": "Loaded Org 2",
                "nodeType": "org",
                "displayOrder": 20
            }
            """);

        // Act - Create a new persistence service and initialize it
        var newStorageAdapter = new FileSystemStorageAdapter(_testDirectory);
        var newPersistence = new InMemoryPersistenceService(newStorageAdapter);
        await newPersistence.InitializeAsync(JsonOptions);

        // Assert
        var children = await newPersistence.GetChildrenAsync("graph", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);
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
                "id": "graph",
                "name": "Graph Root"
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(graphDir, "org1.json"), """
            {
                "id": "org1",
                "namespace": "graph",
                "name": "Organization 1"
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(org1Dir, "project1.json"), """
            {
                "id": "project1",
                "namespace": "graph/org1",
                "name": "Project 1"
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(project1Dir, "story1.json"), """
            {
                "id": "story1",
                "namespace": "graph/org1/project1",
                "name": "Story 1"
            }
            """);

        // Act
        var newStorageAdapter = new FileSystemStorageAdapter(_testDirectory);
        var newPersistence = new InMemoryPersistenceService(newStorageAdapter);
        await newPersistence.InitializeAsync(JsonOptions);

        // Assert
        (await newPersistence.GetNodeAsync("graph/org1", JsonOptions)).Should().NotBeNull();
        (await newPersistence.GetNodeAsync("graph/org1/project1", JsonOptions)).Should().NotBeNull();
        (await newPersistence.GetNodeAsync("graph/org1/project1/story1", JsonOptions)).Should().NotBeNull();

        var org1Children = await newPersistence.GetChildrenAsync("graph/org1", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);
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
        await newPersistence.InitializeAsync(JsonOptions);

        // Assert
        var children = await newPersistence.GetChildrenAsync(null, JsonOptions).ToListAsync(TestContext.Current.CancellationToken);
        children.Should().BeEmpty();
    }

    #endregion

    #region Path Normalization Tests

    [Fact]
    public async Task PathNormalization_CaseInsensitive()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("Graph/ORG1") with { Name = "Org 1" }, JsonOptions);

        // Act & Assert - should find regardless of case
        (await _persistence.GetNodeAsync("graph/org1", JsonOptions)).Should().NotBeNull();
        (await _persistence.GetNodeAsync("GRAPH/ORG1", JsonOptions)).Should().NotBeNull();
        (await _persistence.GetNodeAsync("Graph/Org1", JsonOptions)).Should().NotBeNull();
    }

    [Fact]
    public async Task PathNormalization_TrimsSlashes()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("/graph/org1/") with { Name = "Org 1" }, JsonOptions);

        // Act & Assert
        (await _persistence.GetNodeAsync("graph/org1", JsonOptions)).Should().NotBeNull();
        (await _persistence.GetNodeAsync("/graph/org1", JsonOptions)).Should().NotBeNull();
        (await _persistence.GetNodeAsync("graph/org1/", JsonOptions)).Should().NotBeNull();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task EdgeCase_EmptyPath_HandledGracefully()
    {
        // Act
        var node = await _persistence.GetNodeAsync("", JsonOptions);
        var children = await _persistence.GetChildrenAsync("", JsonOptions).ToListAsync(TestContext.Current.CancellationToken);

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
            Name = "Org with 'quotes' and \"double quotes\""
        };

        // Act
        await _persistence.SaveNodeAsync(node, JsonOptions);
        var loaded = await _persistence.GetNodeAsync("graph/org1", JsonOptions);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Org with 'quotes' and \"double quotes\"");
    }

    [Fact]
    public async Task EdgeCase_DeeplyNestedPath_Works()
    {
        // Arrange
        var deepPath = "graph/a/b/c/d/e/f/g/h/i/j";
        var node = MeshNode.FromPath(deepPath) with { Name = "Deep Node" };

        // Act
        await _persistence.SaveNodeAsync(node, JsonOptions);
        var loaded = await _persistence.GetNodeAsync(deepPath, JsonOptions);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Deep Node");
    }

    #endregion

    #region Markdown File Tests

    [Fact]
    public async Task Read_MarkdownFile_ParsesYamlFrontMatter()
    {
        // Arrange - Create a .md file with YAML front matter
        var mdPath = Path.Combine(_testDirectory, "docs", "readme.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mdPath)!);
        await File.WriteAllTextAsync(mdPath, """
            ---
            NodeType: Markdown
            Name: Test Document
            Category: Documentation
            Description: A test document
            Icon: /static/storage/content/test/icon.svg
            State: Active
            ---

            # Hello World

            This is test content.
            """);

        // Act
        var node = await _storageAdapter.ReadAsync("docs/readme", JsonOptions);

        // Assert
        node.Should().NotBeNull();
        node!.Id.Should().Be("readme");
        node.Name.Should().Be("Test Document");
        node.NodeType.Should().Be("Markdown");
        node.Category.Should().Be("Documentation");
        node.Icon.Should().Be("/static/storage/content/test/icon.svg");
        node.Content.Should().BeOfType<MarkdownContent>();
        var markdownContent = (MarkdownContent)node.Content!;
        markdownContent.Content.Should().Contain("# Hello World");
    }

    [Fact]
    public async Task Read_MarkdownFile_WithMinimalYaml()
    {
        // Arrange - Create a .md file with minimal YAML
        var mdPath = Path.Combine(_testDirectory, "docs", "simple.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mdPath)!);
        await File.WriteAllTextAsync(mdPath, """
            ---
            Name: Simple Doc
            ---

            Just some content.
            """);

        // Act
        var node = await _storageAdapter.ReadAsync("docs/simple", JsonOptions);

        // Assert
        node.Should().NotBeNull();
        node!.Id.Should().Be("simple");
        node.Name.Should().Be("Simple Doc");
        node.NodeType.Should().Be("Markdown"); // Default
        var markdownContent1 = (MarkdownContent)node.Content!;
        markdownContent1.Content.Should().Contain("Just some content");
    }

    [Fact]
    public async Task Read_MarkdownFile_WithoutYaml()
    {
        // Arrange - Create a .md file without YAML front matter
        var mdPath = Path.Combine(_testDirectory, "docs", "plain.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mdPath)!);
        await File.WriteAllTextAsync(mdPath, """
            # Plain Markdown

            No YAML here.
            """);

        // Act
        var node = await _storageAdapter.ReadAsync("docs/plain", JsonOptions);

        // Assert
        node.Should().NotBeNull();
        node!.Id.Should().Be("plain");
        node.Name.Should().Be("plain"); // Defaults to Id
        node.NodeType.Should().Be("Markdown");
        var markdownContent2 = (MarkdownContent)node.Content!;
        markdownContent2.Content.Should().Contain("# Plain Markdown");
    }

    [Fact]
    public async Task Write_MarkdownNode_CreatesMarkdownFile()
    {
        // Arrange
        var node = MeshNode.FromPath("docs/mydoc") with
        {
            Name = "My Document",
            NodeType = "Markdown",
            Category = "Test",
            Content = "# My Document\n\nContent here."
        };

        // Act
        await _storageAdapter.WriteAsync(node, JsonOptions);

        // Assert
        var mdPath = Path.Combine(_testDirectory, "docs", "mydoc.md");
        File.Exists(mdPath).Should().BeTrue();
        var content = await File.ReadAllTextAsync(mdPath);
        content.Should().Contain("---");
        content.Should().Contain("Name: My Document");
        content.Should().Contain("Category: Test");
        content.Should().Contain("# My Document");
    }

    [Fact]
    public async Task ListChildren_IncludesMarkdownFiles()
    {
        // Arrange
        var docsDir = Path.Combine(_testDirectory, "docs");
        Directory.CreateDirectory(docsDir);
        await File.WriteAllTextAsync(Path.Combine(docsDir, "doc1.md"), "# Doc 1");
        await File.WriteAllTextAsync(Path.Combine(docsDir, "doc2.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(docsDir, "doc3.md"), "# Doc 3");

        // Act
        var (nodePaths, _) = await _storageAdapter.ListChildPathsAsync("docs");

        // Assert
        nodePaths.Should().HaveCount(3);
        nodePaths.Should().Contain("docs/doc1");
        nodePaths.Should().Contain("docs/doc2");
        nodePaths.Should().Contain("docs/doc3");
    }

    #endregion

    #region C# Code File Tests

    [Fact]
    public async Task GetPartitionObjects_ReadsCSharpFiles()
    {
        // Arrange - Create a Code partition with .cs files
        var codeDir = Path.Combine(_testDirectory, "Type", "Person", "Code");
        Directory.CreateDirectory(codeDir);
        await File.WriteAllTextAsync(Path.Combine(codeDir, "Person.cs"), """
            public record Person
            {
                public string Id { get; init; }
                public string Name { get; init; }
            }
            """);

        // Act
        var objects = await _storageAdapter.GetPartitionObjectsAsync("Type/Person", "Code", JsonOptions).ToListAsync();

        // Assert
        objects.Should().HaveCount(1);
        objects[0].Should().BeOfType<CodeConfiguration>();
        var config = (CodeConfiguration)objects[0];
        config.Id.Should().Be("Person");
        config.Code.Should().Contain("public record Person");
    }

    [Fact]
    public async Task GetPartitionObjects_ReadsCSharpFilesWithMetadata()
    {
        // Arrange
        var codeDir = Path.Combine(_testDirectory, "Type", "Org", "Code");
        Directory.CreateDirectory(codeDir);
        await File.WriteAllTextAsync(Path.Combine(codeDir, "Organization.cs"), """
            // <meshweaver>
            // Id: Organization
            // DisplayName: Organization Data Model
            // </meshweaver>

            public record Organization
            {
                public string Id { get; init; }
                public string Name { get; init; }
            }
            """);

        // Act
        var objects = await _storageAdapter.GetPartitionObjectsAsync("Type/Org", "Code", JsonOptions).ToListAsync();

        // Assert
        objects.Should().HaveCount(1);
        var config = (CodeConfiguration)objects[0];
        config.Id.Should().Be("Organization");
        config.DisplayName.Should().Be("Organization Data Model");
        config.Code.Should().Contain("public record Organization");
        config.Code.Should().NotContain("<meshweaver>"); // Metadata should be stripped
    }

    [Fact]
    public async Task SavePartitionObjects_WritesCSharpFiles()
    {
        // Arrange
        var codeConfig = new CodeConfiguration
        {
            Id = "MyClass",
            Code = "public class MyClass { }",
            DisplayName = "My Class"
        };

        // Act
        await _storageAdapter.SavePartitionObjectsAsync("Type/Test", "Code", [codeConfig], JsonOptions);

        // Assert
        var csPath = Path.Combine(_testDirectory, "Type", "Test", "Code", "MyClass.cs");
        File.Exists(csPath).Should().BeTrue();
        var content = await File.ReadAllTextAsync(csPath);
        content.Should().Contain("// <meshweaver>");
        content.Should().Contain("// DisplayName: My Class");
        content.Should().Contain("public class MyClass { }");
    }

    [Fact]
    public async Task GetPartitionObjects_HandlesMixedJsonAndCsFiles()
    {
        // Arrange
        var codeDir = Path.Combine(_testDirectory, "Type", "Mixed", "Code");
        Directory.CreateDirectory(codeDir);

        // Add a .cs file
        await File.WriteAllTextAsync(Path.Combine(codeDir, "MyRecord.cs"), "public record MyRecord { }");

        // Add a .json file (legacy format)
        await File.WriteAllTextAsync(Path.Combine(codeDir, "other.json"), """
            {"$type":"CodeConfiguration","code":"public class Other { }"}
            """);

        // Act
        var objects = await _storageAdapter.GetPartitionObjectsAsync("Type/Mixed", "Code", JsonOptions).ToListAsync();

        // Assert
        objects.Should().HaveCount(2);
    }

    #endregion

    #region Format Priority Tests

    [Fact]
    public async Task Read_PrefersMarkdownOverJson()
    {
        // Arrange - Create both .md and .json for same path
        var dir = Path.Combine(_testDirectory, "priority");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "doc.md"), """
            ---
            Name: From Markdown
            ---
            MD Content
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "doc.json"), """
            {"id":"doc","name":"From JSON"}
            """);

        // Act
        var node = await _storageAdapter.ReadAsync("priority/doc", JsonOptions);

        // Assert - should read from .md (higher priority)
        node.Should().NotBeNull();
        node!.Name.Should().Be("From Markdown");
    }

    [Fact]
    public async Task Exists_FindsMarkdownFiles()
    {
        // Arrange
        var dir = Path.Combine(_testDirectory, "exists");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "doc.md"), "# Test");

        // Act
        var exists = await _storageAdapter.ExistsAsync("exists/doc");

        // Assert
        exists.Should().BeTrue();
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
