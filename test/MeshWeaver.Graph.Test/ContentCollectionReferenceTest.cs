using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for ContentCollection configuration via CollectionConfigReference.
/// Verifies that content collections registered in NodeTypeDefinition.Configuration
/// can be retrieved via GetDataRequest with CollectionConfigReference.
/// </summary>
[Collection("ContentCollectionTests")]
public class ContentCollectionReferenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverContentCollectionTests");
    private string? _testDirectory;

    private string GetOrCreateTestDirectory()
    {
        if (_testDirectory == null)
        {
            _testDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);

            // Create avatars directory with a test file
            var avatarsDir = Path.Combine(_testDirectory, "persons");
            Directory.CreateDirectory(avatarsDir);
            File.WriteAllText(Path.Combine(avatarsDir, "alice.svg"), "<svg></svg>");
        }
        return _testDirectory;
    }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        await SetupPersonStructureAsync(persistence);
    }

    private async Task SetupPersonStructureAsync(IPersistenceService persistence)
    {
        // Create Person NodeType with avatars collection in Configuration
        var personTypeNode = new MeshNode("Person")
        {
            Name = "Person",
            NodeType = "NodeType",
            Description = "A person with profile and avatar",
            IconName = "Person",
            DisplayOrder = 5,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "Person",
                Namespace = "",
                DisplayName = "Person",
                IconName = "Person",
                Description = "A person with profile and avatar",
                DisplayOrder = 5,
                // Register avatars collection via Configuration lambda
                Configuration = "config => config.WithContentType<Person>().AddNodeTypeView().AddContentCollections(new ContentCollectionConfig { Name = \"avatars\", SourceType = \"FileSystem\", BasePath = \"persons\" })",
                ChildrenQuery = "$source=activity;nodeType==Person;$orderBy=lastAccessedAt:desc;$limit=20"
            }
        };
        await persistence.SaveNodeAsync(personTypeNode);

        // Create a Person instance
        var alice = new MeshNode("Alice")
        {
            Name = "Alice Chen",
            NodeType = "Person",
            Description = "Senior Software Engineer",
            IconName = "Person",
            IsPersistent = true,
            Content = new { Id = "Alice", Name = "Alice Chen", Avatar = "/static/Person/avatars/alice.svg" }
        };
        await persistence.SaveNodeAsync(alice);

        // Create graph root node
        var graphNode = MeshNode.FromPath("graph") with
        {
            Name = "Graph",
            NodeType = "type/graph",
            IsPersistent = true
        };
        await persistence.SaveNodeAsync(graphNode);

        // Create type/graph type definition
        var graphTypeNode = new MeshNode("graph", "type")
        {
            Name = "Graph",
            NodeType = "NodeType",
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "graph",
                Namespace = "type",
                DisplayName = "Graph"
            }
        };
        await persistence.SaveNodeAsync(graphTypeNode);

        var graphCodeConfig = new CodeConfiguration
        {
            Code = "public record Graph { }"
        };
        await persistence.SavePartitionObjectsAsync("type/graph", null, [graphCodeConfig]);

        // Create Person Code configuration
        var personCodeConfig = new CodeConfiguration
        {
            Code = "public record Person { public string Id { get; init; } public string Name { get; init; } public string Avatar { get; init; } }"
        };
        await persistence.SavePartitionObjectsAsync("Person", null, [personCodeConfig]);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();
        var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

        return builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory))
            .AddJsonGraphConfiguration(testDataDirectory);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (_testDirectory != null && Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
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
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Person_GetCollectionConfig_ReturnsAvatarsCollection()
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
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(personAddress),
            TestContext.Current.CancellationToken);

        // Request collection configuration
        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference(["avatars"])),
            o => o.WithTarget(personAddress),
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
        avatarsConfig.BasePath.Should().Be("persons");
    }

    /// <summary>
    /// Test that GetDataRequest with empty CollectionConfigReference returns all collections.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Person_GetAllCollectionConfigs_ReturnsAllCollections()
    {
        var graphAddress = new Address("graph");
        var personAddress = new Address("Person");

        var client = GetClient(c => c.AddData(data => data));

        // Initialize hubs
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(personAddress),
            TestContext.Current.CancellationToken);

        // Request all collection configurations (empty collection names)
        var response = await client.AwaitResponse(
            new GetDataRequest(new CollectionConfigReference()),
            o => o.WithTarget(personAddress),
            TestContext.Current.CancellationToken);

        // Assert
        response.Should().NotBeNull();
        response.Message.Data.Should().NotBeNull();

        var configs = response.Message.Data as IReadOnlyCollection<ContentCollectionConfig>;
        configs.Should().NotBeNull();
        configs.Should().Contain(c => c.Name == "avatars", "Should contain avatars collection");
    }
}
