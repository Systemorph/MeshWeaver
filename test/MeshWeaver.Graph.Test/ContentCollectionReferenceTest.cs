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
/// Tests for the unified path format: addressType/addressId/collection/collectionName
/// Verifies CollectionPathHandler and CollectionConfigReference resolution.
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
    /// Tests that UnifiedPathRegistry correctly parses collection keyword paths.
    /// Format: addressType/addressId/collection/collectionName
    /// </summary>
    [Fact]
    public void UnifiedPathRegistry_ParsesCollectionPath()
    {
        var registry = new UnifiedPathRegistry();
        registry.Register("collection", new CollectionPathHandler());
        registry.Register("area", new AreaPathHandler());

        // Path format: addressType/addressId/keyword/remainingPath
        var found = registry.TryResolve("Person/Alice/collection/avatars", out var address, out var reference);

        found.Should().BeTrue();
        address.Should().NotBeNull();
        address!.Type.Should().Be("Person");
        address.Id.Should().Be("Alice");
        reference.Should().BeOfType<CollectionConfigReference>();
        var collectionRef = (CollectionConfigReference)reference!;
        collectionRef.CollectionNames.Should().Contain("avatars");
    }

    /// <summary>
    /// Tests that UnifiedPathRegistry handles multiple collection names (comma-separated).
    /// </summary>
    [Fact]
    public void UnifiedPathRegistry_ParsesMultipleCollectionNames()
    {
        var registry = new UnifiedPathRegistry();
        registry.Register("collection", new CollectionPathHandler());
        registry.Register("area", new AreaPathHandler());

        var found = registry.TryResolve("Person/Alice/collection/avatars,photos", out var address, out var reference);

        found.Should().BeTrue();
        var collectionRef = reference.Should().BeOfType<CollectionConfigReference>().Subject;
        collectionRef.CollectionNames.Should().HaveCount(2);
        collectionRef.CollectionNames.Should().Contain("avatars");
        collectionRef.CollectionNames.Should().Contain("photos");
    }

    /// <summary>
    /// Tests that UnifiedPathRegistry handles empty collection path (get all collections).
    /// </summary>
    [Fact]
    public void UnifiedPathRegistry_ParsesEmptyCollectionPath()
    {
        var registry = new UnifiedPathRegistry();
        registry.Register("collection", new CollectionPathHandler());
        registry.Register("area", new AreaPathHandler());

        var found = registry.TryResolve("Person/Alice/collection", out var address, out var reference);

        found.Should().BeTrue();
        var collectionRef = reference.Should().BeOfType<CollectionConfigReference>().Subject;
        collectionRef.CollectionNames.Should().BeNullOrEmpty();
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
