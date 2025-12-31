using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
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
/// Tests for ContentCollection configuration via CollectionConfigReference.
/// Uses the actual samples/Graph directory and its Person.json/Organization.json configurations.
///
/// The configuration uses MapContentCollection with hub address:
/// - Person: MapContentCollection("avatars", "Graph:Storage", $"persons/{config.Address.Segments.Last()}")
/// - Organization: MapContentCollection("logos", "Graph:Storage", $"logos/{config.Address.Segments.Last()}")
///
/// This means collections are configured on INSTANCE hubs (Alice, ACME), not NodeType hubs (Person, Organization).
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

        Output.WriteLine($"Graph path: {graphPath}");
        Output.WriteLine($"Data directory: {dataDirectory}");
        Output.WriteLine($"Cache directory: {cacheDirectory}");

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
    /// Test that Person NodeType hub can be initialized and responds to ping.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Person_NodeType_CanBeInitialized()
    {
        var personAddress = new Address("Person");

        var client = GetClient(c => c.AddData(data => data));

        // Initialize Person NodeType hub - this should compile Person.json's Configuration
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(personAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
    }

    /// <summary>
    /// Test that a Person instance hub (Alice) can be initialized and responds to ping.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Person_Instance_CanBeInitialized()
    {
        var aliceAddress = new Address("Alice");

        var client = GetClient(c => c.AddData(data => data));

        // Initialize Alice instance hub
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
    }

    /// <summary>
    /// Test that the "collection:avatars" unified path can be resolved from a Person instance hub (Alice).
    ///
    /// Person.json has Configuration:
    /// "config => config.WithContentType<Person>().AddNodeTypeView().MapContentCollection(\"avatars\", \"Graph:Storage\", $\"persons/{config.Address.Segments.Last()}\")"
    ///
    /// This test verifies the full integration:
    /// 1. Alice.json is loaded from samples/Graph/Data
    /// 2. Person's NodeTypeDefinition.Configuration is compiled and applied to Alice hub
    /// 3. MapContentCollection registers the "avatars" collection pointing to persons/Alice
    /// 4. The "collection:" unified reference resolver returns the collection config
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Person_Instance_CollectionAvatars_ReturnsCollectionConfig()
    {
        var aliceAddress = new Address("Alice");

        Output.WriteLine("Creating client...");
        var client = GetClient(c => c.AddData(data => data));

        Output.WriteLine("Sending PingRequest to Alice hub...");
        // Initialize Alice instance hub - this triggers compilation of Person.json's Configuration
        var pingResponse = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);
        pingResponse.Should().NotBeNull("Alice hub should respond to ping");
        Output.WriteLine("Alice hub initialized successfully");

        // Request the "avatars" collection configuration via GetDataRequest with UnifiedReference
        Output.WriteLine("Requesting collection config via GetDataRequest with UnifiedReference...");
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("collection:avatars")),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        // Assert
        response.Should().NotBeNull("Response should not be null");
        Output.WriteLine($"Response received: {response.Message?.GetType().Name}");

        response.Message.Should().NotBeNull("Response message should not be null");
        response.Message.Data.Should().NotBeNull("Response data should not be null");

        // Handle both direct type and JSON serialized response
        IReadOnlyCollection<ContentCollectionConfig>? configs;
        if (response.Message.Data is JsonElement jsonElement)
        {
            // Parse JSON manually to avoid Address deserialization issues
            configs = jsonElement.EnumerateArray()
                .Select(e => new ContentCollectionConfig
                {
                    Name = e.GetProperty("name").GetString() ?? "",
                    SourceType = e.GetProperty("sourceType").GetString() ?? "",
                    BasePath = e.GetProperty("basePath").GetString()
                })
                .ToArray();
        }
        else
        {
            configs = response.Message.Data as IReadOnlyCollection<ContentCollectionConfig>;
        }

        configs.Should().NotBeNull($"Data should be a collection of ContentCollectionConfig, but was {response.Message.Data?.GetType().FullName}");
        Output.WriteLine($"Received {configs?.Count ?? 0} collection configs");

        configs.Should().HaveCount(1, "Should have exactly one collection config for 'avatars'");

        var avatarsConfig = configs!.First();
        Output.WriteLine($"Avatars config: Name={avatarsConfig.Name}, SourceType={avatarsConfig.SourceType}, BasePath={avatarsConfig.BasePath}");

        avatarsConfig.Name.Should().Be("avatars");
        avatarsConfig.SourceType.Should().Be("FileSystem");
        // BasePath should end with "persons/Alice" (forward slash as used in the config)
        avatarsConfig.BasePath.Should().Contain("persons").And.EndWith("Alice");
    }

    /// <summary>
    /// Test that GetDataRequest with empty CollectionConfigReference returns all collections from Alice hub.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Person_Instance_GetAllCollectionConfigs_ReturnsAllCollections()
    {
        var aliceAddress = new Address("Alice");

        var client = GetClient(c => c.AddData(data => data));

        Output.WriteLine("Sending PingRequest to Alice hub...");
        // Initialize Alice instance hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Alice hub initialized");

        // Request all collection configurations (empty collection names)
        Output.WriteLine("Requesting all collection configs...");
        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference()),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        // Assert
        response.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        // Handle both direct type and JSON serialized response
        IReadOnlyCollection<ContentCollectionConfig>? configs;
        if (response.Message.Data is JsonElement jsonElement)
        {
            // Parse JSON manually to avoid Address deserialization issues
            configs = jsonElement.EnumerateArray()
                .Select(e => new ContentCollectionConfig
                {
                    Name = e.GetProperty("name").GetString() ?? "",
                    SourceType = e.GetProperty("sourceType").GetString() ?? "",
                    BasePath = e.GetProperty("basePath").GetString()
                })
                .ToArray();
        }
        else
        {
            configs = response.Message.Data as IReadOnlyCollection<ContentCollectionConfig>;
        }

        configs.Should().NotBeNull($"Data should be a collection of ContentCollectionConfig, but was {response.Message.Data?.GetType().FullName}");
        Output.WriteLine($"Received {configs?.Count ?? 0} collection configs");
        foreach (var config in configs ?? [])
        {
            Output.WriteLine($"  - {config.Name}: {config.SourceType} at {config.BasePath}");
        }

        configs.Should().Contain(c => c.Name == "avatars", "Should contain avatars collection");
    }

    /// <summary>
    /// Test that Organization instance hub's (ACME) "logos" collection is accessible.
    ///
    /// Organization.json has Configuration:
    /// "config => config.WithContentType<Organization>().AddDefaultViews().MapContentCollection(\"logos\", \"Graph:Storage\", $\"logos/{config.Address.Segments.Last()}\")"
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Organization_Instance_CollectionLogos_ReturnsCollectionConfig()
    {
        var acmeAddress = new Address("ACME");

        var client = GetClient(c => c.AddData(data => data));

        Output.WriteLine("Sending PingRequest to ACME hub...");
        // Initialize ACME instance hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("ACME hub initialized");

        // Request the "logos" collection configuration
        Output.WriteLine("Requesting logos collection config...");
        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference(["logos"])),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        // Assert
        response.Should().NotBeNull("Response should not be null");
        response.Message.Should().NotBeNull("Response message should not be null");

        Output.WriteLine($"Response data type: {response.Message.Data?.GetType().FullName ?? "null"}");
        Output.WriteLine($"Response data: {response.Message.Data}");

        response.Message.Data.Should().NotBeNull("Response data should not be null");

        // Handle both direct type and JSON serialized response
        IReadOnlyCollection<ContentCollectionConfig>? configs;
        if (response.Message.Data is JsonElement jsonElement)
        {
            // Parse JSON manually to avoid Address deserialization issues
            configs = jsonElement.EnumerateArray()
                .Select(e => new ContentCollectionConfig
                {
                    Name = e.GetProperty("name").GetString() ?? "",
                    SourceType = e.GetProperty("sourceType").GetString() ?? "",
                    BasePath = e.GetProperty("basePath").GetString()
                })
                .ToArray();
        }
        else
        {
            configs = response.Message.Data as IReadOnlyCollection<ContentCollectionConfig>;
        }

        configs.Should().NotBeNull($"Data should be a collection of ContentCollectionConfig, but was {response.Message.Data?.GetType().FullName}");
        Output.WriteLine($"Received {configs?.Count ?? 0} collection configs");

        configs.Should().HaveCount(1, "Should have exactly one collection config for 'logos'");

        var logosConfig = configs!.First();
        Output.WriteLine($"Logos config: Name={logosConfig.Name}, SourceType={logosConfig.SourceType}, BasePath={logosConfig.BasePath}");

        logosConfig.Name.Should().Be("logos");
        logosConfig.SourceType.Should().Be("FileSystem");
        // BasePath should end with "logos/ACME" (forward slash as used in the config)
        logosConfig.BasePath.Should().Contain("logos").And.EndWith("ACME");
    }
}
