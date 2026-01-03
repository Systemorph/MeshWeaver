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

        // Create a "TestStorage" collection config at mesh level
        var testStorageConfig = new ContentCollectionConfig
        {
            Name = "TestStorage",
            SourceType = "FileSystem",
            BasePath = _testBasePath
        };

        return builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            // Register the TestStorage collection at mesh level so clients can map from it
            .ConfigureHub(hub => hub.AddContentCollections([testStorageConfig]));
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
    [Fact(Timeout = 10000)]
    public async Task MapContentCollection_RegistersCollectionWithCorrectBasePath()
    {
        // Arrange - create a client with MapContentCollection configured on it
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("avatars", "TestStorage", "avatars/alice"));

        Output.WriteLine($"Client address: {client.Address}");

        // Act - request the avatars collection configuration from the client's own hub
        var response = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference(["avatars"])),
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
        // BasePath should end with avatars/alice (forward slashes as used in MapContentCollection)
        config.BasePath.Should().EndWith("avatars/alice");
    }

    /// <summary>
    /// Test that MapContentCollection with empty subdirectory uses the source base path.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MapContentCollection_WithEmptySubdirectory_UsesSourceBasePath()
    {
        // Arrange
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("storage", "TestStorage", ""));

        // Act
        var response = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference(["storage"])),
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
    /// Test that MapContentCollection returns null config when source collection doesn't exist.
    /// The mapped collection config will be null when the source collection is not found.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MapContentCollection_WithMissingSourceCollection_ReturnsNullConfig()
    {
        // Arrange - configure with a non-existent source collection
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("files", "NonExistentCollection", "subdir"));

        // Act - requesting the collection should return empty/null because source doesn't exist
        var response = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference(["files"])),
            o => o.WithTarget(client.Address),
            TestContext.Current.CancellationToken);

        // Assert - response should have no configs because the source collection wasn't found
        response.Should().NotBeNull();
        response.Message.Should().NotBeNull();

        var configs = response.Message.Data as IReadOnlyCollection<ContentCollectionConfig>;
        // configs may be null or empty because the mapped config couldn't be resolved
        configs.Should().BeNullOrEmpty("mapped config should not resolve when source collection doesn't exist");
    }

    /// <summary>
    /// Test that GetAllCollectionConfigs returns all registered collections.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MapContentCollection_GetAllConfigs_ReturnsAllCollections()
    {
        // Arrange - create a client with multiple collections
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("collection1", "TestStorage", "dir1")
            .MapContentCollection("collection2", "TestStorage", "dir2"));

        // Act - request all collection configurations (empty array)
        var response = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference()),
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
