using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for MapContentCollection extension method.
/// Tests the basic functionality of mapping a source configuration section
/// to a content collection with a subdirectory.
/// </summary>
[Collection("MapContentCollectionTests")]
public class MapContentCollectionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string _testBasePath = Path.Combine(Path.GetTempPath(), "MapContentCollectionTest", Guid.NewGuid().ToString());

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Create test directory
        Directory.CreateDirectory(_testBasePath);
        Output.WriteLine($"Test base path: {_testBasePath}");

        // Configure TestStorage section in IConfiguration
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TestStorage:SourceType"] = "FileSystem",
                ["TestStorage:BasePath"] = _testBasePath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (Directory.Exists(_testBasePath))
        {
            try { Directory.Delete(_testBasePath, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Test that MapContentCollection registers a collection with the correct configuration.
    /// The collection is configured on the CLIENT hub, not a remote hub.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MapContentCollection_RegistersCollectionWithCorrectBasePath()
    {
        // Arrange - create a client with MapContentCollection configured on it
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("avatars", "TestStorage", "persons/alice"));

        Output.WriteLine($"Client address: {client.Address}");

        // Act - request the avatars collection configuration from the client's own hub
        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference(["avatars"])),
            o => o.WithTarget(client.Address), // Send to self
            TestContext.Current.CancellationToken);

        // Assert
        response.Should().NotBeNull();
        response.Message.Should().NotBeNull();

        Output.WriteLine($"Response data type: {response.Message.Data?.GetType().FullName ?? "null"}");
        Output.WriteLine($"Response data: {response.Message.Data}");

        response.Message.Data.Should().NotBeNull("Response data should not be null");

        var configs = response.Message.Data as IReadOnlyCollection<ContentCollectionConfig>;
        configs.Should().NotBeNull("Data should be IReadOnlyCollection<ContentCollectionConfig>");
        configs.Should().HaveCount(1, "Should have exactly one collection");

        var config = configs!.First();
        config.Name.Should().Be("avatars");
        config.SourceType.Should().Be("FileSystem");
        // BasePath should end with persons/alice (forward slashes as used in MapContentCollection)
        config.BasePath.Should().EndWith("persons/alice");
    }

    /// <summary>
    /// Test that MapContentCollection with empty subdirectory uses the source base path.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MapContentCollection_WithEmptySubdirectory_UsesSourceBasePath()
    {
        // Arrange
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("storage", "TestStorage", ""));

        // Act
        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference(["storage"])),
            o => o.WithTarget(client.Address),
            TestContext.Current.CancellationToken);

        // Assert
        response.Should().NotBeNull();
        response.Message.Should().NotBeNull();

        Output.WriteLine($"Response data type: {response.Message.Data?.GetType().FullName ?? "null"}");

        response.Message.Data.Should().NotBeNull();

        var configs = response.Message.Data as IReadOnlyCollection<ContentCollectionConfig>;
        configs.Should().NotBeNull();

        var config = configs!.First();
        config.Name.Should().Be("storage");
        config.BasePath.Should().Be(_testBasePath);
    }

    /// <summary>
    /// Test that MapContentCollection falls back to current directory when source config is missing.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MapContentCollection_WithMissingSourceConfig_UsesCurrentDirectory()
    {
        // Arrange
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("files", "NonExistent:Section", "subdir"));

        // Act
        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference(["files"])),
            o => o.WithTarget(client.Address),
            TestContext.Current.CancellationToken);

        // Assert
        response.Should().NotBeNull();
        response.Message.Should().NotBeNull();

        Output.WriteLine($"Response data type: {response.Message.Data?.GetType().FullName ?? "null"}");

        response.Message.Data.Should().NotBeNull();

        var configs = response.Message.Data as IReadOnlyCollection<ContentCollectionConfig>;
        configs.Should().NotBeNull();

        var config = configs!.First();
        config.Name.Should().Be("files");
        config.SourceType.Should().Be("FileSystem");
        // BasePath should be current directory combined with subdirectory
        config.BasePath.Should().EndWith("subdir");
    }

    /// <summary>
    /// Test that GetAllCollectionConfigs returns all registered collections.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MapContentCollection_GetAllConfigs_ReturnsAllCollections()
    {
        // Arrange - create a client with multiple collections
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("collection1", "TestStorage", "dir1")
            .MapContentCollection("collection2", "TestStorage", "dir2"));

        // Act - request all collection configurations (empty array)
        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference()),
            o => o.WithTarget(client.Address),
            TestContext.Current.CancellationToken);

        // Assert
        response.Should().NotBeNull();
        response.Message.Should().NotBeNull();

        Output.WriteLine($"Response data type: {response.Message.Data?.GetType().FullName ?? "null"}");

        response.Message.Data.Should().NotBeNull();

        var configs = response.Message.Data as IReadOnlyCollection<ContentCollectionConfig>;
        configs.Should().NotBeNull();
        configs.Should().HaveCountGreaterThanOrEqualTo(2, "Should have at least 2 collections");

        configs.Should().Contain(c => c.Name == "collection1");
        configs.Should().Contain(c => c.Name == "collection2");
    }
}
