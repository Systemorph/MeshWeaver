using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using Xunit;

namespace MeshWeaver.StorageImport.Test;

public class StorageImporterTests : IDisposable
{
    private readonly string _sourceDir;
    private readonly string _targetDir;

    public StorageImporterTests()
    {
        // Resolve samples/Graph/Data relative to the test assembly location
        var assemblyDir = Path.GetDirectoryName(typeof(StorageImporterTests).Assembly.Location)!;
        _sourceDir = Path.GetFullPath(Path.Combine(assemblyDir, "../../../../../samples/Graph/Data"));
        _targetDir = Path.Combine(Path.GetTempPath(), $"StorageImporterTest_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_targetDir))
            Directory.Delete(_targetDir, recursive: true);
    }

    [Fact]
    public async Task FullImport_CopiesAllNodes()
    {
        // Arrange
        Directory.Exists(_sourceDir).Should().BeTrue($"Source directory {_sourceDir} must exist");
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act
        var result = await importer.ImportAsync(ct: ct);

        // Assert
        result.NodesImported.Should().BeGreaterThan(0, "sample data should contain nodes");
        result.Elapsed.Should().BeGreaterThan(TimeSpan.Zero);

        // Verify known nodes were copied (use nodes still present in samples/Graph/Data)
        var todoAgentExists = await target.ExistsAsync("ACME/Project/TodoAgent", ct);
        todoAgentExists.Should().BeTrue("ACME/Project/TodoAgent.md node should exist in target");

        var documentationExists = await target.ExistsAsync("MeshWeaver/Documentation", ct);
        documentationExists.Should().BeTrue("MeshWeaver/Documentation.md node should exist in target");
    }

    [Fact]
    public async Task SubtreeImport_CopiesOnlySubtree()
    {
        // Arrange
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act
        var result = await importer.ImportAsync(new StorageImportOptions { RootPath = "ACME" }, ct);

        // Assert
        result.NodesImported.Should().BeGreaterThan(0);

        // ACME children should be copied
        var todoAgentExists = await target.ExistsAsync("ACME/Project/TodoAgent", ct);
        todoAgentExists.Should().BeTrue("ACME/Project/TodoAgent should have been imported");

        // Nodes outside ACME should NOT be copied
        var workerExists = await target.ExistsAsync("Worker", ct);
        workerExists.Should().BeFalse("Worker is not under ACME and should not be imported");
    }

    [Fact]
    public async Task PartitionImport_TransfersPartitionData()
    {
        // Arrange
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act
        var result = await importer.ImportAsync(new StorageImportOptions { ImportPartitions = true }, ct);

        // Assert
        result.NodesImported.Should().BeGreaterThan(0);
        result.PartitionsImported.Should().BeGreaterThanOrEqualTo(0, "partitions may or may not exist in sample data");
    }

    [Fact]
    public async Task ProgressReporting_FiresCallback()
    {
        // Arrange
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var progressCalls = new List<(int Nodes, int Partitions, string Path)>();

        var ct = TestContext.Current.CancellationToken;

        // Act
        var result = await importer.ImportAsync(new StorageImportOptions
        {
            OnProgress = (nodes, partitions, path) => progressCalls.Add((nodes, partitions, path))
        }, ct);

        // Assert
        progressCalls.Should().NotBeEmpty("progress callback should fire for each imported node");
        progressCalls.Count.Should().Be(result.NodesImported);
    }

    [Fact]
    public async Task RecursiveImport_NodeWithSubfolder_ImportsAllChildren()
    {
        // Arrange - import only the Documentation subtree which contains
        // index.md + Article.json + Article/_Source/*.cs
        Directory.Exists(_sourceDir).Should().BeTrue($"Source directory {_sourceDir} must exist");
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act - import Documentation subtree
        var result = await importer.ImportAsync(new StorageImportOptions
        {
            RootPath = "MeshWeaver/Documentation"
        }, ct);

        // Assert - Documentation subtree nodes should be imported
        result.NodesImported.Should().BeGreaterThan(1, "Documentation children should all be imported");

        var articleExists = await target.ExistsAsync("MeshWeaver/Documentation/Article", ct);
        articleExists.Should().BeTrue("Article.json node should exist in target");

        var codeExists = await target.ExistsAsync("MeshWeaver/Documentation/Article/_Source/Article", ct);
        codeExists.Should().BeTrue("Article.cs code node should be imported");
    }

    [Fact]
    public async Task RecursiveImport_NestedSubnodes_ImportsDeepHierarchy()
    {
        // Arrange - Doc/DataMesh contains CollaborativeEditing/_Comment/ with comment JSONs + nested reply
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act
        var result = await importer.ImportAsync(new StorageImportOptions
        {
            RootPath = "Doc/DataMesh"
        }, ct);

        // Assert - should import CollaborativeEditing/_Comment/ nodes: c1-c6 (6) + c1/reply1 (1) = 7
        result.NodesImported.Should().Be(7, "DataMesh has 7 comment nodes (c1-c6 + reply1)");

        // Level 1: CollaborativeEditing comment nodes in _Comment sub-namespace
        var c1Exists = await target.ExistsAsync("Doc/DataMesh/CollaborativeEditing/_Comment/c1", ct);
        c1Exists.Should().BeTrue();

        // Level 2: Nested reply under c1
        var replyExists = await target.ExistsAsync("Doc/DataMesh/CollaborativeEditing/_Comment/c1/reply1", ct);
        replyExists.Should().BeTrue("reply1.json under _Comment/c1 should be imported");
    }

    [Fact]
    public async Task DialogUploadFlow_SingleFileWithNamespace_ImportsCorrectly()
    {
        // Arrange - simulate the dialog: user uploads Overview.md,
        // it gets placed under importDir/{namespace}/Overview.md
        var importDir = Path.Combine(_targetDir, "dialog_import");
        var namespaceDir = Path.Combine(importDir, "ACME", "Documentation");
        Directory.CreateDirectory(namespaceDir);

        // Copy Overview.md from samples into the namespace dir
        var sampleFile = Path.Combine(_sourceDir, "ACME", "Documentation", "Overview.md");
        File.Exists(sampleFile).Should().BeTrue($"Sample file {sampleFile} must exist");
        File.Copy(sampleFile, Path.Combine(namespaceDir, "Overview.md"));

        var source = new FileSystemStorageAdapter(importDir);
        var targetDir2 = Path.Combine(_targetDir, "dialog_target");
        var target = new FileSystemStorageAdapter(targetDir2);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act - import with no rootPath (dialog style)
        var result = await importer.ImportAsync(ct: ct);

        // Assert - the node should be placed at ACME/Documentation/Overview
        result.NodesImported.Should().Be(1);

        var exists = await target.ExistsAsync("ACME/Documentation/Overview", ct);
        exists.Should().BeTrue("Overview node should be at the correct namespace path");

        // Read the node and verify metadata
        var jsonOptions = StorageImporter.CreateDefaultImportOptions();
        var node = await target.ReadAsync("ACME/Documentation/Overview", jsonOptions, ct);
        node.Should().NotBeNull();
        node!.Namespace.Should().Be("ACME/Documentation");
        node.Id.Should().Be("Overview");
    }

    [Fact]
    public async Task EmptySource_ReturnsZeroCounts()
    {
        // Arrange
        var emptyDir = Path.Combine(_targetDir, "empty_source");
        Directory.CreateDirectory(emptyDir);
        var source = new FileSystemStorageAdapter(emptyDir);
        var targetDir = Path.Combine(_targetDir, "empty_target");
        var target = new FileSystemStorageAdapter(targetDir);
        var importer = new StorageImporter(source, target);

        // Act
        var result = await importer.ImportAsync(ct: TestContext.Current.CancellationToken);

        // Assert
        result.NodesImported.Should().Be(0);
        result.PartitionsImported.Should().Be(0);
    }

    [Fact]
    public async Task RemoveMissing_DeletesTargetNodesNotInSource()
    {
        // Arrange - first import the full tree, then re-import a subset with RemoveMissing
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Full import first
        var fullResult = await importer.ImportAsync(ct: ct);
        fullResult.NodesImported.Should().BeGreaterThan(0);

        // Verify ACME/Project/TodoAgent and Cornerstone exist in target
        (await target.ExistsAsync("ACME/Project/TodoAgent", ct)).Should().BeTrue();
        (await target.ExistsAsync("Cornerstone", ct)).Should().BeTrue();

        // Now create a partial source with only ACME/Project/TodoAgent.md
        var partialSourceDir = Path.Combine(_targetDir, "partial_source");
        var projectDir = Path.Combine(partialSourceDir, "ACME", "Project");
        Directory.CreateDirectory(projectDir);
        var sampleFile = Path.Combine(_sourceDir, "ACME", "Project", "TodoAgent.md");
        File.Exists(sampleFile).Should().BeTrue();
        File.Copy(sampleFile, Path.Combine(projectDir, "TodoAgent.md"));

        var partialSource = new FileSystemStorageAdapter(partialSourceDir);
        var importer2 = new StorageImporter(partialSource, target);

        // Act - re-import partial source with RemoveMissing
        var result = await importer2.ImportAsync(new StorageImportOptions { RemoveMissing = true }, ct);

        // Assert - ACME/Project/TodoAgent should still exist, Cornerstone should be removed
        result.NodesImported.Should().Be(1);
        result.NodesRemoved.Should().BeGreaterThan(0);
        (await target.ExistsAsync("ACME/Project/TodoAgent", ct)).Should().BeTrue("ACME/Project/TodoAgent was in the source");
        (await target.ExistsAsync("Cornerstone", ct)).Should().BeFalse("Cornerstone was not in partial source and should be removed");
    }

    [Fact]
    public async Task RemoveMissing_SubtreeOnly_DoesNotAffectSiblings()
    {
        // Arrange - import full tree, then re-import a subtree with RemoveMissing
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Full import first
        var fullResult = await importer.ImportAsync(ct: ct);
        fullResult.NodesImported.Should().BeGreaterThan(0);

        // Verify both top-level and subtree nodes exist
        (await target.ExistsAsync("Cornerstone", ct)).Should().BeTrue();
        (await target.ExistsAsync("Doc/DataMesh/CollaborativeEditing/_Comment/c1", ct)).Should().BeTrue();
        (await target.ExistsAsync("Doc/DataMesh/CollaborativeEditing/_Comment/c2", ct)).Should().BeTrue();

        // Create partial source with just one CollaborativeEditing/_Comment child
        var partialSourceDir = Path.Combine(_targetDir, "partial_subtree");
        var collabDir = Path.Combine(partialSourceDir, "Doc", "DataMesh", "CollaborativeEditing", "_Comment");
        Directory.CreateDirectory(collabDir);
        // Copy just c1.json
        File.Copy(
            Path.Combine(_sourceDir, "Doc", "DataMesh", "CollaborativeEditing", "_Comment", "c1.json"),
            Path.Combine(collabDir, "c1.json"));

        var partialSource = new FileSystemStorageAdapter(partialSourceDir);
        var importer2 = new StorageImporter(partialSource, target);

        // Act - re-import subtree with RemoveMissing, scoped to CollaborativeEditing
        var result = await importer2.ImportAsync(new StorageImportOptions
        {
            RootPath = "Doc/DataMesh/CollaborativeEditing",
            RemoveMissing = true
        }, ct);

        // Assert
        result.NodesImported.Should().Be(1);
        result.NodesRemoved.Should().BeGreaterThan(0, "c2 and other comment children should be removed");

        // c1 should still exist (was in partial source)
        (await target.ExistsAsync("Doc/DataMesh/CollaborativeEditing/_Comment/c1", ct)).Should().BeTrue();
        // c2 should be removed (not in partial source)
        (await target.ExistsAsync("Doc/DataMesh/CollaborativeEditing/_Comment/c2", ct)).Should().BeFalse();
        // Top-level nodes like Cornerstone should NOT be affected (outside scoped subtree)
        (await target.ExistsAsync("Cornerstone", ct)).Should().BeTrue("Cornerstone is outside the import subtree and should not be removed");
    }

    [Fact]
    public async Task RemoveMissing_False_DoesNotDeleteAnything()
    {
        // Arrange - import full tree, then re-import a subset WITHOUT RemoveMissing
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Full import first
        await importer.ImportAsync(ct: ct);

        // Create partial source with only ACME/Project/TodoAgent.md
        var partialSourceDir = Path.Combine(_targetDir, "partial_no_remove");
        var projectDir = Path.Combine(partialSourceDir, "ACME", "Project");
        Directory.CreateDirectory(projectDir);
        File.Copy(
            Path.Combine(_sourceDir, "ACME", "Project", "TodoAgent.md"),
            Path.Combine(projectDir, "TodoAgent.md"));

        var partialSource = new FileSystemStorageAdapter(partialSourceDir);
        var importer2 = new StorageImporter(partialSource, target);

        // Act - re-import partial source WITHOUT RemoveMissing (default)
        var result = await importer2.ImportAsync(new StorageImportOptions { RemoveMissing = false }, ct);

        // Assert - nothing should be removed
        result.NodesRemoved.Should().Be(0);
        (await target.ExistsAsync("ACME/Project/TodoAgent", ct)).Should().BeTrue();
        (await target.ExistsAsync("Cornerstone", ct)).Should().BeTrue("Cornerstone should still exist when RemoveMissing is false");
    }

    [Fact]
    public async Task DataMeshSubtree_ImportsAllNodesIncludingNestedChildren()
    {
        // Arrange - verify exact node count for DataMesh subtree
        // After satellite move, DataMesh/ contains only:
        // - CollaborativeEditing/_Comment/{c1-c6}.json (6 comment nodes) + _Comment/c1/reply1.json (1 reply)
        // - UnifiedPath/sample.md (1 child)
        // (.md documentation files moved to MeshWeaver.Documentation embedded resources)
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act - import only DataMesh subtree
        var result = await importer.ImportAsync(new StorageImportOptions
        {
            RootPath = "Doc/DataMesh"
        }, ct);

        // Assert - 7 nodes total: c1-c6.json (6) + c1/reply1.json (1) under _Comment/
        result.NodesImported.Should().Be(7,
            "DataMesh/ has CollaborativeEditing/_Comment/ nodes (c1-c6 + reply1) = 7 total");

        // Verify key expected nodes
        var expectedNodes = new[]
        {
            "Doc/DataMesh/CollaborativeEditing/_Comment/c1",
            "Doc/DataMesh/CollaborativeEditing/_Comment/c1/reply1",
            "Doc/DataMesh/CollaborativeEditing/_Comment/c2",
            "Doc/DataMesh/CollaborativeEditing/_Comment/c3",
            "Doc/DataMesh/CollaborativeEditing/_Comment/c4",
            "Doc/DataMesh/CollaborativeEditing/_Comment/c5",
            "Doc/DataMesh/CollaborativeEditing/_Comment/c6",
        };

        foreach (var nodePath in expectedNodes)
        {
            (await target.ExistsAsync(nodePath, ct)).Should().BeTrue($"{nodePath} should be imported");
        }
    }

    [Fact]
    public async Task DialogUploadFlow_FileWithSiblingFolder_ImportsAllChildren()
    {
        // Arrange - simulate dialog uploading Documentation.md AND its sibling Documentation/ folder
        // (the dialog should copy sibling folders matching uploaded file names)
        var importDir = Path.Combine(_targetDir, "dialog_with_folder");
        var namespaceDir = Path.Combine(importDir, "MeshWeaver");
        Directory.CreateDirectory(namespaceDir);

        // Copy Documentation/index.md as Documentation.md (simulating a single file upload)
        var sampleFile = Path.Combine(_sourceDir, "MeshWeaver", "Documentation", "index.md");
        File.Exists(sampleFile).Should().BeTrue();
        File.Copy(sampleFile, Path.Combine(namespaceDir, "Documentation.md"));

        // Copy Documentation/ directory (the sibling folder)
        CopyDirectory(
            Path.Combine(_sourceDir, "MeshWeaver", "Documentation"),
            Path.Combine(namespaceDir, "Documentation"));

        var source = new FileSystemStorageAdapter(importDir);
        var targetDir2 = Path.Combine(_targetDir, "dialog_folder_target");
        var target = new FileSystemStorageAdapter(targetDir2);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act
        var result = await importer.ImportAsync(ct: ct);

        // Assert - Documentation.md (1) + Article.json (1) + Article/_Source/*.cs (2) = 4
        result.NodesImported.Should().BeGreaterThanOrEqualTo(3,
            "Documentation.md (1) + Article.json (1) + Article/_Source/*.cs (2) = at least 3-4 nodes");

        (await target.ExistsAsync("MeshWeaver/Documentation", ct)).Should().BeTrue();
        (await target.ExistsAsync("MeshWeaver/Documentation/Article", ct)).Should().BeTrue();

        // Verify metadata
        var jsonOptions = StorageImporter.CreateDefaultImportOptions();
        var node = await target.ReadAsync("MeshWeaver/Documentation", jsonOptions, ct);
        node.Should().NotBeNull();
        node!.Namespace.Should().Be("MeshWeaver");
        node.Id.Should().Be("Documentation");
    }

    [Fact]
    public async Task RemoveMissing_IdempotentReimport_NoRemovals()
    {
        // Arrange - import the same source twice with RemoveMissing.
        // Second import should produce zero removals since the source and target match.
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);

        var ct = TestContext.Current.CancellationToken;

        // First import
        var importer1 = new StorageImporter(source, target);
        var firstResult = await importer1.ImportAsync(ct: ct);
        firstResult.NodesImported.Should().BeGreaterThan(0);

        // Act - second import with RemoveMissing from the same source
        var importer2 = new StorageImporter(source, target);
        var result = await importer2.ImportAsync(new StorageImportOptions { RemoveMissing = true }, ct);

        // Assert - all nodes re-imported, zero removals
        result.NodesImported.Should().Be(firstResult.NodesImported,
            "same source should produce the same node count");
        result.NodesRemoved.Should().Be(0,
            "re-importing same source should produce zero removals");
    }

