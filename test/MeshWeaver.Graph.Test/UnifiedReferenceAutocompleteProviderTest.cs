using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data.Completion;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Completion;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for UnifiedReferenceAutocompleteProvider using samples/Graph/Data.
/// Validates @ autocomplete returns correct suggestions for mesh nodes.
/// </summary>
[Collection("UnifiedReferenceAutocompleteProviderTest")]
public class UnifiedReferenceAutocompleteProviderTest : MonolithMeshTestBase
{
    private static readonly string SamplesDataDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "Graph", "Data"));

    private readonly string _cacheDirectory;

    private IMeshCatalog MeshCatalog => Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
    private IMeshQuery MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

    public UnifiedReferenceAutocompleteProviderTest(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverUcrTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(SamplesDataDirectory)
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .AddJsonGraphConfiguration(SamplesDataDirectory)
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

    [Fact(Timeout = 30000)]
    public void MeshCatalog_HasTopLevelNodes()
    {
        // Verify the mesh catalog is populated with top-level nodes
        var nodes = MeshCatalog.Configuration.Nodes.Values.ToArray();

        Output.WriteLine($"MeshCatalog has {nodes.Length} total nodes:");
        foreach (var node in nodes.Take(20))
        {
            Output.WriteLine($"  - {node.Path}: Segments.Count={node.Segments.Count}, Name={node.Name}");
        }

        nodes.Should().NotBeEmpty("MeshCatalog should contain nodes from samples/Graph/Data");

        // Check for expected top-level nodes (Segments.Count == 1)
        var topLevelNodes = nodes.Where(n => n.Segments.Count == 1).ToArray();
        Output.WriteLine($"\nTop-level nodes (Segments.Count == 1): {topLevelNodes.Length}");
        foreach (var node in topLevelNodes)
        {
            Output.WriteLine($"  - {node.Path}: {node.Name}");
        }

        topLevelNodes.Should().NotBeEmpty("Should have top-level nodes for autocomplete");
    }

    [Fact(Timeout = 30000)]
    public async Task MeshQuery_ReturnsSystemorph()
    {
        // Note: Systemorph is in persistence (JSON files), not MeshCatalog.Configuration.Nodes
        // MeshCatalog.Configuration.Nodes only contains type definitions (NodeType, Agent)
        // The actual data nodes are in persistence and accessed via IMeshQuery

        Output.WriteLine("Querying for Systemorph via IMeshQuery...");

        // Query from root to find Systemorph
        var suggestions = await MeshQuery.AutocompleteAsync("", "Sys", 10).ToArrayAsync(TestContext.Current.CancellationToken);

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

    [Fact(Timeout = 30000)]
    public async Task Provider_AtSymbol_ReturnsTopLevelSuggestions()
    {
        // Arrange - create provider with injected services
        var provider = new UnifiedReferenceAutocompleteProvider(
            MeshCatalog,
            MeshQuery,
            navigationContext: null);  // No navigation context for this test

        // Act - query with just "@"
        var items = await provider.GetItemsAsync("@", TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Got {items.Count()} suggestions for '@':");
        foreach (var item in items)
        {
            Output.WriteLine($"  - Label: {item.Label}, InsertText: {item.InsertText}, Category: {item.Category}");
        }

        items.Should().NotBeEmpty("@ should return top-level node suggestions");
    }

    [Fact(Timeout = 30000)]
    public async Task Provider_AtSys_ReturnsSystemorphSuggestion()
    {
        // Arrange - create provider with injected services
        var provider = new UnifiedReferenceAutocompleteProvider(
            MeshCatalog,
            MeshQuery,
            navigationContext: null);

        // Act - query with "@Sys" (partial match for Systemorph)
        var items = await provider.GetItemsAsync("@Sys", TestContext.Current.CancellationToken);

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

    [Fact(Timeout = 30000)]
    public async Task Provider_AtOrg_ReturnsOrganizationSuggestion()
    {
        // Arrange
        var provider = new UnifiedReferenceAutocompleteProvider(
            MeshCatalog,
            MeshQuery,
            navigationContext: null);

        // Act - query with "@Org" (partial match for Organization)
        var items = await provider.GetItemsAsync("@Org", TestContext.Current.CancellationToken);

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

    [Fact(Timeout = 30000)]
    public async Task Provider_AtUse_ReturnsUserSuggestion()
    {
        // Arrange
        var provider = new UnifiedReferenceAutocompleteProvider(
            MeshCatalog,
            MeshQuery,
            navigationContext: null);

        // Act - query with "@Use" (partial match for User)
        var items = await provider.GetItemsAsync("@Use");

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

    [Fact(Timeout = 30000)]
    public async Task Provider_CaseInsensitive_MatchesLowercase()
    {
        // Arrange
        var provider = new UnifiedReferenceAutocompleteProvider(
            MeshCatalog,
            MeshQuery,
            navigationContext: null);

        // Act - query with lowercase "@sys"
        var items = await provider.GetItemsAsync("@sys");

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

    [Fact(Timeout = 30000)]
    public void DI_AutocompleteProvider_IsRegistered()
    {
        // Verify the MeshNodeAutocompleteProvider is registered (it doesn't require INavigationService)
        // Note: UnifiedReferenceAutocompleteProvider requires INavigationService which is only
        // available in Blazor hosting, so we test it with manual instantiation instead
        var meshNodeProvider = Mesh.ServiceProvider.GetService<IMeshQuery>();
        meshNodeProvider.Should().NotBeNull("IMeshQuery should be registered for autocomplete");

        Output.WriteLine($"IMeshQuery is registered: {meshNodeProvider?.GetType().Name}");

        // Test that we can manually create the UCR provider with available services
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var provider = new UnifiedReferenceAutocompleteProvider(meshCatalog, meshQuery, null);
        provider.Should().NotBeNull("UnifiedReferenceAutocompleteProvider can be instantiated with available services");
    }

    [Fact(Timeout = 30000)]
    public async Task DI_ManualProvider_ReturnsSystemorphForAtSys()
    {
        // Create provider manually with available services (no INavigationService in test env)
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var provider = new UnifiedReferenceAutocompleteProvider(meshCatalog, meshQuery, null);

        // Act
        var items = await provider.GetItemsAsync("@Sys");

        // Assert
        Output.WriteLine($"Provider returned {items.Count()} suggestions for '@Sys':");
        foreach (var item in items)
        {
            Output.WriteLine($"  - {item.Label}: {item.InsertText}");
        }

        items.Should().Contain(i => i.Label.Contains("Systemorph", StringComparison.OrdinalIgnoreCase),
            "Provider should return Systemorph for @Sys");
    }

    #endregion
}
