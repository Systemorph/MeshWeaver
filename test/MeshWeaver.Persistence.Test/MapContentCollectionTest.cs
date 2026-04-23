using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Persistence.Test;

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

    /// <summary>
    /// Test that accessing content through a mapped collection correctly resolves the path.
    /// When "content" is mapped to "TestStorage" with subdirectory "images",
    /// accessing "content:logo.svg" should resolve to "TestStorage:images/logo.svg".
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MapContentCollection_AccessContent_ResolvesPathCorrectly()
    {
        // Arrange - create subdirectory and test file
        var imagesPath = Path.Combine(_testBasePath, "images");
        Directory.CreateDirectory(imagesPath);
        var testFilePath = Path.Combine(imagesPath, "logo.svg");
        var testContent = "<svg><circle cx='50' cy='50' r='40'/></svg>";
        await File.WriteAllTextAsync(testFilePath, testContent, TestContext.Current.CancellationToken);

        Output.WriteLine($"Created test file: {testFilePath}");

        // Create client with "content" mapped to "TestStorage" with subdirectory "images"
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("content", "TestStorage", "images"));

        // Act - get the content collection and read the file via mapped name
        var contentService = client.ServiceProvider.GetRequiredService<IContentService>();
        var collection = await contentService.GetCollectionAsync("content", TestContext.Current.CancellationToken);

        collection.Should().NotBeNull("Content collection should be resolved");

        // Read "logo.svg" via the mapped "content" collection
        // This should resolve to TestStorage:images/logo.svg
        await using var stream = await collection!.GetContentAsync("logo.svg", TestContext.Current.CancellationToken);

        stream.Should().NotBeNull("Content should be found via mapped path");

        using var reader = new StreamReader(stream!);
        var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Assert
        content.Should().Be(testContent, "Content should match the file we created");
    }

    /// <summary>
    /// Test that accessing nested paths through a mapped collection works correctly.
    /// When "content" is mapped to "TestStorage" with subdirectory "media",
    /// accessing "content:icons/app.png" should resolve to "TestStorage:media/icons/app.png".
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MapContentCollection_AccessNestedContent_ResolvesPathCorrectly()
    {
        // Arrange - create nested subdirectory and test file
        var nestedPath = Path.Combine(_testBasePath, "media", "icons");
        Directory.CreateDirectory(nestedPath);
        var testFilePath = Path.Combine(nestedPath, "app.txt");
        var testContent = "Test nested content";
        await File.WriteAllTextAsync(testFilePath, testContent, TestContext.Current.CancellationToken);

        Output.WriteLine($"Created test file: {testFilePath}");

        // Create client with "content" mapped to "TestStorage" with subdirectory "media"
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("content", "TestStorage", "media"));

        // Act - read nested file via mapped collection
        var contentService = client.ServiceProvider.GetRequiredService<IContentService>();
        var collection = await contentService.GetCollectionAsync("content", TestContext.Current.CancellationToken);

        collection.Should().NotBeNull();

        // Access "icons/app.txt" which should resolve to "TestStorage:media/icons/app.txt"
        await using var stream = await collection!.GetContentAsync("icons/app.txt", TestContext.Current.CancellationToken);

        stream.Should().NotBeNull("Nested content should be found via mapped path");

        using var reader = new StreamReader(stream!);
        var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Assert
        content.Should().Be(testContent);
    }

    /// <summary>
    /// Test that UCR content path without slash uses default "content" collection.
    /// When accessing "logo.svg" without a collection prefix, it should use "content" collection.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task UcrContentPath_WithoutSlash_UsesDefaultContentCollection()
    {
        // Arrange - create content collection mapped to "content" and a test file
        var contentPath = Path.Combine(_testBasePath, "images");
        Directory.CreateDirectory(contentPath);
        var testFilePath = Path.Combine(contentPath, "logo.svg");
        var testContent = "<svg><circle cx='50' cy='50' r='40'/></svg>";
        await File.WriteAllTextAsync(testFilePath, testContent, TestContext.Current.CancellationToken);

        Output.WriteLine($"Created test file: {testFilePath}");

        // Create client with "content" mapped to "TestStorage" with subdirectory "images"
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("content", "TestStorage", "images"));

        // Act - get the content service and try to resolve "logo.svg" directly
        // This simulates what happens when UCR parses "content:logo.svg" (no slash)
        var contentService = client.ServiceProvider.GetRequiredService<IContentService>();
        var collection = await contentService.GetCollectionAsync("content", TestContext.Current.CancellationToken);

        collection.Should().NotBeNull("Content collection should be resolved");

        // Read "logo.svg" via the "content" collection
        await using var stream = await collection!.GetContentAsync("logo.svg", TestContext.Current.CancellationToken);

        stream.Should().NotBeNull("Content should be found when using default collection");

        using var reader = new StreamReader(stream!);
        var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Assert
        content.Should().Be(testContent, "Content should match the file we created");
    }

    /// <summary>
    /// Test that UCR content path with explicit collection works correctly.
    /// When accessing "other/data.json", it should use "other" collection and "data.json" path.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task UcrContentPath_WithExplicitCollection_UsesSpecifiedCollection()
    {
        // Arrange - create two collections: "content" and "other"
        var contentPath = Path.Combine(_testBasePath, "content");
        var otherPath = Path.Combine(_testBasePath, "other");
        Directory.CreateDirectory(contentPath);
        Directory.CreateDirectory(otherPath);

        // Put different content in each
        await File.WriteAllTextAsync(Path.Combine(contentPath, "data.json"), "{\"source\":\"content\"}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(otherPath, "data.json"), "{\"source\":\"other\"}", TestContext.Current.CancellationToken);

        // Create client with both collections mapped
        var client = GetClient(c => c
            .AddData(data => data)
            .MapContentCollection("content", "TestStorage", "content")
            .MapContentCollection("other", "TestStorage", "other"));

        // Act - get the "other" collection
        var contentService = client.ServiceProvider.GetRequiredService<IContentService>();
        var collection = await contentService.GetCollectionAsync("other", TestContext.Current.CancellationToken);

        collection.Should().NotBeNull("Other collection should be resolved");

        // Read "data.json" from the "other" collection
        await using var stream = await collection!.GetContentAsync("data.json", TestContext.Current.CancellationToken);

        stream.Should().NotBeNull("Content should be found in 'other' collection");

        using var reader = new StreamReader(stream!);
        var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Assert - should get the content from "other" collection, not "content"
        content.Should().Contain("\"source\":\"other\"", "Should read from 'other' collection");
    }
}
