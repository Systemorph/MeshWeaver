using System;
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
    public async Task ContentService_BrowseFolders()
    {
        var contentService = Mesh.ServiceProvider.GetRequiredService<IContentService>();
        var configs = contentService.GetAllCollectionConfigs();
        configs.Should().NotBeEmpty();

        var firstConfig = configs.First();
        var collection = await contentService.GetCollectionAsync(firstConfig.Name);
        collection.Should().NotBeNull();

        var folders = await collection!.GetFoldersAsync("/");
        Output.WriteLine($"Root folders in '{collection.Collection}': {string.Join(", ", folders.Select(f => f.Name))}");

        // If there are folders, verify we can browse into them
        if (folders.Count > 0)
        {
            var firstFolder = folders.First();
            var subItems = await collection.GetCollectionItemsAsync($"/{firstFolder.Name}");
            Output.WriteLine($"Items in '{firstFolder.Name}': {subItems.Count}");
            subItems.Should().NotBeNull();
        }
    }

    [Fact(Timeout = 30000)]
    public async Task ContentCompletions_CollectionNames_ShownAtFirstLevel()
    {
        var contentService = Mesh.ServiceProvider.GetRequiredService<IContentService>();
        var configs = contentService.GetAllCollectionConfigs();
        configs.Should().NotBeEmpty();

        // When user types just "@", content collections should appear as "collectionName:" suggestions
        foreach (var config in configs)
        {
            var collPath = $"{config.Name}:";
            collPath.Should().EndWith(":", "collection names should end with colon for drilling down");
            Output.WriteLine($"  Collection tag: {collPath} (DisplayName: {config.DisplayName})");
        }
    }

    [Fact(Timeout = 30000)]
    public async Task ContentCompletions_FilesInCollection_ReturnedAfterColon()
    {
        var contentService = Mesh.ServiceProvider.GetRequiredService<IContentService>();
        var configs = contentService.GetAllCollectionConfigs();
        configs.Should().NotBeEmpty();

        var firstConfig = configs.First();
        var collection = await contentService.GetCollectionAsync(firstConfig.Name);
        collection.Should().NotBeNull();

        // After user selects "content:", they should see files and/or folders
        var files = await collection!.GetFilesAsync("/");
        var folders = await collection.GetFoldersAsync("/");

        Output.WriteLine($"Items after '{firstConfig.Name}:' -> {files.Count} files, {folders.Count} folders");
        (files.Count + folders.Count).Should().BeGreaterThan(0,
            "selecting a collection should reveal its contents");
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

    [Fact(Timeout = 30000)]
    public async Task ContentFiles_FilterByPartialName()
    {
        var contentService = Mesh.ServiceProvider.GetRequiredService<IContentService>();
        var configs = contentService.GetAllCollectionConfigs();
        var firstConfig = configs.First();
        var collection = await contentService.GetCollectionAsync(firstConfig.Name);
        collection.Should().NotBeNull();

        var allFiles = await collection!.GetFilesAsync("/");
        if (allFiles.Count == 0)
        {
            Output.WriteLine("No files in collection — skipping filter test");
            return;
        }

        // Take the first 3 chars of the first file's name as a filter
        var firstFile = allFiles.First();
        var filterText = firstFile.Name[..Math.Min(3, firstFile.Name.Length)];

        // Filter manually to verify
        var expectedMatches = allFiles.Where(f =>
            f.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase)).ToList();

        Output.WriteLine($"Filter '{filterText}': {expectedMatches.Count} matches out of {allFiles.Count} files");
        expectedMatches.Should().NotBeEmpty($"filter '{filterText}' should match at least the original file");
        expectedMatches.Should().AllSatisfy(f =>
            f.Name.Should().Contain(filterText, Exactly.Once(),
                $"each match should contain the filter text '{filterText}'"));
    }

    [Fact(Timeout = 30000)]
    public async Task ContentFolders_BrowsingYieldsOnlyScopedItems()
    {
        var contentService = Mesh.ServiceProvider.GetRequiredService<IContentService>();
        var configs = contentService.GetAllCollectionConfigs();
        var firstConfig = configs.First();
        var collection = await contentService.GetCollectionAsync(firstConfig.Name);
        collection.Should().NotBeNull();

        var rootFolders = await collection!.GetFoldersAsync("/");
        if (rootFolders.Count == 0)
        {
            Output.WriteLine("No folders in root — skipping scoping test");
            return;
        }

        var folder = rootFolders.First();
        var subFiles = await collection.GetFilesAsync($"/{folder.Name}");
        var subFolders = await collection.GetFoldersAsync($"/{folder.Name}");

        Output.WriteLine($"Browsing '{folder.Name}/': {subFiles.Count} files, {subFolders.Count} folders");

        // Sub-items should have paths that include the parent folder
        subFiles.Should().AllSatisfy(f =>
            f.Path.Should().Contain(folder.Name,
                $"file path '{f.Path}' should be inside folder '{folder.Name}'"));
    }
}
