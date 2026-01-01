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
/// Tests for CollectionConfigReference resolution via GetDataRequest.
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
    /// Tests that GetDataRequest with CollectionConfigReference returns collection configurations
    /// from a Person instance hub (Alice).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GetDataRequest_WithCollectionConfigReference_ReturnsConfig()
    {
        var aliceAddress = new Address("Alice");
        var client = GetClient(c => c.AddData(data => data));

        // Initialize Alice hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        // Request the "avatars" collection configuration
        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference(["avatars"])),
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
    /// Tests that GetDataRequest with empty CollectionConfigReference returns all collections.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GetDataRequest_WithEmptyCollectionConfigReference_ReturnsAllCollections()
    {
        var aliceAddress = new Address("Alice");
        var client = GetClient(c => c.AddData(data => data));

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference()),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        var configs = ParseCollectionConfigs(response.Message.Data);
        configs.Should().NotBeNull();
        configs.Should().Contain(c => c.Name == "avatars");
    }

    /// <summary>
    /// Tests that Organization instance hub's (ACME) collection is accessible via CollectionConfigReference.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GetDataRequest_WithCollectionConfigReference_ForOrganization_ReturnsConfig()
    {
        var acmeAddress = new Address("ACME");
        var client = GetClient(c => c.AddData(data => data));

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference(["logos"])),
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
        var client = GetClient(c => c.AddData(data => data));

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
        var client = GetClient(c => c.AddData(data => data));

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
        var client = GetClient(c => c.AddData(data => data));

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
