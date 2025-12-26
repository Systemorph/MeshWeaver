using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI.Plugins;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Import.Test;

public class CollectionPluginImportTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly string _testFilesPath = Path.Combine(AppContext.BaseDirectory, "TestFiles", "CollectionPluginImport");

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration configuration)
    {
        // Ensure test directory and file exist
        Directory.CreateDirectory(_testFilesPath);
        var csvContent = @"@@LineOfBusiness
SystemName,DisplayName
1,LoB 1
2,LoB 2
3,LoB 3";
        File.WriteAllText(Path.Combine(_testFilesPath, "test-data.csv"), csvContent);

        return base.ConfigureMesh(configuration)
            .WithTypes(typeof(ImportRequest), typeof(CollectionSource))
            .AddContentCollections()
            .AddFileSystemContentCollection("TestCollection", _ => _testFilesPath)
            .ConfigureImportRouter();
    }

    [Fact]
    public async Task CollectionPlugin_Import_ShouldImportSuccessfully()
    {
        // Arrange
        var client = GetClient();
        var plugin = new ContentPlugin(client);

        // Act - use fully qualified collection:path syntax
        var result = await plugin.Import(
            path: "TestCollection:test-data.csv",
            address: ImportAddress.Create(2024),
            format: null // Use default format
        );

        // Assert
        result.Should().Contain("succeeded", "import should succeed");
        result.Should().NotContain("Error", "there should be no errors");

        // Verify data was imported
        var referenceDataHub = Mesh.GetHostedHub(ReferenceDataAddress.Create());
        var workspace = referenceDataHub.ServiceProvider.GetRequiredService<IWorkspace>();

        var allData = await workspace.GetObservable<LineOfBusiness>()
            .Timeout(10.Seconds())
            .FirstAsync(x => x.Count >= 3);

        allData.Should().HaveCount(3);
        var items = allData.OrderBy(x => x.SystemName).ToList();
        items[0].DisplayName.Should().Be("LoB 1");
        items[1].DisplayName.Should().Be("LoB 2");
        items[2].DisplayName.Should().Be("LoB 3");
    }

    [Fact]
    public async Task CollectionPlugin_Import_WithNonExistentFile_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var plugin = new ContentPlugin(client);

        // Act - use fully qualified collection:path syntax
        var result = await plugin.Import(
            path: "TestCollection:non-existent.csv",
            address: ImportAddress.Create(2024),
            format: null
        );

        // Assert
        result.Should().Contain("failed", "import should fail for non-existent file");
    }

    [Fact]
    public async Task CollectionPlugin_Import_WithNonExistentCollection_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var plugin = new ContentPlugin(client);

        // Act - use fully qualified collection:path syntax with non-existent collection
        var result = await plugin.Import(
            path: "NonExistentCollection:test-data.csv",
            address: ImportAddress.Create(2024),
            format: null
        );

        // Assert
        result.Should().Contain("Error", "import should fail for non-existent collection");
    }

    [Fact]
    public async Task CollectionPlugin_Import_WithMissingCollection_ShouldReturnError()
    {
        // Arrange
        var client = GetClient();
        var plugin = new ContentPlugin(client);

        // Act - path without collection:path syntax (no colon)
        var result = await plugin.Import(
            path: "test-data.csv",
            address: ImportAddress.Create(2024),
            format: null
        );

        // Assert - should fail because no collection can be resolved
        result.Should().Contain("No collection specified", "should indicate missing collection");
    }

    [Fact]
    public async Task CollectionPlugin_Import_WithMissingAddress_ShouldReturnError()
    {
        // Arrange
        var client = GetClient();
        var plugin = new ContentPlugin(client);

        // Act - use fully qualified collection:path syntax but missing address
        var result = await plugin.Import(
            path: "TestCollection:test-data.csv",
            address: null,
            format: null
        );

        // Assert
        result.Should().Contain("Target address is required");
    }

    [Fact]
    public async Task CollectionPlugin_Import_WithCustomFormat_ShouldImportSuccessfully()
    {
        // Arrange
        var client = GetClient();
        var plugin = new ContentPlugin(client);

        // Act - use fully qualified collection:path syntax with explicit format
        var result = await plugin.Import(
            path: "TestCollection:test-data.csv",
            address: ImportAddress.Create(2024),
            format: "Default" // Explicit format
        );

        // Assert
        result.Should().Contain("succeeded", "import should succeed with explicit format");

        // Verify data was imported
        var referenceDataHub = Mesh.GetHostedHub(ReferenceDataAddress.Create());
        var workspace = referenceDataHub.ServiceProvider.GetRequiredService<IWorkspace>();

        var allData = await workspace.GetObservable<LineOfBusiness>()
            .Timeout(10.Seconds())
            .FirstAsync(x => x.Count >= 3);

        allData.Should().HaveCount(3);
    }

    [Fact]
    public async Task CollectionPlugin_Import_WithConfiguration_ShouldImportWithoutFormat()
    {
        // Arrange
        var client = GetClient();
        var plugin = new ContentPlugin(client);

        // Create a configuration JSON that is not registered as a format
        var configurationJson = @"{
            ""$type"": ""MeshWeaver.Import.Configuration.ImportConfiguration"",
            ""name"": ""test-config-not-registered"",
            ""address"": ""ImportAddress/2024""
        }";

        // Act - use fully qualified collection:path syntax with configuration
        var result = await plugin.Import(
            path: "TestCollection:test-data.csv",
            address: ImportAddress.Create(2024),
            format: null,
            configuration: configurationJson
        );

        // Assert
        // The import should succeed even though "test-config-not-registered" is not a registered format
        // This demonstrates that when configuration is provided, it bypasses format resolution
        result.Should().Contain("succeeded", "import should succeed with configuration even if not registered as format");
        result.Should().NotContain("Error", "there should be no errors");
        result.Should().NotContain("Unknown format", "should not try to resolve configuration as format");

        // Verify data was imported
        var referenceDataHub = Mesh.GetHostedHub(ReferenceDataAddress.Create());
        var workspace = referenceDataHub.ServiceProvider.GetRequiredService<IWorkspace>();

        var allData = await workspace.GetObservable<LineOfBusiness>()
            .Timeout(10.Seconds())
            .FirstAsync(x => x.Count >= 3);

        allData.Should().HaveCount(3);
        var items = allData.OrderBy(x => x.SystemName).ToList();
        items[0].DisplayName.Should().Be("LoB 1");
        items[1].DisplayName.Should().Be("LoB 2");
        items[2].DisplayName.Should().Be("LoB 3");
    }
}