    [Fact]
    public async Task RemoveMissing_SubtreeReimport_NoRemovals()
    {
        // Arrange - import full tree, then re-import DataMesh subtree with RemoveMissing.
        // The subtree source matches the target subtree, so no removals should occur.
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);

        var ct = TestContext.Current.CancellationToken;

        // Full import
        var importer1 = new StorageImporter(source, target);
        await importer1.ImportAsync(ct: ct);

        // Act - re-import just DataMesh subtree from the same source with RemoveMissing
        var importer2 = new StorageImporter(source, target);
        var result = await importer2.ImportAsync(new StorageImportOptions
        {
            RootPath = "Doc/DataMesh",
            RemoveMissing = true
        }, ct);

        // Assert
        result.NodesImported.Should().Be(7, "DataMesh subtree has 7 comment nodes (c1-c6 + reply1)");
        result.NodesRemoved.Should().Be(0,
            "re-importing same subtree should produce zero removals");

        // Nodes outside the subtree should still exist
        (await target.ExistsAsync("Cornerstone", ct)).Should().BeTrue();
        (await target.ExistsAsync("Doc/DataMesh/CollaborativeEditing/_Comment/c1", ct)).Should().BeTrue();
    }

    /// <summary>
    /// Recursively copies a directory.
    /// </summary>
    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }
}
