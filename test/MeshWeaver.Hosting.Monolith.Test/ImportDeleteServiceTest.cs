using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for ImportHelper, MeshImportService, and the import/delete workflow.
/// Uses file system storage adapters with temporary directories.
/// </summary>
public class ImportDeleteServiceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string _sourceDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", "ImportSource_" + Guid.NewGuid());
    private readonly string _targetDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", "ImportTarget_" + Guid.NewGuid());

    private CancellationToken TestTimeout => new CancellationTokenSource(30.Seconds()).Token;

    public override void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
        if (Directory.Exists(_targetDirectory))
            Directory.Delete(_targetDirectory, recursive: true);
    }

    #region ImportHelper Tests

    [Fact]
    public async Task ImportHelper_EmptySource_ReturnsZeroCounts()
    {
        // Arrange - empty source directory
        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_targetDirectory);

        var source = new FileSystemStorageAdapter(_sourceDirectory);
        var target = new FileSystemStorageAdapter(_targetDirectory);
        var logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<ImportDeleteServiceTest>();

        // Act
        var result = await ImportHelper.RunImportAsync(source, target, logger);

        // Assert
        result.Success.Should().BeTrue();
        result.NodesImported.Should().Be(0);
        result.PartitionsImported.Should().Be(0);
    }

    [Fact]
    public async Task ImportHelper_WithNodes_ImportsSuccessfully()
    {
        // Arrange - create source nodes
        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_targetDirectory);

        var source = new FileSystemStorageAdapter(_sourceDirectory);
        var target = new FileSystemStorageAdapter(_targetDirectory);
        var logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<ImportDeleteServiceTest>();
        var jsonOptions = StorageImporter.CreateFullImportOptions();

        // Seed source with some nodes
        var node1 = MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "organization" };
        var node2 = MeshNode.FromPath("org/acme/project1") with { Name = "Project 1", NodeType = "project" };
        await source.WriteAsync(node1, jsonOptions, CancellationToken.None);
        await source.WriteAsync(node2, jsonOptions, CancellationToken.None);

        // Act
        var result = await ImportHelper.RunImportAsync(source, target, logger, force: true);

        // Assert
        result.Success.Should().BeTrue();
        result.NodesImported.Should().BeGreaterThanOrEqualTo(2);

        // Verify nodes were written to target
        var targetNode = await target.ReadAsync("org/acme", jsonOptions, CancellationToken.None);
        targetNode.Should().NotBeNull();
        targetNode!.Name.Should().Be("Acme Corp");
    }

    [Fact]
    public async Task ImportHelper_IdempotencyCheck_SkipsWhenTargetHasData()
    {
        // Arrange
        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_targetDirectory);

        var source = new FileSystemStorageAdapter(_sourceDirectory);
        var target = new FileSystemStorageAdapter(_targetDirectory);
        var logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<ImportDeleteServiceTest>();
        var jsonOptions = StorageImporter.CreateFullImportOptions();

        // Seed source with a node
        var node = MeshNode.FromPath("test") with { Name = "Test" };
        await source.WriteAsync(node, jsonOptions, CancellationToken.None);

        // Seed target with existing data at root level (ListChildPathsAsync(null) finds root-level .json files)
        var existing = MeshNode.FromPath("existing") with { Name = "Existing" };
        await target.WriteAsync(existing, jsonOptions, CancellationToken.None);

        // Act - run without force
        var result = await ImportHelper.RunImportAsync(source, target, logger, force: false);

        // Assert - should skip import (0 nodes) because target has data
        result.Success.Should().BeTrue();
        result.NodesImported.Should().Be(0);
    }

    [Fact]
    public async Task ImportHelper_ForceReimport_ImportsEvenWithExistingData()
    {
        // Arrange
        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_targetDirectory);

        var source = new FileSystemStorageAdapter(_sourceDirectory);
        var target = new FileSystemStorageAdapter(_targetDirectory);
        var logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<ImportDeleteServiceTest>();
        var jsonOptions = StorageImporter.CreateFullImportOptions();

        // Seed source
        var node = MeshNode.FromPath("test") with { Name = "Test Node" };
        await source.WriteAsync(node, jsonOptions, CancellationToken.None);

        // Seed target with existing data at root level
        var existing = MeshNode.FromPath("existing") with { Name = "Existing" };
        await target.WriteAsync(existing, jsonOptions, CancellationToken.None);

        // Act - run WITH force
        var result = await ImportHelper.RunImportAsync(source, target, logger, force: true);

        // Assert - should import despite existing data
        result.Success.Should().BeTrue();
        result.NodesImported.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ImportHelper_ProgressCallback_IsInvoked()
    {
        // Arrange
        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_targetDirectory);

        var source = new FileSystemStorageAdapter(_sourceDirectory);
        var target = new FileSystemStorageAdapter(_targetDirectory);
        var logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<ImportDeleteServiceTest>();
        var jsonOptions = StorageImporter.CreateFullImportOptions();

        // Seed source with nodes
        await source.WriteAsync(MeshNode.FromPath("test/node1") with { Name = "Node 1" }, jsonOptions, CancellationToken.None);
        await source.WriteAsync(MeshNode.FromPath("test/node2") with { Name = "Node 2" }, jsonOptions, CancellationToken.None);

        var progressCalled = false;
        Action<int, int, string> onProgress = (nodes, partitions, path) => progressCalled = true;

        // Act
        var result = await ImportHelper.RunImportAsync(source, target, logger, force: true, onProgress: onProgress);

        // Assert
        result.Success.Should().BeTrue();
        progressCalled.Should().BeTrue();
    }

    #endregion

    #region MeshImportService Tests

    [Fact]
    public async Task MeshImportService_ImportNodes_NonexistentSource_ReturnsFail()
    {
        // Arrange - create a service with a file system adapter so DI resolution works
        var logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<MeshImportService>();
        Directory.CreateDirectory(_targetDirectory);
        var storageAdapter = new FileSystemStorageAdapter(_targetDirectory);
        var contentService = Mesh.ServiceProvider.GetService<IContentService>();
        var importService = new MeshImportService(storageAdapter, contentService!, logger);

        var nonExistentPath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid());

        // Act
        var result = await importService.ImportNodesAsync(nonExistentPath);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not exist");
    }

    [Fact]
    public async Task MeshImportService_ImportNodes_ValidSource_ImportsSuccessfully()
    {
        // Arrange
        var logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<MeshImportService>();
        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_targetDirectory);
        var storageAdapter = new FileSystemStorageAdapter(_targetDirectory);
        var contentService = Mesh.ServiceProvider.GetService<IContentService>();
        var importService = new MeshImportService(storageAdapter, contentService!, logger);

        // Seed source with nodes
        var sourceAdapter = new FileSystemStorageAdapter(_sourceDirectory);
        var jsonOptions = StorageImporter.CreateFullImportOptions();
        await sourceAdapter.WriteAsync(MeshNode.FromPath("svc/node1") with { Name = "Service Node 1" }, jsonOptions, CancellationToken.None);

        // Act
        var result = await importService.ImportNodesAsync(_sourceDirectory, force: true);

        // Assert
        result.Success.Should().BeTrue();
        result.NodesImported.Should().BeGreaterThanOrEqualTo(1);
    }

    #endregion

    #region Full Lifecycle Tests (Create, Import, Delete)

    [Fact]
    public async Task FullLifecycle_CreateNodes_DeleteRecursively()
    {
        // Arrange
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Create parent
        var parent = new MeshNode("ImportTestParent", "lifecycle") { Name = "Parent" };
        var createParent = await client.AwaitResponse(
            new CreateNodeRequest(parent),
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createParent.Message.Success.Should().BeTrue();

        // Create children
        var child1 = new MeshNode("Child1", "lifecycle/ImportTestParent") { Name = "Child 1" };
        var child2 = new MeshNode("Child2", "lifecycle/ImportTestParent") { Name = "Child 2" };
        await client.AwaitResponse(new CreateNodeRequest(child1), o => o.WithTarget(Mesh.Address), TestTimeout);
        await client.AwaitResponse(new CreateNodeRequest(child2), o => o.WithTarget(Mesh.Address), TestTimeout);

        // Act - delete parent recursively
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest("lifecycle/ImportTestParent") { Recursive = true },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        deleteResponse.Message.Success.Should().BeTrue();

        // Verify all nodes are gone
        var parentNode = await catalog.GetNodeAsync(new Address("lifecycle/ImportTestParent"));
        parentNode.Should().BeNull();

        var child1Node = await catalog.GetNodeAsync(new Address("lifecycle/ImportTestParent/Child1"));
        child1Node.Should().BeNull();

        var child2Node = await catalog.GetNodeAsync(new Address("lifecycle/ImportTestParent/Child2"));
        child2Node.Should().BeNull();
    }

    #endregion
}
