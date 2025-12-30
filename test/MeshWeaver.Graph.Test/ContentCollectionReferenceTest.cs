using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
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
/// Tests for ContentCollection configuration via CollectionConfigReference.
/// Uses the actual samples/Graph directory and its Person.json/Organization.json configurations.
/// </summary>
[Collection("ContentCollectionTests")]
public class ContentCollectionReferenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Use the actual samples/Graph directory
    private static string GetSamplesGraphPath()
    {
        // Navigate from test project to samples/Graph
        var currentDir = Directory.GetCurrentDirectory();
        var solutionRoot = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "samples", "Graph");
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = GetSamplesGraphPath();
        var dataDirectory = Path.Combine(graphPath, "Data");
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverContentCollectionTests", Guid.NewGuid().ToString(), ".mesh-cache");
        Directory.CreateDirectory(cacheDirectory);

        // Configure Graph:Storage to point to samples/Graph (parent of Data)
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(dataDirectory)
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory);
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddJsonGraphConfiguration(dataDirectory);
    }

    /// <summary>
    /// Test that Person hub can be initialized and responds to ping.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Person_Hub_CanBeInitialized()
    {
        var graphAddress = new Address("graph");
        var personAddress = new Address("Person");

        var client = GetClient(c => c.AddData(data => data));

        // Initialize graph hub first
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Initialize Person hub
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(personAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
    }

    /// <summary>
    /// Test that GetDataRequest with CollectionConfigReference returns the avatars collection.
    /// The content collections are registered at mesh level, so we send request to graph hub.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Graph_GetCollectionConfig_ReturnsAvatarsCollection()
    {
        var graphAddress = new Address("graph");

        var client = GetClient(c => c.AddData(data => data));

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Request collection configuration from graph hub (which has content collections configured)
        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference(["avatars"])),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Assert
        response.Should().NotBeNull("Response should not be null");
        response.Message.Should().NotBeNull("Response message should not be null");
        response.Message.Data.Should().NotBeNull("Response data should not be null");

        var configs = response.Message.Data as IReadOnlyCollection<ContentCollectionConfig>;
        configs.Should().NotBeNull("Data should be a collection of ContentCollectionConfig");
        configs.Should().HaveCount(1, "Should have exactly one collection config");

        var avatarsConfig = configs!.First();
        avatarsConfig.Name.Should().Be("avatars");
        avatarsConfig.SourceType.Should().Be("FileSystem");
        // BasePath should be the combined path: testDataDirectory + "persons"
        avatarsConfig.BasePath.Should().EndWith("persons");
    }

    /// <summary>
    /// Test that GetDataRequest with empty CollectionConfigReference returns all collections.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Graph_GetAllCollectionConfigs_ReturnsAllCollections()
    {
        var graphAddress = new Address("graph");

        var client = GetClient(c => c.AddData(data => data));

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Request all collection configurations (empty collection names)
        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference()),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Assert
        response.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        var configs = response.Message.Data as IReadOnlyCollection<ContentCollectionConfig>;
        configs.Should().NotBeNull();
        configs.Should().Contain(c => c.Name == "avatars", "Should contain avatars collection");
    }
}
