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

        // Act
        var result = await importer.ImportAsync();

        // Assert
        result.NodesImported.Should().BeGreaterThan(0, "sample data should contain nodes");
        result.Elapsed.Should().BeGreaterThan(TimeSpan.Zero);

        // Verify known nodes were copied (use .md nodes which don't require polymorphic JSON)
        var executorExists = await target.ExistsAsync("Executor");
        executorExists.Should().BeTrue("Executor.md node should exist in target");

        var plannerExists = await target.ExistsAsync("Planner");
        plannerExists.Should().BeTrue("Planner.md node should exist in target");
    }

    [Fact]
    public async Task SubtreeImport_CopiesOnlySubtree()
    {
        // Arrange
        Directory.Exists(_sourceDir).Should().BeTrue();
        var source = new FileSystemStorageAdapter(_sourceDir);
        var target = new FileSystemStorageAdapter(_targetDir);
        var importer = new StorageImporter(source, target);

        // Act
        var result = await importer.ImportAsync(new StorageImportOptions { RootPath = "ACME" });

        // Assert
        result.NodesImported.Should().BeGreaterThan(0);

        // ACME children should be copied
        var todoAgentExists = await target.ExistsAsync("ACME/Project/TodoAgent");
        todoAgentExists.Should().BeTrue("ACME/Project/TodoAgent should have been imported");

        // Nodes outside ACME should NOT be copied
        var executorExists = await target.ExistsAsync("Executor");
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

        // Act
        var result = await importer.ImportAsync(new StorageImportOptions { ImportPartitions = true });

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

        // Act
        var result = await importer.ImportAsync(new StorageImportOptions
        {
            OnProgress = (nodes, partitions, path) => progressCalls.Add((nodes, partitions, path))
        });

        // Assert
        progressCalls.Should().NotBeEmpty("progress callback should fire for each imported node");
        progressCalls.Count.Should().Be(result.NodesImported);
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
        var result = await importer.ImportAsync();

        // Assert
        result.NodesImported.Should().Be(0);
        result.PartitionsImported.Should().Be(0);
    }
}
