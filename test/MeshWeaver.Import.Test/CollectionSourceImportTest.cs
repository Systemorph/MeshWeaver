using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Import.Test;

public class CollectionSourceImportTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly string _testFilesPath = Path.Combine(AppContext.BaseDirectory, "TestFiles", "CollectionSource");

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
            .AddContentCollections()
            .AddFileSystemContentCollection("TestCollection", _ => _testFilesPath)
            .WithRoutes(forward =>
                forward
                    .RouteAddressToHostedHub<ReferenceDataAddress>(c => c.ConfigureReferenceDataModel())
                    .RouteAddressToHostedHub<ImportAddress>(c => c.ConfigureImportHub())
            );
    }

    [Fact]
    public async Task ImportFromCollectionSource_ShouldResolveAndImportSuccessfully()
    {
        // Arrange
        var client = GetClient();

        // Create ImportRequest with CollectionSource - stream will be resolved automatically
        var importRequest = new ImportRequest(new CollectionSource("TestCollection", "test-data.csv"));

        // Act
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new ImportAddress(2024)),
            TestContext.Current.CancellationToken
        );

        // Assert
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

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
    public async Task ImportFromCollectionSource_WithSubfolder_ShouldResolveAndImportSuccessfully()
    {
        // Arrange
        var client = GetClient();

        // Test with path without leading slash (file is in root of collection)
        var importRequest = new ImportRequest(new CollectionSource("TestCollection", "test-data.csv"));

        // Act
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new ImportAddress(2024)),
            TestContext.Current.CancellationToken
        );

        // Assert
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        // Verify data was imported
        var referenceDataHub = Router.GetHostedHub(new ReferenceDataAddress());
        var workspace = referenceDataHub.ServiceProvider.GetRequiredService<IWorkspace>();

        var allData = await workspace.GetObservable<LineOfBusiness>()
            .Timeout(10.Seconds())
            .FirstAsync(x => x.Count >= 3);

        allData.Should().HaveCount(3);
    }

    [Fact]
    public async Task ImportFromCollectionSource_NonExistentFile_ShouldFail()
    {
        // Arrange
        var client = GetClient();

        var importRequest = new ImportRequest(new CollectionSource("TestCollection", "non-existent.csv"));

        // Act
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new ImportAddress(2024)),
            TestContext.Current.CancellationToken
        );

        // Assert
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Failed);
        var errors = importResponse.Message.Log.Errors();
        errors.Should().Contain(m => m.Message.Contains("Could not find content"));
    }

    [Fact]
    public async Task ImportFromCollectionSource_NonExistentCollection_ShouldFail()
    {
        // Arrange
        var client = GetClient();

        var importRequest = new ImportRequest(new CollectionSource("NonExistentCollection", "test-data.csv"));

        // Act
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new ImportAddress(2024)),
            TestContext.Current.CancellationToken
        );

        // Assert
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Failed);
        var errors = importResponse.Message.Log.Errors();
        errors.Should().Contain(m => m.Message.Contains("Collection") && m.Message.Contains("not found"));
    }
}
