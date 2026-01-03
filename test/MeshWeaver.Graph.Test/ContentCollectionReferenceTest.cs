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

        // Create the "storage" collection config that node types (Organization, Person) will map from
        var storageConfig = new ContentCollectionConfig
        {
            Name = "storage",
            SourceType = "FileSystem",
            BasePath = graphPath
        };

        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(dataDirectory)
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory);
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            // Register the storage collection at mesh level so node types can map from it
            .ConfigureHub(hub => hub.AddContentCollections([storageConfig]))
            // Configure default content collections for all node hubs
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                return config
                    .MapContentCollection("attachments", "storage", $"attachments/{nodePath}");
            })
            .AddJsonGraphConfiguration(dataDirectory);
    }

    /// <summary>
    /// Tests that GetDataRequest with ContentCollectionReference returns collection configurations
    /// from a Person instance hub (Alice).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task GetDataRequest_WithContentCollectionReference_ReturnsConfig()
    {
        var aliceAddress = new Address("Alice");
        var client = GetClient(c => c.AddContentCollections());

        // Initialize Alice hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        // Request the "attachments" collection configuration
        var response = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference(["attachments"])),
            o => o.WithTarget(aliceAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        var configs = ParseCollectionConfigs(response.Message.Data);
        configs.Should().NotBeNull();
        configs.Should().HaveCount(1);

        var attachmentsConfig = configs!.First();
        attachmentsConfig.Name.Should().Be("attachments");
        attachmentsConfig.SourceType.Should().Be("FileSystem");
        attachmentsConfig.BasePath.Should().Contain("attachments").And.EndWith("Alice");
    }

    /// <summary>
    /// Tests that GetDataRequest with empty ContentCollectionReference returns all collections.
    /// </summary>
    [Fact(Timeout = 10000)]
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
        configs.Should().Contain(c => c.Name == "attachments");
    }

    /// <summary>
    /// Tests that Organization instance hub's (ACME) collection is accessible via ContentCollectionReference.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task GetDataRequest_WithContentCollectionReference_ForOrganization_ReturnsConfig()
    {
        var acmeAddress = new Address("ACME");
        var client = GetClient(c => c.AddContentCollections());

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        var response = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference(["attachments"])),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        var configs = ParseCollectionConfigs(response.Message.Data);
        configs.Should().NotBeNull();
        configs.Should().HaveCount(1);

        var attachmentsConfig = configs!.First();
        attachmentsConfig.Name.Should().Be("attachments");
        attachmentsConfig.SourceType.Should().Be("FileSystem");
        attachmentsConfig.BasePath.Should().Contain("attachments").And.EndWith("ACME");
    }

    /// <summary>
    /// Tests that UnifiedReference with "collection:attachments" prefix resolves collection config from ACME.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task UnifiedReference_CollectionPrefix_ReturnsConfig()
    {
        var acmeAddress = new Address("ACME");
        var client = GetClient(c => c.AddContentCollections());

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        // Request collection:attachments from ACME hub using prefix:path format
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("collection:attachments")),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        var configs = ParseCollectionConfigs(response.Message.Data);
        configs.Should().NotBeNull();
        configs.Should().Contain(c => c.Name == "attachments");
    }

    /// <summary>
    /// Tests that UnifiedReference with "content:attachments/test.txt" prefix resolves file content from ACME.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task UnifiedReference_ContentPrefix_ReturnsFileContent()
    {
        var acmeAddress = new Address("ACME");
        var client = GetClient(c => c.AddContentCollections());

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        // Request content:attachments/test.txt from ACME hub using prefix:path format
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("content:attachments/test.txt")),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();
        // Content should be test text
        var content = response.Message.Data as string;
        content.Should().NotBeNull();
        content.Should().Contain("Test attachment");
    }

    /// <summary>
    /// Tests that UnifiedReference with "data:" prefix resolves to DataPathReference behavior.
    /// </summary>
    [Fact(Timeout = 10000)]
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
    /// Both ACME and Systemorph are Organizations which have the "attachments" collection.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ConcurrentCollectionRequests_WithUnifiedReference_AllSucceed()
    {
        var acmeAddress = new Address("ACME");
        var systemorphAddress = new Address("Systemorph");
        var client = GetClient(c => c.AddContentCollections());

        // Launch concurrent requests to both organizations WITHOUT pre-initializing them
        // This tests the race condition where multiple hubs are initialized simultaneously
        // Uses collection:name format (UnifiedReference)
        // Both ACME and Systemorph are Organizations which have "attachments" collection
        var tasks = new List<Task<IMessageDelivery<GetDataResponse>>>();

        for (int i = 0; i < 10; i++)
        {
            // Alternate between ACME and Systemorph - both request attachments collection
            var address = i % 2 == 0 ? acmeAddress : systemorphAddress;

            tasks.Add(client.AwaitResponse(
                new GetDataRequest(new UnifiedReference("collection:attachments")),
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
    [Fact(Timeout = 10000)]
    public async Task ConcurrentContentRequests_ToSameOrganization_AllSucceed()
    {
        var acmeAddress = new Address("ACME");
        var client = GetClient(c => c.AddContentCollections());

        // Launch concurrent content requests WITHOUT pre-initializing
        var tasks = new List<Task<IMessageDelivery<GetDataResponse>>>();

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(client.AwaitResponse(
                new GetDataRequest(new UnifiedReference("content:attachments/test.txt")),
                o => o.WithTarget(acmeAddress),
                TestContext.Current.CancellationToken));
        }

        // Wait for all requests to complete
        var responses = await Task.WhenAll(tasks);

        // Verify all requests succeeded with test content
        foreach (var response in responses)
        {
            response.Should().NotBeNull();
            response.Message.Should().NotBeNull();
            response.Message.Data.Should().NotBeNull();

            var content = response.Message.Data as string;
            content.Should().NotBeNull();
            content.Should().Contain("Test attachment");
        }
    }

    /// <summary>
    /// Tests that simulate what BlazorHostingExtensions.MapStaticContent does:
    /// 1. Get collection config from target hub (ACME or Systemorph)
    /// 2. Add config to portal's content service
    /// 3. Retrieve content from the collection
    ///
    /// This test verifies that ACME/attachments and Systemorph/attachments are stored separately
    /// and don't overwrite each other when added to the portal's content service.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task StaticContentFlow_MultipleOrganizations_ReturnsCorrectContent()
    {
        var acmeAddress = new Address("ACME");
        var systemorphAddress = new Address("Systemorph");
        var client = GetClient(c => c.AddContentCollections());

        // Initialize both hubs
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(systemorphAddress),
            TestContext.Current.CancellationToken);

        // Step 1: Get collection config from ACME (like BlazorHostingExtensions does)
        var acmeConfigResponse = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference(["attachments"])),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        var acmeConfigs = ParseCollectionConfigs(acmeConfigResponse.Message.Data);
        acmeConfigs.Should().NotBeNull();
        var acmeLogosConfig = acmeConfigs!.First();
        Output.WriteLine($"ACME attachments config: Name={acmeLogosConfig.Name}, BasePath={acmeLogosConfig.BasePath}");

        // Step 2: Get collection config from Systemorph
        var systemorphConfigResponse = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference(["attachments"])),
            o => o.WithTarget(systemorphAddress),
            TestContext.Current.CancellationToken);

        var systemorphConfigs = ParseCollectionConfigs(systemorphConfigResponse.Message.Data);
        systemorphConfigs.Should().NotBeNull();
        var systemorphLogosConfig = systemorphConfigs!.First();
        Output.WriteLine($"Systemorph attachments config: Name={systemorphLogosConfig.Name}, BasePath={systemorphLogosConfig.BasePath}");

        // Verify they have different base paths
        acmeLogosConfig.BasePath.Should().Contain("ACME", "ACME attachments should point to ACME directory");
        systemorphLogosConfig.BasePath.Should().Contain("Systemorph", "Systemorph attachments should point to Systemorph directory");
        acmeLogosConfig.BasePath.Should().NotBe(systemorphLogosConfig.BasePath, "ACME and Systemorph should have different base paths");

        // Step 3: Simulate what the portal does - add both configs to its content service
        // and verify we can retrieve content from both without collision
        var contentService = client.ServiceProvider.GetRequiredService<IContentService>();

        // Add ACME config with qualified name to avoid collision
        var acmeQualifiedConfig = acmeLogosConfig with
        {
            Name = $"{acmeAddress}/attachments",
            Address = acmeAddress
        };
        contentService.AddConfiguration(acmeQualifiedConfig);
        Output.WriteLine($"Added ACME config with qualified name: {acmeQualifiedConfig.Name}");

        // Add Systemorph config with qualified name
        var systemorphQualifiedConfig = systemorphLogosConfig with
        {
            Name = $"{systemorphAddress}/attachments",
            Address = systemorphAddress
        };
        contentService.AddConfiguration(systemorphQualifiedConfig);
        Output.WriteLine($"Added Systemorph config with qualified name: {systemorphQualifiedConfig.Name}");

        // Verify we can get both collections separately
        var acmeCollection = await contentService.GetCollectionAsync($"{acmeAddress}/attachments", TestContext.Current.CancellationToken);
        acmeCollection.Should().NotBeNull("ACME attachments collection should be retrievable");
        Output.WriteLine($"ACME collection retrieved: {acmeCollection?.Collection}");

        var systemorphCollection = await contentService.GetCollectionAsync($"{systemorphAddress}/attachments", TestContext.Current.CancellationToken);
        systemorphCollection.Should().NotBeNull("Systemorph attachments collection should be retrievable");
        Output.WriteLine($"Systemorph collection retrieved: {systemorphCollection?.Collection}");

        // Verify they are different collections with different base paths
        acmeCollection!.Collection.Should().NotBe(systemorphCollection!.Collection,
            "ACME and Systemorph should have different collection names");
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
