using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data.Completion;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for UnifiedReferenceAutocompleteProvider using samples/Graph/Data.
/// Validates @ autocomplete returns correct suggestions for mesh nodes.
/// </summary>
[Collection("UnifiedReferenceAutocompleteProviderTest")]
public class UnifiedReferenceAutocompleteProviderTest : MonolithMeshTestBase
{
    private readonly string _cacheDirectory;
    private IMessageHub Hub => Mesh.ServiceProvider.GetRequiredService<IMessageHub>();

    private MeshConfiguration MeshConfig => Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();

    public UnifiedReferenceAutocompleteProviderTest(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverUcrTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(TestPaths.SamplesGraphData)
            .AddSystemorph()
            .AddAcme()
            .AddUserData()
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .AddGraph()
            .ConfigureHub(hub => hub.AddMeshNavigation());  // Register the autocomplete provider
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (Directory.Exists(_cacheDirectory))
        {
            try { Directory.Delete(_cacheDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    #region Configuration Validation Tests

    [Fact(Timeout = 10000)]
    public void MeshConfig_HasTopLevelNodes()
    {
        // Verify the mesh configuration is populated with top-level nodes
        var nodes = MeshConfig.Nodes.Values.ToArray();

        Output.WriteLine($"MeshConfig has {nodes.Length} total nodes:");
        foreach (var node in nodes.Take(20))
        {
            Output.WriteLine($"  - {node.Path}: Segments.Count={node.Segments.Count}, Name={node.Name}");
        }

        nodes.Should().NotBeEmpty("MeshConfig should contain nodes from samples/Graph/Data");

        // Check for expected top-level nodes (Segments.Count == 1)
        var topLevelNodes = nodes.Where(n => n.Segments.Count == 1).ToArray();
        Output.WriteLine($"\nTop-level nodes (Segments.Count == 1): {topLevelNodes.Length}");
        foreach (var node in topLevelNodes)
        {
            Output.WriteLine($"  - {node.Path}: {node.Name}");
        }

        topLevelNodes.Should().NotBeEmpty("Should have top-level nodes for autocomplete");
    }

    [Fact(Timeout = 10000)]
    public async Task MeshQuery_ReturnsSystemorph()
    {
        // Note: Systemorph is in persistence (JSON files), not MeshCatalog.Configuration.Nodes
        // MeshCatalog.Configuration.Nodes only contains type definitions (NodeType, Agent)
        // The actual data nodes are in persistence and accessed via IMeshService

        Output.WriteLine("Querying for Systemorph via IMeshService...");

        // Query from root to find Systemorph
        var suggestions = await MeshQuery.AutocompleteAsync("", "Sys", 10, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {suggestions.Length} suggestions for 'Sys':");
        foreach (var s in suggestions)
        {
            Output.WriteLine($"  - {s.Path}: {s.Name} ({s.NodeType})");
        }

        var systemorphSuggestion = suggestions.FirstOrDefault(s =>
            s.Path.Equals("Systemorph", StringComparison.OrdinalIgnoreCase));

        systemorphSuggestion.Should().NotBeNull("Systemorph should be returned by MeshQuery autocomplete");
    }

    #endregion

    #region Provider Tests

    private IAutocompleteProvider GetUnifiedReferenceProvider()
    {
        // Get all autocomplete providers from DI and find the unified reference one
        var providers = Hub.ServiceProvider.GetServices<IAutocompleteProvider>().ToList();
        // The unified reference provider handles "@" queries - find it by checking type name
        var provider = providers.FirstOrDefault(p => p.GetType().Name.Contains("UnifiedReference"));
        provider.Should().NotBeNull("UnifiedReferenceAutocompleteProvider should be registered via AddMeshNavigation()");
        return provider!;
    }

    [Fact(Timeout = 10000)]
    public async Task Provider_AtSymbol_ReturnsTopLevelSuggestions()
    {
        // Arrange - get provider from DI
        var provider = GetUnifiedReferenceProvider();

        // Act - query with just "@"
        var items = await provider.GetItemsAsync("@", null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Got {items.Count()} suggestions for '@':");
        foreach (var item in items)
        {
            Output.WriteLine($"  - Label: {item.Label}, InsertText: {item.InsertText}, Category: {item.Category}");
        }

        items.Should().NotBeEmpty("@ should return top-level node suggestions");
    }

    [Fact(Timeout = 10000)]
    public async Task Provider_AtSys_ReturnsSystemorphSuggestion()
    {
        // Arrange - get provider from DI
        var provider = GetUnifiedReferenceProvider();

        // Act - query with "@Sys" (partial match for Systemorph)
        var items = await provider.GetItemsAsync("@Sys", null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Got {items.Count()} suggestions for '@Sys':");
        foreach (var item in items)
        {
            Output.WriteLine($"  - Label: {item.Label}, InsertText: {item.InsertText}, Priority: {item.Priority}");
        }

        items.Should().NotBeEmpty("@Sys should return suggestions");

        var systemorphItem = items.FirstOrDefault(i =>
            i.Label.Contains("Systemorph", StringComparison.OrdinalIgnoreCase) ||
            i.InsertText.Contains("Systemorph", StringComparison.OrdinalIgnoreCase));

        systemorphItem.Should().NotBeNull("@Sys should match Systemorph");
        Output.WriteLine($"\nMatched Systemorph: Label={systemorphItem?.Label}, InsertText={systemorphItem?.InsertText}");
    }

    [Fact(Timeout = 10000)]
    public async Task Provider_AtOrg_ReturnsOrganizationSuggestion()
    {
        // Arrange - get provider from DI
        var provider = GetUnifiedReferenceProvider();

        // Act - query with "@Org" (partial match for Organization)
        var items = await provider.GetItemsAsync("@Org", null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Got {items.Count()} suggestions for '@Org':");
        foreach (var item in items)
        {
            Output.WriteLine($"  - Label: {item.Label}, InsertText: {item.InsertText}");
        }

        var orgItem = items.FirstOrDefault(i =>
            i.Label.Contains("Organization", StringComparison.OrdinalIgnoreCase));

        orgItem.Should().NotBeNull("@Org should match Organization");
    }

    [Fact(Timeout = 10000)]
    public async Task Provider_AtUse_ReturnsUserSuggestion()
    {
        // Arrange - get provider from DI
        var provider = GetUnifiedReferenceProvider();

        // Act - query with "@Use" (partial match for User)
        var items = await provider.GetItemsAsync("@Use", null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Got {items.Count()} suggestions for '@Use':");
        foreach (var item in items)
        {
            Output.WriteLine($"  - Label: {item.Label}, InsertText: {item.InsertText}");
        }

        var userItem = items.FirstOrDefault(i =>
            i.Label.Contains("User", StringComparison.OrdinalIgnoreCase));

        userItem.Should().NotBeNull("@Use should match User");
    }

    [Fact(Timeout = 10000)]
    public async Task Provider_CaseInsensitive_MatchesLowercase()
    {
        // Arrange - get provider from DI
        var provider = GetUnifiedReferenceProvider();

        // Act - query with lowercase "@sys"
        var items = await provider.GetItemsAsync("@sys", null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Got {items.Count()} suggestions for '@sys' (lowercase):");
        foreach (var item in items)
        {
            Output.WriteLine($"  - Label: {item.Label}, InsertText: {item.InsertText}");
        }

        var systemorphItem = items.FirstOrDefault(i =>
            i.Label.Contains("Systemorph", StringComparison.OrdinalIgnoreCase));

        systemorphItem.Should().NotBeNull("@sys (lowercase) should match Systemorph (case-insensitive)");
    }

    #endregion

    #region DI Integration Tests

    [Fact(Timeout = 10000)]
    public void DI_AutocompleteProvider_IsRegistered()
    {
        // Verify the MeshNodeAutocompleteProvider is registered (it doesn't require INavigationService)
        var meshQuery = Mesh.ServiceProvider.GetService<IMeshService>();
        meshQuery.Should().NotBeNull("IMeshService should be registered for autocomplete");

        Output.WriteLine($"IMeshService is registered: {meshQuery?.GetType().Name}");

        // Test that the unified reference provider is available from DI
        var providers = Hub.ServiceProvider.GetServices<IAutocompleteProvider>().ToList();
        Output.WriteLine($"Found {providers.Count} IAutocompleteProvider instances");
        foreach (var p in providers)
        {
            Output.WriteLine($"  - {p.GetType().Name}");
        }

        var unifiedProvider = providers.FirstOrDefault(p => p.GetType().Name.Contains("UnifiedReference"));
        unifiedProvider.Should().NotBeNull("UnifiedReferenceAutocompleteProvider should be registered via AddMeshNavigation()");
    }

    [Fact(Timeout = 10000)]
    public async Task DI_Provider_ReturnsSystemorphForAtSys()
    {
        // Get provider from DI
        var provider = GetUnifiedReferenceProvider();

        // Act
        var items = await provider.GetItemsAsync("@Sys", null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Provider returned {items.Length} suggestions for '@Sys':");
        foreach (var item in items)
        {
            Output.WriteLine($"  - {item.Label}: {item.InsertText}");
        }

        items.Should().Contain(i => i.Label.Contains("Systemorph", StringComparison.OrdinalIgnoreCase),
            "Provider should return Systemorph for @Sys");
    }

    #endregion
}
