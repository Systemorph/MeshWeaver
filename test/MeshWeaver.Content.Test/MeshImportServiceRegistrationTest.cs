using FluentAssertions;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Tests that IMeshImportService is registered in DI across all persistence configurations
/// and that zip/folder import flows work end-to-end.
/// </summary>
public class MeshImportServiceRegistrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string _sourceDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", "ImportReg_Source_" + Guid.NewGuid());
    private readonly string _targetDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", "ImportReg_Target_" + Guid.NewGuid());

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    public override void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
        if (Directory.Exists(_targetDirectory))
            Directory.Delete(_targetDirectory, recursive: true);
    }

    [Fact]
    public void IMeshImportService_IsRegisteredInDI()
    {
        // The key fix: IMeshImportService must be resolvable from the service provider.
        // Previously this returned null, causing "Import service is not available." errors.
        var importService = Mesh.ServiceProvider.GetService<IMeshImportService>();
        importService.Should().NotBeNull("IMeshImportService must be registered in DI");
        importService.Should().BeOfType<MeshImportService>();
    }

    [Fact]
    public async Task IMeshImportService_ImportNodes_FromSourceDirectory()
    {
        // Arrange
        Directory.CreateDirectory(_sourceDirectory);
        var sourceAdapter = new FileSystemStorageAdapter(_sourceDirectory);
        var jsonOptions = StorageImporter.CreateFullImportOptions();

        // Create test nodes in the source directory
        var node1 = MeshNode.FromPath("TestNs/Alpha") with { Name = "Alpha Node", NodeType = "Markdown" };
        var node2 = MeshNode.FromPath("TestNs/Beta") with { Name = "Beta Node", NodeType = "Markdown" };
        await sourceAdapter.WriteAsync(node1, jsonOptions, CancellationToken.None);
        await sourceAdapter.WriteAsync(node2, jsonOptions, CancellationToken.None);

        // Act - resolve from DI and run import
        var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();
        var result = await importService.ImportNodesAsync(_sourceDirectory, force: true);

        // Assert
        result.Success.Should().BeTrue();
        result.NodesImported.Should().BeGreaterThanOrEqualTo(2);
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

        // Act - import with a target root path
        var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();
        var result = await importService.ImportNodesAsync(
            _sourceDirectory,
            targetRootPath: "ImportedData",
            force: true);

        // Assert
        result.Success.Should().BeTrue();
        result.NodesImported.Should().BeGreaterThanOrEqualTo(1);
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

        // Create a zip file from the source directory
        var zipPath = Path.Combine(Path.GetTempPath(), $"meshweaver-test-{Guid.NewGuid():N}.zip");
        try
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(_sourceDirectory, zipPath);
            File.Exists(zipPath).Should().BeTrue();

            // Extract to a fresh temp directory (simulating what NodeImportView does)
            var extractDir = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", "ZipExtract_" + Guid.NewGuid());
            try
            {
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);

                // Act - import the extracted directory
                var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();
                var result = await importService.ImportNodesAsync(extractDir, force: true);

                // Assert
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
        // Arrange
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

        // Act
        var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();
        var result = await importService.ImportNodesAsync(
            _sourceDirectory, force: true, onProgress: onProgress);

        // Assert
        result.Success.Should().BeTrue();
        progressPaths.Should().NotBeEmpty("progress callback should be invoked during import");
    }
}
