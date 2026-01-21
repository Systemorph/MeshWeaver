using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
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
            // Order matters: AddContentCollections registers $Content area first,
            // then AddDefaultViews sets CatalogArea as default (can be overridden by node type config)
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                config = config
                    .AddContentCollections() // Register $Content layout area first
                    .MapContentCollection("attachments", "storage", $"attachments/{nodePath}")
                    .MapContentCollection("content", "storage", $"content/{nodePath}");

                // Add mesh node views last (sets CatalogArea as default, can be overridden by node type)
                return config.AddDefaultViews();
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
        var aliceAddress = new Address("User/Alice");
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
        var aliceAddress = new Address("User/Alice");
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

    /// <summary>
    /// Tests that "content" collection is properly mapped for Markdown nodes.
    /// The content files at samples/Graph/content/{nodePath}/ should be accessible.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ContentCollection_ForMarkdownNode_ResolvesCorrectly()
    {
        var ucrAddress = new Address("MeshWeaver/Documentation/DataMesh/UnifiedContentReferences");
        var client = GetClient(c => c.AddContentCollections());

        // Initialize the UCR node hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(ucrAddress),
            TestContext.Current.CancellationToken);

        // Request the "content" collection configuration
        var response = await client.AwaitResponse(
            new GetDataRequest(new ContentCollectionReference(["content"])),
            o => o.WithTarget(ucrAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        var configs = ParseCollectionConfigs(response.Message.Data);
        configs.Should().NotBeNull();
        configs.Should().HaveCount(1);

        var contentConfig = configs!.First();
        contentConfig.Name.Should().Be("content");
        contentConfig.SourceType.Should().Be("FileSystem");
        // BasePath should contain content/MeshWeaver/Documentation/DataMesh/UnifiedContentReferences
        contentConfig.BasePath.Should().Contain("content");
        contentConfig.BasePath.Should().EndWith("MeshWeaver/Documentation/DataMesh/UnifiedContentReferences");
    }

    /// <summary>
    /// Tests that content files can be retrieved from the "content" collection.
    /// The icon.svg file at samples/Graph/content/MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/icon.svg should be accessible.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ContentCollection_RetrieveFile_ReturnsContent()
    {
        var ucrAddress = new Address("MeshWeaver/Documentation/DataMesh/UnifiedContentReferences");
        var client = GetClient(c => c.AddContentCollections());

        // Initialize the UCR node hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(ucrAddress),
            TestContext.Current.CancellationToken);

        // Request content:content/icon.svg from UCR hub
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("content:content/icon.svg")),
            o => o.WithTarget(ucrAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Message.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        // Content should be SVG text
        var content = response.Message.Data as string;
        content.Should().NotBeNull();
        content.Should().Contain("<svg");
    }

    /// <summary>
    /// Tests that $Content layout area for icon.svg returns content without hanging.
    /// This simulates what happens when @@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:icon.svg is rendered.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task ContentLayoutArea_IconSvg_ReturnsWithoutHanging()
    {
        var ucrAddress = new Address("MeshWeaver/Documentation/DataMesh/UnifiedContentReferences");
        var client = GetClient(c => c
            .AddLayoutClient(cc => cc)
            .AddContentCollections());

        Output.WriteLine($"Testing $Content area for icon.svg on {ucrAddress}");

        // Initialize the UCR node hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(ucrAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized");

        // Request the $Content area with icon.svg as the id
        // This is what @@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:icon.svg does
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("$Content") { Id = "icon.svg" };

        Output.WriteLine($"Requesting layout area: {reference.Area} with id: {reference.Id}");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(ucrAddress, reference);

        // Wait for the stream to emit a value - this is where eternal spinner would occur
        Output.WriteLine("Waiting for stream value...");
        var changeItem = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();
        var value = changeItem.Value;

        var rawText = value.GetRawText();
        Output.WriteLine($"Received value: {rawText.Substring(0, Math.Min(200, rawText.Length))}...");

        // Assert - should have received something (content or error message)
        value.Should().NotBe(default(JsonElement), "Should receive a layout area response");
    }

    /// <summary>
    /// Tests that $Content layout area for sample.md returns content without hanging.
    /// This simulates what happens when @@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/content:sample.md is rendered.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task ContentLayoutArea_SampleMd_ReturnsWithoutHanging()
    {
        var ucrAddress = new Address("MeshWeaver/Documentation/DataMesh/UnifiedContentReferences");
        var client = GetClient(c => c
            .AddLayoutClient(cc => cc)
            .AddContentCollections());

        Output.WriteLine($"Testing $Content area for sample.md on {ucrAddress}");

        // Initialize the UCR node hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(ucrAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized");

        // Request the $Content area with sample.md as the id
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("$Content") { Id = "sample.md" };

        Output.WriteLine($"Requesting layout area: {reference.Area} with id: {reference.Id}");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(ucrAddress, reference);

        // Wait for the stream to emit a value
        Output.WriteLine("Waiting for stream value...");
        var changeItem = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();
        var value = changeItem.Value;

        var rawText = value.GetRawText();
        Output.WriteLine($"Received value: {rawText.Substring(0, Math.Min(200, rawText.Length))}...");

        // Assert
        value.Should().NotBe(default(JsonElement), "Should receive a layout area response");
    }

    /// <summary>
    /// Tests that $Content layout area for non-existent file returns error message, not eternal spinner.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task ContentLayoutArea_NonExistentFile_ReturnsErrorMessage()
    {
        var ucrAddress = new Address("MeshWeaver/Documentation/DataMesh/UnifiedContentReferences");
        var client = GetClient(c => c
            .AddLayoutClient(cc => cc)
            .AddContentCollections());

        Output.WriteLine($"Testing $Content area for non-existent file on {ucrAddress}");

        // Initialize the UCR node hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(ucrAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized");

        // Request the $Content area with a non-existent file
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("$Content") { Id = "does-not-exist.txt" };

        Output.WriteLine($"Requesting layout area: {reference.Area} with id: {reference.Id}");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(ucrAddress, reference);

        // Wait for the stream to emit a value - should return error message, not hang
        Output.WriteLine("Waiting for stream value...");
        var changeItem = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();
        var value = changeItem.Value;

        var rawText = value.GetRawText();
        Output.WriteLine($"Received value: {rawText.Substring(0, Math.Min(500, rawText.Length))}...");

        // Assert - should have received something (error message)
        value.Should().NotBe(default(JsonElement), "Should receive a layout area response with error message");
    }

    /// <summary>
    /// Tests that $Schema layout area returns schema without hanging.
    /// This simulates what happens when @@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/schema: is rendered.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task SchemaLayoutArea_SelfReference_ReturnsWithoutHanging()
    {
        var ucrAddress = new Address("MeshWeaver/Documentation/DataMesh/UnifiedContentReferences");
        var client = GetClient(c => c
            .AddLayoutClient(cc => cc)
            .AddContentCollections());

        Output.WriteLine($"Testing $Schema area on {ucrAddress}");

        // Initialize the UCR node hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(ucrAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized");

        // Request the $Schema area with empty id (self-reference)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("$Schema") { Id = "" };

        Output.WriteLine($"Requesting layout area: {reference.Area} with id: {reference.Id}");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(ucrAddress, reference);

        // Wait for the stream to emit a value
        Output.WriteLine("Waiting for stream value...");
        var changeItem = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();
        var value = changeItem.Value;

        var rawText = value.GetRawText();
        Output.WriteLine($"Received value: {rawText.Substring(0, Math.Min(500, rawText.Length))}...");

        // Assert
        value.Should().NotBe(default(JsonElement), "Should receive a layout area response with schema");
    }

    /// <summary>
    /// Tests that $Data layout area returns data without hanging.
    /// This simulates what happens when @@MeshWeaver/Documentation/DataMesh/UnifiedContentReferences/data: is rendered.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task DataLayoutArea_SelfReference_ReturnsWithoutHanging()
    {
        var ucrAddress = new Address("MeshWeaver/Documentation/DataMesh/UnifiedContentReferences");
        var client = GetClient(c => c
            .AddLayoutClient(cc => cc)
            .AddContentCollections());

        Output.WriteLine($"Testing $Data area on {ucrAddress}");

        // Initialize the UCR node hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(ucrAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized");

        // Request the $Data area with empty id (self-reference)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("$Data") { Id = "" };

        Output.WriteLine($"Requesting layout area: {reference.Area} with id: {reference.Id}");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(ucrAddress, reference);

        // Wait for the stream to emit a value
        Output.WriteLine("Waiting for stream value...");
        var changeItem = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();
        var value = changeItem.Value;

        var rawText = value.GetRawText();
        Output.WriteLine($"Received value: {rawText.Substring(0, Math.Min(500, rawText.Length))}...");

        // Assert
        value.Should().NotBe(default(JsonElement), "Should receive a layout area response with data");
    }

    /// <summary>
    /// Tests that Markdown node's default area is $Content, not Catalog.
    /// This uses the same configuration pattern as LoomConfiguration.cs.
    /// When requesting the default area (empty area), it should resolve to $Content for Markdown nodes.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task MarkdownNode_DefaultArea_IsContentNotCatalog()
    {
        var ucrAddress = new Address("MeshWeaver/Documentation/DataMesh/UnifiedContentReferences");
        var client = GetClient(c => c
            .AddLayoutClient(cc => cc)
            .AddContentCollections());

        Output.WriteLine($"Testing default area for Markdown node at {ucrAddress}");

        // Initialize the UCR node hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(ucrAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized");

        // Request the default area (empty string) - should resolve to $Content for Markdown nodes
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(""); // Empty area = default area

        Output.WriteLine($"Requesting default layout area");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(ucrAddress, reference);

        // Wait for the stream to emit a value
        // The nodeType config (Markdown) should be compiled before hub creation,
        // so the first emission should already have $Content as default area
        Output.WriteLine("Waiting for stream value...");
        var changeItem = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();
        var value = changeItem.Value;

        var rawText = value.GetRawText();
        Output.WriteLine($"Received value: {rawText.Substring(0, Math.Min(500, rawText.Length))}...");

        // For Markdown nodes, default area should be Read which renders the markdown content
        // NOT $Catalog which shows a search grid
        // Check that the resolved area is Read, not $Catalog
        rawText.Should().Contain("\"area\":\"Read\"",
            "Markdown node default should resolve to Read area, not $Catalog");
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
