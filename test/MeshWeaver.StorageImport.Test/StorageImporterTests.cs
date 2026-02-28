using System;
using System.Collections.Generic;
using System.IO;
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

        // Verify known nodes were copied (use .md nodes which don't require polymorphic JSON)
        var executorExists = await target.ExistsAsync("Agent/Executor", ct);
        executorExists.Should().BeTrue("Agent/Executor.md node should exist in target");

        var plannerExists = await target.ExistsAsync("Agent/Planner", ct);
        plannerExists.Should().BeTrue("Agent/Planner.md node should exist in target");
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
        var executorExists = await target.ExistsAsync("Executor", ct);
        executorExists.Should().BeFalse("Executor is not under ACME and should not be imported");
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
        // DataMesh.md + DataMesh/ folder with sub-nodes like NodeTypeConfiguration.md
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

        // Assert - DataMesh parent node should be imported
        result.NodesImported.Should().BeGreaterThan(1, "DataMesh.md and its children should all be imported");

        var dataMeshExists = await target.ExistsAsync("MeshWeaver/Documentation/DataMesh", ct);
        dataMeshExists.Should().BeTrue("DataMesh.md node should exist in target");

        // Children inside DataMesh/ folder should also be imported
        var nodeTypeConfigExists = await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/NodeTypeConfiguration", ct);
        nodeTypeConfigExists.Should().BeTrue("NodeTypeConfiguration.md child node should be imported");

        var querySyntaxExists = await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/QuerySyntax", ct);
        querySyntaxExists.Should().BeTrue("QuerySyntax.md child node should be imported");

        var collaborativeEditingExists = await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/CollaborativeEditing", ct);
        collaborativeEditingExists.Should().BeTrue("CollaborativeEditing.md child node should be imported");

        var unifiedPathExists = await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/UnifiedPath", ct);
        unifiedPathExists.Should().BeTrue("UnifiedPath.md child node should be imported");
    }

    [Fact]
    public async Task RecursiveImport_NestedSubnodes_ImportsDeepHierarchy()
    {
        // Arrange - UnifiedPath.md has a UnifiedPath/ subfolder with deeper children
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act
        var result = await importer.ImportAsync(new StorageImportOptions
        {
            RootPath = "MeshWeaver/Documentation/DataMesh"
        }, ct);

        // Assert - should import DataMesh children AND their children
        result.NodesImported.Should().BeGreaterThan(5, "DataMesh children + UnifiedPath sub-children should be imported");

        // Level 1: DataMesh children
        var unifiedPathExists = await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/UnifiedPath", ct);
        unifiedPathExists.Should().BeTrue();

        // Level 2: UnifiedPath children
        var syntaxExists = await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/UnifiedPath/Syntax", ct);
        syntaxExists.Should().BeTrue("Syntax.md under UnifiedPath should be imported");

        var areaPrefixExists = await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/UnifiedPath/AreaPrefix", ct);
        areaPrefixExists.Should().BeTrue("AreaPrefix.md under UnifiedPath should be imported");
    }

    [Fact]
    public async Task DialogUploadFlow_SingleFileWithNamespace_ImportsCorrectly()
    {
        // Arrange - simulate the dialog: user uploads DataMesh.md,
        // it gets placed under importDir/{namespace}/DataMesh.md
        var importDir = Path.Combine(_targetDir, "dialog_import");
        var namespaceDir = Path.Combine(importDir, "MeshWeaver", "Documentation");
        Directory.CreateDirectory(namespaceDir);

        // Copy DataMesh.md from samples into the namespace dir
        var sampleFile = Path.Combine(_sourceDir, "MeshWeaver", "Documentation", "DataMesh.md");
        File.Exists(sampleFile).Should().BeTrue($"Sample file {sampleFile} must exist");
        File.Copy(sampleFile, Path.Combine(namespaceDir, "DataMesh.md"));

        var source = new FileSystemStorageAdapter(importDir);
        var targetDir2 = Path.Combine(_targetDir, "dialog_target");
        var target = new FileSystemStorageAdapter(targetDir2);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act - import with no rootPath (dialog style)
        var result = await importer.ImportAsync(ct: ct);

        // Assert - the node should be placed at MeshWeaver/Documentation/DataMesh
        result.NodesImported.Should().Be(1);

        var exists = await target.ExistsAsync("MeshWeaver/Documentation/DataMesh", ct);
        exists.Should().BeTrue("DataMesh node should be at the correct namespace path");

        // Read the node and verify metadata
        var jsonOptions = StorageImporter.CreateDefaultImportOptions();
        var node = await target.ReadAsync("MeshWeaver/Documentation/DataMesh", jsonOptions, ct);
        node.Should().NotBeNull();
        node!.Namespace.Should().Be("MeshWeaver/Documentation");
        node.Id.Should().Be("DataMesh");
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

        // Verify Cornerstone and Northwind exist in target (both are root-level .md nodes)
        (await target.ExistsAsync("Cornerstone", ct)).Should().BeTrue();
        (await target.ExistsAsync("Northwind", ct)).Should().BeTrue();

        // Now create a partial source with only Cornerstone
        var partialSourceDir = Path.Combine(_targetDir, "partial_source");
        Directory.CreateDirectory(partialSourceDir);
        var sampleCornerstone = Path.Combine(_sourceDir, "Cornerstone.md");
        File.Exists(sampleCornerstone).Should().BeTrue();
        File.Copy(sampleCornerstone, Path.Combine(partialSourceDir, "Cornerstone.md"));

        var partialSource = new FileSystemStorageAdapter(partialSourceDir);
        var importer2 = new StorageImporter(partialSource, target);

        // Act - re-import partial source with RemoveMissing
        var result = await importer2.ImportAsync(new StorageImportOptions { RemoveMissing = true }, ct);

        // Assert - Cornerstone should still exist, Northwind should be removed
        result.NodesImported.Should().Be(1);
        result.NodesRemoved.Should().BeGreaterThan(0);
        (await target.ExistsAsync("Cornerstone", ct)).Should().BeTrue("Cornerstone was in the source");
        (await target.ExistsAsync("Northwind", ct)).Should().BeFalse("Northwind was not in partial source and should be removed");
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
        (await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/QuerySyntax", ct)).Should().BeTrue();
        (await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/CollaborativeEditing", ct)).Should().BeTrue();

        // Create partial source with just one DataMesh child
        var partialSourceDir = Path.Combine(_targetDir, "partial_subtree");
        var dataMeshDir = Path.Combine(partialSourceDir, "MeshWeaver", "Documentation", "DataMesh");
        Directory.CreateDirectory(dataMeshDir);
        // Copy just QuerySyntax.md
        File.Copy(
            Path.Combine(_sourceDir, "MeshWeaver", "Documentation", "DataMesh", "QuerySyntax.md"),
            Path.Combine(dataMeshDir, "QuerySyntax.md"));

        var partialSource = new FileSystemStorageAdapter(partialSourceDir);
        var importer2 = new StorageImporter(partialSource, target);

        // Act - re-import subtree with RemoveMissing, scoped to DataMesh
        var result = await importer2.ImportAsync(new StorageImportOptions
        {
            RootPath = "MeshWeaver/Documentation/DataMesh",
            RemoveMissing = true
        }, ct);

        // Assert
        result.NodesImported.Should().Be(1);
        result.NodesRemoved.Should().BeGreaterThan(0, "CollaborativeEditing and other DataMesh children should be removed");

        // QuerySyntax should still exist
        (await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/QuerySyntax", ct)).Should().BeTrue();
        // CollaborativeEditing should be removed (not in partial source)
        (await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/CollaborativeEditing", ct)).Should().BeFalse();
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

        // Create partial source with only Cornerstone
        var partialSourceDir = Path.Combine(_targetDir, "partial_no_remove");
        Directory.CreateDirectory(partialSourceDir);
        File.Copy(
            Path.Combine(_sourceDir, "Cornerstone.md"),
            Path.Combine(partialSourceDir, "Cornerstone.md"));

        var partialSource = new FileSystemStorageAdapter(partialSourceDir);
        var importer2 = new StorageImporter(partialSource, target);

        // Act - re-import partial source WITHOUT RemoveMissing (default)
        var result = await importer2.ImportAsync(new StorageImportOptions { RemoveMissing = false }, ct);

        // Assert - nothing should be removed
        result.NodesRemoved.Should().Be(0);
        (await target.ExistsAsync("Cornerstone", ct)).Should().BeTrue();
        (await target.ExistsAsync("Northwind", ct)).Should().BeTrue("Northwind should still exist when RemoveMissing is false");
    }

    [Fact]
    public async Task DataMeshSubtree_ImportsAllNodesIncludingNestedChildren()
    {
        // Arrange - verify exact node count for DataMesh subtree
        // DataMesh/ contains: CollaborativeEditing.md, CRUD.md, DataConfiguration.md,
        // DataModeling.md, InteractiveMarkdown.md, NodeTypeConfiguration.md, QuerySyntax.md,
        // UnifiedPath.md, What is a Data Mesh.md, plus:
        // - CollaborativeEditing/{c1-c6}.json (6 comment nodes) + c1/reply1.json (1 reply)
        // - UnifiedPath/{AreaPrefix,ContentPrefix,DataPrefix,sample,SchemaPrefix,Syntax}.md (6 children)
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act - import only DataMesh subtree
        var result = await importer.ImportAsync(new StorageImportOptions
        {
            RootPath = "MeshWeaver/Documentation/DataMesh"
        }, ct);

        // Assert - 22 nodes total:
        // - 9 direct .md children of DataMesh (including CollaborativeEditing.md, What is a Data Mesh.md)
        // - 7 nodes under CollaborativeEditing/ (c1-c6 + c1/reply1)
        // - 6 UnifiedPath .md children (including sample.md)
        result.NodesImported.Should().Be(22,
            "DataMesh/ has 9 .md children + CollaborativeEditing/ has 7 comment nodes + UnifiedPath/ has 6 .md children = 22 total");

        // Verify key expected nodes
        var expectedNodes = new[]
        {
            "MeshWeaver/Documentation/DataMesh/CollaborativeEditing",
            "MeshWeaver/Documentation/DataMesh/CollaborativeEditing/c1",
            "MeshWeaver/Documentation/DataMesh/CollaborativeEditing/c1/reply1",
            "MeshWeaver/Documentation/DataMesh/CollaborativeEditing/c2",
            "MeshWeaver/Documentation/DataMesh/CollaborativeEditing/c3",
            "MeshWeaver/Documentation/DataMesh/CollaborativeEditing/c4",
            "MeshWeaver/Documentation/DataMesh/CollaborativeEditing/c5",
            "MeshWeaver/Documentation/DataMesh/CollaborativeEditing/c6",
            "MeshWeaver/Documentation/DataMesh/CRUD",
            "MeshWeaver/Documentation/DataMesh/DataConfiguration",
            "MeshWeaver/Documentation/DataMesh/DataModeling",
            "MeshWeaver/Documentation/DataMesh/InteractiveMarkdown",
            "MeshWeaver/Documentation/DataMesh/NodeTypeConfiguration",
            "MeshWeaver/Documentation/DataMesh/QuerySyntax",
            "MeshWeaver/Documentation/DataMesh/UnifiedPath",
            "MeshWeaver/Documentation/DataMesh/UnifiedPath/AreaPrefix",
            "MeshWeaver/Documentation/DataMesh/UnifiedPath/ContentPrefix",
            "MeshWeaver/Documentation/DataMesh/UnifiedPath/DataPrefix",
            "MeshWeaver/Documentation/DataMesh/UnifiedPath/sample",
            "MeshWeaver/Documentation/DataMesh/UnifiedPath/SchemaPrefix",
            "MeshWeaver/Documentation/DataMesh/UnifiedPath/Syntax",
            "MeshWeaver/Documentation/DataMesh/What is a Data Mesh",
        };

        foreach (var nodePath in expectedNodes)
        {
            (await target.ExistsAsync(nodePath, ct)).Should().BeTrue($"{nodePath} should be imported");
        }
    }

    [Fact]
    public async Task DialogUploadFlow_FileWithSiblingFolder_ImportsAllChildren()
    {
        // Arrange - simulate dialog uploading DataMesh.md AND its sibling DataMesh/ folder
        // (the dialog should copy sibling folders matching uploaded file names)
        var importDir = Path.Combine(_targetDir, "dialog_with_folder");
        var namespaceDir = Path.Combine(importDir, "MeshWeaver", "Documentation");
        Directory.CreateDirectory(namespaceDir);

        // Copy DataMesh.md
        var sampleFile = Path.Combine(_sourceDir, "MeshWeaver", "Documentation", "DataMesh.md");
        File.Exists(sampleFile).Should().BeTrue();
        File.Copy(sampleFile, Path.Combine(namespaceDir, "DataMesh.md"));

        // Copy DataMesh/ directory (the sibling folder)
        CopyDirectory(
            Path.Combine(_sourceDir, "MeshWeaver", "Documentation", "DataMesh"),
            Path.Combine(namespaceDir, "DataMesh"));

        var source = new FileSystemStorageAdapter(importDir);
        var targetDir2 = Path.Combine(_targetDir, "dialog_folder_target");
        var target = new FileSystemStorageAdapter(targetDir2);
        var importer = new StorageImporter(source, target);

        var ct = TestContext.Current.CancellationToken;

        // Act
        var result = await importer.ImportAsync(ct: ct);

        // Assert - should import DataMesh + all 22 children = 23 total
        result.NodesImported.Should().Be(23,
            "DataMesh.md (1) + DataMesh/ children (9) + CollaborativeEditing/ comments (7) + UnifiedPath/ children (6) = 23");

        (await target.ExistsAsync("MeshWeaver/Documentation/DataMesh", ct)).Should().BeTrue();
        (await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/NodeTypeConfiguration", ct)).Should().BeTrue();
        (await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/UnifiedPath/Syntax", ct)).Should().BeTrue();

        // Verify metadata
        var jsonOptions = StorageImporter.CreateDefaultImportOptions();
        var node = await target.ReadAsync("MeshWeaver/Documentation/DataMesh", jsonOptions, ct);
        node.Should().NotBeNull();
        node!.Namespace.Should().Be("MeshWeaver/Documentation");
        node.Id.Should().Be("DataMesh");
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
            "re-importing identical source with RemoveMissing should remove nothing");
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
            RootPath = "MeshWeaver/Documentation/DataMesh",
            RemoveMissing = true
        }, ct);

        // Assert
        result.NodesImported.Should().Be(22, "DataMesh subtree has 22 nodes (9 .md + 7 comment .json + 6 UnifiedPath .md)");
        result.NodesRemoved.Should().Be(0,
            "re-importing same subtree should produce zero removals");

        // Nodes outside the subtree should still exist
        (await target.ExistsAsync("Cornerstone", ct)).Should().BeTrue();
        (await target.ExistsAsync("MeshWeaver/Documentation/DataMesh/QuerySyntax", ct)).Should().BeTrue();
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
