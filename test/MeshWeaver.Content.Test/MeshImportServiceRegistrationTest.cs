using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Tests that IMeshImportService is registered in DI and that
/// import flows work end-to-end using IMeshService for CRUD.
/// </summary>
public class MeshImportServiceRegistrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string _sourceDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", "ImportReg_Source_" + Guid.NewGuid());

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    public override void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }

    [Fact]
    public void IMeshImportService_IsRegisteredInDI()
    {
        var importService = Mesh.ServiceProvider.GetService<IMeshImportService>();
        importService.Should().NotBeNull("IMeshImportService must be registered in DI");
        importService.Should().BeOfType<MeshImportService>();
    }

    [Fact]
    public async Task IMeshImportService_ImportNodes_FromSourceDirectory()
    {
        // Arrange - create source nodes on disk
        Directory.CreateDirectory(_sourceDirectory);
        var sourceAdapter = new FileSystemStorageAdapter(_sourceDirectory);
        var jsonOptions = StorageImporter.CreateFullImportOptions();

        var node1 = MeshNode.FromPath("TestNs/Alpha") with { Name = "Alpha Node", NodeType = "Markdown" };
        var node2 = MeshNode.FromPath("TestNs/Beta") with { Name = "Beta Node", NodeType = "Markdown" };
        await sourceAdapter.WriteAsync(node1, jsonOptions, CancellationToken.None);
        await sourceAdapter.WriteAsync(node2, jsonOptions, CancellationToken.None);

        // Act - resolve from DI (uses IMeshService, not IStorageAdapter)
        var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();
        var result = await importService.ImportNodesAsync(_sourceDirectory, force: true);

        // Assert
        result.Success.Should().BeTrue();
        result.NodesImported.Should().BeGreaterThanOrEqualTo(2);

        // Verify nodes are queryable via IMeshService
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var alpha = await meshService.QueryAsync<MeshNode>("path:TestNs/Alpha").FirstOrDefaultAsync();
        alpha.Should().NotBeNull();
        alpha!.Name.Should().Be("Alpha Node");
    }

    [Fact]
    public async Task IMeshImportService_ImportNodes_WithTargetRootPath()
    {
        // Arrange
        Directory.CreateDirectory(_sourceDirectory);
        var sourceAdapter = new FileSystemStorageAdapter(_sourceDirectory);
        var jsonOptions = StorageImporter.CreateFullImportOptions();

        var node = MeshNode.FromPath("Item1") with { Name = "Item 1", NodeType = "Markdown" };
        await sourceAdapter.WriteAsync(node, jsonOptions, CancellationToken.None);

        // Act - import with target root path remapping
        var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();
        var result = await importService.ImportNodesAsync(
            _sourceDirectory,
            targetRootPath: "ImportedData",
            force: true);

        // Assert
        result.Success.Should().BeTrue();
        result.NodesImported.Should().BeGreaterThanOrEqualTo(1);

        // Verify the node was remapped to ImportedData/Item1
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node1 = await meshService.QueryAsync<MeshNode>("path:ImportedData/Item1").FirstOrDefaultAsync();
        node1.Should().NotBeNull();
    }

    [Fact]
    public async Task IMeshImportService_ImportNodes_NonexistentSource_ReturnsFail()
    {
        var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid());

        var result = await importService.ImportNodesAsync(nonExistentPath);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not exist");
    }

    [Fact]
    public async Task ZipImport_ExtractAndImport_WorksEndToEnd()
    {
        // Arrange - create source nodes, then zip them
        Directory.CreateDirectory(_sourceDirectory);
        var sourceAdapter = new FileSystemStorageAdapter(_sourceDirectory);
        var jsonOptions = StorageImporter.CreateFullImportOptions();

        var node1 = MeshNode.FromPath("ZipNs/Gamma") with { Name = "Gamma", NodeType = "Markdown" };
        var node2 = MeshNode.FromPath("ZipNs/Delta") with { Name = "Delta", NodeType = "Markdown" };
        await sourceAdapter.WriteAsync(node1, jsonOptions, CancellationToken.None);
        await sourceAdapter.WriteAsync(node2, jsonOptions, CancellationToken.None);

        var zipPath = Path.Combine(Path.GetTempPath(), $"meshweaver-test-{Guid.NewGuid():N}.zip");
        try
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(_sourceDirectory, zipPath);

            var extractDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", "ZipExtract_" + Guid.NewGuid());
            try
            {
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);

                var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();
                var result = await importService.ImportNodesAsync(extractDir, force: true);

                result.Success.Should().BeTrue();
                result.NodesImported.Should().BeGreaterThanOrEqualTo(2);
            }
            finally
            {
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, recursive: true);
            }
        }
        finally
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task IMeshImportService_ProgressCallback_IsInvoked()
    {
        Directory.CreateDirectory(_sourceDirectory);
        var sourceAdapter = new FileSystemStorageAdapter(_sourceDirectory);
        var jsonOptions = StorageImporter.CreateFullImportOptions();

        await sourceAdapter.WriteAsync(
            MeshNode.FromPath("Progress/Node1") with { Name = "Node 1" },
            jsonOptions, CancellationToken.None);
        await sourceAdapter.WriteAsync(
            MeshNode.FromPath("Progress/Node2") with { Name = "Node 2" },
            jsonOptions, CancellationToken.None);

        var progressPaths = new List<string>();
        Action<int, int, string> onProgress = (_, _, path) => progressPaths.Add(path);

        var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();
        var result = await importService.ImportNodesAsync(
            _sourceDirectory, force: true, onProgress: onProgress);

        result.Success.Should().BeTrue();
        progressPaths.Should().NotBeEmpty("progress callback should be invoked during import");
    }

    [Fact]
    public async Task IMeshImportService_ForceUpdate_OverwritesExistingNodes()
    {
        // Arrange - create a node via IMeshService
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNodeAsync(
            MeshNode.FromPath("ForceTest/Existing") with { Name = "Original", NodeType = "Markdown" });

        // Create source with updated version
        Directory.CreateDirectory(_sourceDirectory);
        var sourceAdapter = new FileSystemStorageAdapter(_sourceDirectory);
        var jsonOptions = StorageImporter.CreateFullImportOptions();
        await sourceAdapter.WriteAsync(
            MeshNode.FromPath("ForceTest/Existing") with { Name = "Updated", NodeType = "Markdown" },
            jsonOptions, CancellationToken.None);

        // Act - import with force
        var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();
        var result = await importService.ImportNodesAsync(_sourceDirectory, force: true);

        // Assert
        result.Success.Should().BeTrue();
        result.NodesImported.Should().Be(1);
        result.NodesSkipped.Should().Be(0);

        var updated = await meshService.QueryAsync<MeshNode>("path:ForceTest/Existing").FirstOrDefaultAsync();
        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Updated");
    }

    [Fact]
    public async Task IMeshImportService_RemoveMissing_DeletesExtraNodes()
    {
        // Arrange - create nodes via IMeshService
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNodeAsync(
            MeshNode.FromPath("RemoveTest/Keep") with { Name = "Keep", NodeType = "Markdown" });
        await meshService.CreateNodeAsync(
            MeshNode.FromPath("RemoveTest/Remove") with { Name = "Remove", NodeType = "Markdown" });

        // Source only has "Keep" (will be remapped to RemoveTest/Keep by targetRootPath)
        Directory.CreateDirectory(_sourceDirectory);
        var sourceAdapter = new FileSystemStorageAdapter(_sourceDirectory);
        var jsonOptions = StorageImporter.CreateFullImportOptions();
        await sourceAdapter.WriteAsync(
            MeshNode.FromPath("Keep") with { Name = "Keep", NodeType = "Markdown" },
            jsonOptions, CancellationToken.None);

        // Act - import with removeMissing
        var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();
        var result = await importService.ImportNodesAsync(
            _sourceDirectory,
            targetRootPath: "RemoveTest",
            force: true,
            removeMissing: true);

        // Assert
        result.Success.Should().BeTrue();
        result.NodesRemoved.Should().BeGreaterThanOrEqualTo(1);

        var removed = await meshService.QueryAsync<MeshNode>("path:RemoveTest/Remove").FirstOrDefaultAsync();
        removed.Should().BeNull("node not in source should be removed");

        var kept = await meshService.QueryAsync<MeshNode>("path:RemoveTest/Keep").FirstOrDefaultAsync();
        kept.Should().NotBeNull("node in source should be kept");
    }
}
