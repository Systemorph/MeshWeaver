using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
/// Tests for ContentCollectionReference resolution via GetDataRequest.
/// </summary>
[Collection("ContentCollectionTests")]
public class ContentCollectionReferenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static string GetSamplesGraphPath()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var solutionRoot = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "samples", "Graph");
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = GetSamplesGraphPath();
        var dataDirectory = Path.Combine(graphPath, "Data");
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverContentCollectionTests", System.Guid.NewGuid().ToString(), ".mesh-cache");
        Directory.CreateDirectory(cacheDirectory);

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
    /// Tests that GetDataRequest with ContentCollectionReference returns collection configurations
    /// from a Person instance hub (Alice).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GetDataRequest_WithContentCollectionReference_ReturnsConfig()
    {
        var aliceAddress = new Address("Alice");
        var client = GetClient(c => c.AddContentCollections());

        // Initialize Alice hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        // Request the "avatars" collection configuration
        var response = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference(["avatars"])),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        var configs = ParseCollectionConfigs(response.Message.Data);
        configs.Should().NotBeNull();
        configs.Should().HaveCount(1);

        var avatarsConfig = configs!.First();
        avatarsConfig.Name.Should().Be("avatars");
        avatarsConfig.SourceType.Should().Be("FileSystem");
        avatarsConfig.BasePath.Should().Contain("persons").And.EndWith("Alice");
    }

    /// <summary>
    /// Tests that GetDataRequest with empty ContentCollectionReference returns all collections.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GetDataRequest_WithEmptyContentCollectionReference_ReturnsAllCollections()
    {
        var aliceAddress = new Address("Alice");
        var client = GetClient(c => c.AddContentCollections());

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        var response = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference()),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        var configs = ParseCollectionConfigs(response.Message.Data);
        configs.Should().NotBeNull();
        configs.Should().Contain(c => c.Name == "avatars");
    }

    /// <summary>
    /// Tests that Organization instance hub's (ACME) collection is accessible via ContentCollectionReference.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GetDataRequest_WithContentCollectionReference_ForOrganization_ReturnsConfig()
    {
        var acmeAddress = new Address("ACME");
        var client = GetClient(c => c.AddContentCollections());

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        var response = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference(["logos"])),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        var configs = ParseCollectionConfigs(response.Message.Data);
        configs.Should().NotBeNull();
        configs.Should().HaveCount(1);

        var logosConfig = configs!.First();
        logosConfig.Name.Should().Be("logos");
        logosConfig.SourceType.Should().Be("FileSystem");
        logosConfig.BasePath.Should().Contain("logos").And.EndWith("ACME");
    }

    /// <summary>
    /// Tests that UnifiedReference with "collection:logos" prefix resolves collection config from ACME.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task UnifiedReference_CollectionPrefix_ReturnsConfig()
    {
        var acmeAddress = new Address("ACME");
        var client = GetClient(c => c.AddContentCollections());

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        // Request collection:logos from ACME hub using prefix:path format
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("collection:logos")),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        var configs = ParseCollectionConfigs(response.Message.Data);
        configs.Should().NotBeNull();
        configs.Should().Contain(c => c.Name == "logos");
    }

    /// <summary>
    /// Tests that UnifiedReference with "content:logos/logo.svg" prefix resolves file content from ACME.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task UnifiedReference_ContentPrefix_ReturnsFileContent()
    {
        var acmeAddress = new Address("ACME");
        var client = GetClient(c => c.AddContentCollections());

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        // Request content:logos/logo.svg from ACME hub using prefix:path format
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("content:logos/logo.svg")),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();
        // Content should be SVG
        var content = response.Message.Data as string;
        content.Should().NotBeNull();
        content.Should().Contain("<svg");
    }

    /// <summary>
    /// Tests that UnifiedReference with "data:" prefix resolves to DataPathReference behavior.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task UnifiedReference_DataPrefix_ReturnsData()
    {
        var acmeAddress = new Address("ACME");
        var client = GetClient(c => c.AddContentCollections());

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        // Request data without prefix (defaults to data:) from ACME hub
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("data:")),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        // Should return default data (may be null or empty store)
        response.Should().NotBeNull();
    }

    /// <summary>
    /// Tests concurrent collection requests to multiple organizations using UnifiedReference.
    /// This test launches parallel requests to ACME and Systemorph simultaneously
    /// to verify thread-safe hub initialization with collection:name format.
    /// Both ACME and Systemorph are Organizations which have the "logos" collection.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ConcurrentCollectionRequests_WithUnifiedReference_AllSucceed()
    {
        var acmeAddress = new Address("ACME");
        var systemorphAddress = new Address("Systemorph");
        var client = GetClient(c => c.AddContentCollections());

        // Launch concurrent requests to both organizations WITHOUT pre-initializing them
        // This tests the race condition where multiple hubs are initialized simultaneously
        // Uses collection:name format (UnifiedReference)
        // Both ACME and Systemorph are Organizations which have "logos" collection
        var tasks = new List<Task<IMessageDelivery<GetDataResponse>>>();

        for (int i = 0; i < 10; i++)
        {
            // Alternate between ACME and Systemorph - both request logos collection
            var address = i % 2 == 0 ? acmeAddress : systemorphAddress;

            tasks.Add(client.AwaitResponse(
                new GetDataRequest(new UnifiedReference("collection:logos")),
                o => o.WithTarget(address),
                TestContext.Current.CancellationToken));
        }

        // Wait for all requests to complete
        var responses = await Task.WhenAll(tasks);

        // Verify all requests succeeded
        for (int i = 0; i < responses.Length; i++)
        {
            var response = responses[i];
            response.Should().NotBeNull($"Request {i} should not be null");
            response.Message.Should().NotBeNull($"Request {i} message should not be null");
            response.Message.Data.Should().NotBeNull($"Request {i} data should not be null");

            var configs = ParseCollectionConfigs(response.Message.Data);
            configs.Should().NotBeNull($"Request {i} configs should not be null");
            configs.Should().HaveCount(1, $"Request {i} should return exactly 1 config");
        }
    }

    /// <summary>
    /// Tests concurrent content file requests to verify thread-safe file resolution.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ConcurrentContentRequests_ToSameOrganization_AllSucceed()
    {
        var acmeAddress = new Address("ACME");
        var client = GetClient(c => c.AddContentCollections());

        // Launch concurrent content requests WITHOUT pre-initializing
        var tasks = new List<Task<IMessageDelivery<GetDataResponse>>>();

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(client.AwaitResponse(
                new GetDataRequest(new UnifiedReference("content:logos/logo.svg")),
                o => o.WithTarget(acmeAddress),
                TestContext.Current.CancellationToken));
        }

        // Wait for all requests to complete
        var responses = await Task.WhenAll(tasks);

        // Verify all requests succeeded with SVG content
        foreach (var response in responses)
        {
            response.Should().NotBeNull();
            response.Message.Should().NotBeNull();
            response.Message.Data.Should().NotBeNull();

            var content = response.Message.Data as string;
            content.Should().NotBeNull();
            content.Should().Contain("<svg");
        }
    }

    private static IReadOnlyCollection<ContentCollectionConfig>? ParseCollectionConfigs(object? data)
    {
        if (data is JsonElement jsonElement)
        {
            return jsonElement.EnumerateArray()
                .Select(e => new ContentCollectionConfig
                {
                    Name = e.GetProperty("name").GetString() ?? "",
                    SourceType = e.GetProperty("sourceType").GetString() ?? "",
                    BasePath = e.GetProperty("basePath").GetString()
                })
                .ToArray();
        }
        return data as IReadOnlyCollection<ContentCollectionConfig>;
    }
}
