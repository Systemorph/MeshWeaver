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

    protected override MessageHubConfiguration ConfigureRouter(MessageHubConfiguration configuration)
    {
        // Ensure test directory and file exist
        Directory.CreateDirectory(_testFilesPath);
        var csvContent = @"@@LineOfBusiness
SystemName,DisplayName
1,LoB 1
2,LoB 2
3,LoB 3";
        File.WriteAllText(Path.Combine(_testFilesPath, "test-data.csv"), csvContent);

        return base.ConfigureRouter(configuration)
            .WithTypes(typeof(ImportAddress))
            .AddContentCollections()
            .AddFileSystemContentCollection("TestCollection", _ => _testFilesPath)
            .WithRoutes(forward =>
                forward
                    .RouteAddressToHostedHub<ReferenceDataAddress>(c => c.ConfigureReferenceDataModel())
                    .RouteAddressToHostedHub<ImportAddress>(c => c.ConfigureImportHub())
            );
    }

    [Fact]
    public async Task CollectionPlugin_Import_ShouldImportSuccessfully()
    {
        // Arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);

        // Act
        var result = await plugin.Import(
            path: "test-data.csv",
            collection: "TestCollection",
            address: new ImportAddress(2024),
            format: null, // Use default format
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().Contain("succeeded", "import should succeed");
        result.Should().NotContain("Error", "there should be no errors");

        // Verify data was imported
        var referenceDataHub = Router.GetHostedHub(new ReferenceDataAddress());
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
        var plugin = new CollectionPlugin(client);

        // Act
        var result = await plugin.Import(
            path: "non-existent.csv",
            collection: "TestCollection",
            address: new ImportAddress(2024),
            format: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().Contain("failed", "import should fail for non-existent file");
    }

    [Fact]
    public async Task CollectionPlugin_Import_WithNonExistentCollection_ShouldFail()
    {
        // Arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);

        // Act
        var result = await plugin.Import(
            path: "test-data.csv",
            collection: "NonExistentCollection",
            address: new ImportAddress(2024),
            format: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().Contain("Error", "import should fail for non-existent collection");
    }

    [Fact]
    public async Task CollectionPlugin_Import_WithMissingCollection_ShouldReturnError()
    {
        // Arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);

        // Act
        var result = await plugin.Import(
            path: "test-data.csv",
            collection: null,
            address: "ImportAddress/2024",
            format: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().Contain("Collection name is required");
    }

    [Fact]
    public async Task CollectionPlugin_Import_WithMissingAddress_ShouldReturnError()
    {
        // Arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);

        // Act
        var result = await plugin.Import(
            path: "test-data.csv",
            collection: "TestCollection",
            address: null,
            format: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().Contain("Target address is required");
    }

    [Fact]
    public async Task CollectionPlugin_Import_WithCustomFormat_ShouldImportSuccessfully()
    {
        // Arrange
        var client = GetClient();
        var plugin = new CollectionPlugin(client);

        // Act
        var result = await plugin.Import(
            path: "test-data.csv",
            collection: "TestCollection",
            address: new ImportAddress(2024),
            format: "Default", // Explicit format
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().Contain("succeeded", "import should succeed with explicit format");

        // Verify data was imported
        var referenceDataHub = Router.GetHostedHub(new ReferenceDataAddress());
        var workspace = referenceDataHub.ServiceProvider.GetRequiredService<IWorkspace>();

        var allData = await workspace.GetObservable<LineOfBusiness>()
            .Timeout(10.Seconds())
            .FirstAsync(x => x.Count >= 3);

        allData.Should().HaveCount(3);
    }
}
