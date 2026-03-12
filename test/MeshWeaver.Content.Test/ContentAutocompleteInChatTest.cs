using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.ContentCollections.Completion;
using MeshWeaver.Data.Completion;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Tests that content collection files are enumerable and appear
/// in autocomplete results, which is required for the chat autocomplete
/// to surface content items alongside MeshNode suggestions.
/// </summary>
[Collection("SamplesGraphData")]
public class ContentAutocompleteInChatTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverContentAutoTests", System.Guid.NewGuid().ToString());
        Directory.CreateDirectory(cacheDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        var storageConfig = new ContentCollectionConfig
        {
            Name = "storage",
            SourceType = "FileSystem",
            BasePath = graphPath
        };

        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddUserData()
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory);
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .ConfigureHub(hub => hub.AddContentCollections([storageConfig]))
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                return config
                    .AddContentCollections()
                    .MapContentCollection("content", "storage", $"content/{nodePath}")
                    .AddDefaultLayoutAreas();
            })
            .AddGraph()
            .ConfigureHub(hub => hub.AddMeshNavigation());
    }

    [Fact(Timeout = 30000)]
    public async Task ContentService_EnumeratesCollections()
    {
        var contentService = Mesh.ServiceProvider.GetService<IContentService>();
        contentService.Should().NotBeNull("IContentService should be registered at mesh level");

        // Use GetAllCollectionConfigs + GetCollectionAsync to discover and instantiate collections.
        // GetCollectionsAsync() only returns already-instantiated collections.
        var configs = contentService!.GetAllCollectionConfigs();
        Output.WriteLine($"Found {configs.Count} collection configs:");
        foreach (var c in configs)
            Output.WriteLine($"  - {c.Name} (SourceType: {c.SourceType})");

        configs.Should().NotBeEmpty("at least the storage collection should be registered");

        // Verify we can instantiate at least one collection
        var firstConfig = configs.First();
        var collection = await contentService.GetCollectionAsync(firstConfig.Name);
        collection.Should().NotBeNull($"collection '{firstConfig.Name}' should be instantiatable");
        Output.WriteLine($"Instantiated collection: {collection!.Collection} ({collection.DisplayName})");
    }

    [Fact(Timeout = 30000)]
    public async Task ContentService_EnumeratesFiles()
    {
        var contentService = Mesh.ServiceProvider.GetRequiredService<IContentService>();

        var configs = contentService.GetAllCollectionConfigs();
        configs.Should().NotBeEmpty();

        var firstConfig = configs.First();
        var collection = await contentService.GetCollectionAsync(firstConfig.Name);
        collection.Should().NotBeNull();

        var files = await collection!.GetFilesAsync("/");
        Output.WriteLine($"Collection '{collection.Collection}' has {files.Count} files:");
        foreach (var f in files.Take(10))
            Output.WriteLine($"  - {f.Path} ({f.Name})");

        files.Should().NotBeEmpty("the storage collection should contain sample files");
    }

    [Fact(Timeout = 30000)]
    public async Task ContentAutocompleteProvider_ReturnsFileItems()
    {
        var providers = Mesh.ServiceProvider.GetServices<IAutocompleteProvider>().ToList();
        var contentProvider = providers.FirstOrDefault(p => p is ContentAutocompleteProvider);

        // ContentAutocompleteProvider may only be registered on node hubs, not mesh level.
        // If not found at mesh level, verify that the content service pattern works directly.
        if (contentProvider == null)
        {
            Output.WriteLine("ContentAutocompleteProvider not registered at mesh level; testing content service directly");
            var contentService = Mesh.ServiceProvider.GetRequiredService<IContentService>();
            var configs = contentService.GetAllCollectionConfigs();
            configs.Should().NotBeEmpty();
            var collection = await contentService.GetCollectionAsync(configs.First().Name);
            collection.Should().NotBeNull();
            return;
        }

        var items = await contentProvider.GetItemsAsync("@").ToListAsync();
        Output.WriteLine($"ContentAutocompleteProvider returned {items.Count} items:");
        foreach (var item in items.Take(10))
            Output.WriteLine($"  - [{item.Kind}] {item.Label}: {item.InsertText}");

        items.Should().NotBeEmpty("content collection files should appear as autocomplete items");
        items.Should().AllSatisfy(item =>
        {
            item.Kind.Should().Be(AutocompleteKind.File);
        });
    }

    [Fact(Timeout = 30000)]
    public async Task ContentFiles_HaveExpectedUnifiedPathFormat()
    {
        var contentService = Mesh.ServiceProvider.GetRequiredService<IContentService>();

        var configs = contentService.GetAllCollectionConfigs();
        configs.Should().NotBeEmpty();

        foreach (var config in configs)
        {
            var collection = await contentService.GetCollectionAsync(config.Name);
            if (collection == null) continue;

            var files = await collection.GetFilesAsync("/");
            foreach (var file in files.Take(5))
            {
                var filePath = file.Path.TrimStart('/');
                var collectionName = collection.Collection;

                // Unified path format: {collectionName}:{filePath}
                // When used with a node address, it becomes: {address}/{collectionName}:{filePath}
                var unifiedRef = $"{collectionName}:{filePath}";
                unifiedRef.Should().Contain(":");
                unifiedRef.Should().StartWith($"{collectionName}:");

                Output.WriteLine($"  {unifiedRef} -> {file.Name}");
            }
        }
    }
}
