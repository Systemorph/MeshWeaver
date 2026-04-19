using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data.Completion;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Autocomplete.Test;

/// <summary>
/// Integration tests for the full autocomplete pipeline:
/// - Multiple data sources (partitioned file system, in-memory, content collections)
/// - Chat input orchestrator (IChatCompletionOrchestrator) with all 3 sources
/// - Scoring: local first, shorter paths first, proximity-based
/// - Separator format: / preferred over : for content references
/// - Node delegation: @node/ returns children + keywords + layout areas
/// </summary>
[Collection("AutocompleteIntegrationTest")]
public class AutocompleteIntegrationTest : MonolithMeshTestBase
{
    private readonly string _cacheDirectory;

    public AutocompleteIntegrationTest(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverAutoIntTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
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
            .AddRowLevelSecurity()
            .AddSystemorph()
            .AddAcme()
            .AddUserData()
            .AddMeshNodes(TestUsers.PublicAdminAccess())
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory);
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

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_cacheDirectory))
        {
            try { Directory.Delete(_cacheDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    private new IMeshService MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IChatCompletionOrchestrator Orchestrator => Mesh.ServiceProvider.GetRequiredService<IChatCompletionOrchestrator>();

    #region Multi-Source Chat Autocomplete

    [Fact(Timeout = 30000)]
    public async Task ChatAutocomplete_AtText_ReturnsFromMultipleSources()
    {
        // Typing "@Sys" in chat — should get results from:
        // Source A: AutocompleteRequest to current hub (local providers)
        // Source B: Subtree query
        // Source C: Cross-partition broadening
        var batches = await Orchestrator
            .GetCompletionsAsync("@Sys", "Systemorph")
            .ToListAsync(TestContext.Current.CancellationToken);

        var allItems = batches.SelectMany(b => b.Items).ToList();

        Output.WriteLine($"Batches: {batches.Count}, Total items: {allItems.Count}");
        foreach (var batch in batches)
        {
            Output.WriteLine($"  Batch '{batch.Category}' (priority {batch.CategoryPriority}): {batch.Items.Count} items");
            foreach (var item in batch.Items.Take(3))
                Output.WriteLine($"    [{item.Priority}] {item.Label} => {item.InsertText}");
        }

        allItems.Should().NotBeEmpty("@Sys should return suggestions from at least one source");
        allItems.Should().Contain(i => i.InsertText.Contains("Systemorph", StringComparison.OrdinalIgnoreCase),
            "should find Systemorph partition");
    }

    [Fact(Timeout = 30000)]
    public async Task ChatAutocomplete_PartitionDrillDown_ShowsChildrenAndKeywords()
    {
        // Typing "@/ACME/" — should show children of ACME partition AND keyword suggestions
        var batches = await Orchestrator
            .GetCompletionsAsync("@/ACME/", null)
            .ToListAsync(TestContext.Current.CancellationToken);

        var allItems = batches.SelectMany(b => b.Items).ToList();

        Output.WriteLine($"Items for @/ACME/:");
        foreach (var item in allItems)
            Output.WriteLine($"  [{item.Priority}] {item.Label} => {item.InsertText} (category: {item.Category})");

        allItems.Should().NotBeEmpty("@/ACME/ should return children");

        // Should include keyword suggestions (content/, data/, schema/)
        var keywords = allItems.Where(i => i.Category == "Keywords").ToList();
        keywords.Should().NotBeEmpty("should include keyword suggestions like content/, data/");

        // Keywords should use / separator
        foreach (var kw in keywords)
        {
            kw.InsertText.Should().Contain("/",
                $"keyword '{kw.Label}' should use / separator");
        }
    }

    [Fact(Timeout = 30000)]
    public async Task ChatAutocomplete_LocalResultsFirst_HigherPriorityThanGlobal()
    {
        // When typing in the context of "Systemorph/Marketing", local results
        // should have higher priority than cross-partition results
        var batches = await Orchestrator
            .GetCompletionsAsync("@", "Systemorph/Marketing")
            .ToListAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Batches for '@' with context 'Systemorph/Marketing':");
        foreach (var batch in batches)
        {
            Output.WriteLine($"  Batch '{batch.Category}' (priority {batch.CategoryPriority}): {batch.Items.Count} items");
        }

        if (batches.Count >= 2)
        {
            var nearbyBatch = batches.FirstOrDefault(b => b.Category == "Nearby");
            var globalBatch = batches.FirstOrDefault(b => b.Category == "Global");

            if (nearbyBatch != null && globalBatch != null)
            {
                nearbyBatch.CategoryPriority.Should().BeGreaterThan(globalBatch.CategoryPriority,
                    "Nearby (local) results should have higher category priority than Global");
            }
        }
    }

    #endregion

    #region AutocompleteAsync — Score-First Ordering

    [Fact(Timeout = 30000)]
    public async Task AutocompleteAsync_ScoreFirstOrdering_HigherScoresFirst()
    {
        // AutocompleteAsync should now order by score descending, then path length
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "", 20, TestContext.Current.CancellationToken)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Top-level suggestions ({suggestions.Length}):");
        foreach (var s in suggestions)
            Output.WriteLine($"  [{s.Score:F0}] {s.Path} (len={s.Path.Length})");

        suggestions.Should().NotBeEmpty();

        // Verify score-first ordering
        for (int i = 0; i < suggestions.Length - 1; i++)
        {
            suggestions[i].Score.Should().BeGreaterThanOrEqualTo(suggestions[i + 1].Score,
                $"item '{suggestions[i].Path}' (score {suggestions[i].Score}) should sort before '{suggestions[i + 1].Path}' (score {suggestions[i + 1].Score})");
        }
    }

    [Fact(Timeout = 30000)]
    public async Task AutocompleteAsync_WithContext_ProximityBoostApplied()
    {
        // Items closer to contextPath should get boosted scores
        var withContext = await MeshQuery
            .AutocompleteAsync("ACME", "", AutocompleteMode.RelevanceFirst, 20, "ACME", ct: TestContext.Current.CancellationToken)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        var withoutContext = await MeshQuery
            .AutocompleteAsync("ACME", "", 20, TestContext.Current.CancellationToken)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"With context 'ACME': {withContext.Length} items");
        foreach (var s in withContext.Take(5))
            Output.WriteLine($"  [{s.Score:F0}] {s.Path}");

        Output.WriteLine($"Without context: {withoutContext.Length} items");
        foreach (var s in withoutContext.Take(5))
            Output.WriteLine($"  [{s.Score:F0}] {s.Path}");

        // With context should boost ACME children
        if (withContext.Length > 0 && withoutContext.Length > 0)
        {
            var contextScoreSum = withContext.Take(5).Sum(s => s.Score);
            var noContextScoreSum = withoutContext.Take(5).Sum(s => s.Score);
            contextScoreSum.Should().BeGreaterThanOrEqualTo(noContextScoreSum,
                "context-boosted scores should be at least as high");
        }
    }

    [Fact(Timeout = 30000)]
    public async Task AutocompleteAsync_ShorterPathsPreferred_WhenScoresEqual()
    {
        // When scores are the same, shorter paths should come first
        var suggestions = await MeshQuery
            .AutocompleteAsync("ACME", "", 30, TestContext.Current.CancellationToken)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"ACME children ({suggestions.Length}):");
        foreach (var s in suggestions)
            Output.WriteLine($"  [{s.Score:F0}] {s.Path} (segments={s.Path.Count(c => c == '/') + 1})");

        // Group by same score and verify path length ordering within each group
        var scoreGroups = suggestions.GroupBy(s => (int)s.Score);
        foreach (var group in scoreGroups.Where(g => g.Count() > 1))
        {
            var items = group.ToArray();
            for (int i = 0; i < items.Length - 1; i++)
            {
                items[i].Path.Length.Should().BeLessThanOrEqualTo(items[i + 1].Path.Length,
                    $"within score {group.Key}: '{items[i].Path}' should sort before '{items[i + 1].Path}'");
            }
        }
    }

    #endregion

    #region Content Provider — Slash Format

    [Fact(Timeout = 30000)]
    public async Task ContentProvider_InsertText_UsesSlashFormat()
    {
        // Content autocomplete should produce @content/path not @content:path
        var providers = Mesh.ServiceProvider.GetServices<IAutocompleteProvider>();
        var contentProvider = providers
            .FirstOrDefault(p => p.GetType().Name.Contains("ContentAutocomplete"));

        if (contentProvider == null)
        {
            Output.WriteLine("ContentAutocompleteProvider not registered at mesh level — skipping");
            return;
        }

        var items = await contentProvider
            .GetItemsAsync("@", null, TestContext.Current.CancellationToken)
            .Take(10)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Content items: {items.Length}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} => {item.InsertText}");

        foreach (var item in items)
        {
            if (item.InsertText.Contains("content"))
            {
                item.InsertText.Should().Contain("content/",
                    $"item '{item.Label}' should use content/ not content:");
                item.InsertText.Should().NotContain("content:",
                    $"item '{item.Label}' should not use legacy content: format");
            }
        }
    }

    #endregion

    #region Unified Reference — Keywords Use Slash

    [Fact(Timeout = 30000)]
    public async Task UnifiedReference_Keywords_UseSlashNotColon()
    {
        var providers = Mesh.ServiceProvider.GetServices<IAutocompleteProvider>();
        var unifiedProvider = providers
            .FirstOrDefault(p => p.GetType().Name.Contains("UnifiedReference"));
        unifiedProvider.Should().NotBeNull();

        // Type @/ACME/ProductLaunch/ to get keyword suggestions (needs 2+ completed segments)
        var items = await unifiedProvider!
            .GetItemsAsync("@/ACME/ProductLaunch/", null, TestContext.Current.CancellationToken)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        var keywords = items.Where(i => i.Category == "Keywords").ToArray();

        Output.WriteLine($"Keywords for @/ACME/ProductLaunch/:");
        foreach (var kw in keywords)
            Output.WriteLine($"  {kw.Label} => {kw.InsertText}");

        keywords.Should().NotBeEmpty("should have keyword suggestions");

        foreach (var kw in keywords)
        {
            kw.InsertText.Should().Contain("/",
                $"keyword '{kw.Label}' insertText should use / separator");
        }
    }

    #endregion

    #region UCR Prefix Resolver — Both Formats

    [Theory]
    [InlineData("content/logo.svg", true, "$Content", "logo.svg")]
    [InlineData("content:logo.svg", true, "$Content", "logo.svg")]
    [InlineData("data/Collection", true, "$Data", "Collection")]
    [InlineData("data:Collection", true, "$Data", "Collection")]
    [InlineData("schema/Type", true, "$Schema", "Type")]
    [InlineData("schema:Type", true, "$Schema", "Type")]
    [InlineData("model/My", true, "$Model", "My")]
    [InlineData("model:My", true, "$Model", "My")]
    [InlineData("menu/main", true, "$Menu", "main")]
    [InlineData("menu:main", true, "$Menu", "main")]
    [InlineData("unknown/path", false, null, null)]
    [InlineData("unknown:path", false, null, null)]
    public void UcrPrefixResolver_BothFormats_ResolveSame(
        string path, bool expectResolved, string? expectedArea, string? expectedRemaining)
    {
        var resolved = Data.UcrPrefixResolver.TryResolve(path, out var area, out var remaining);

        resolved.Should().Be(expectResolved, $"'{path}' resolution");
        area.Should().Be(expectedArea);
        remaining.Should().Be(expectedRemaining);
    }

    #endregion

    #region Cross-Partition — Multiple Partitions Queried

    [Fact(Timeout = 30000)]
    public async Task AutocompleteAsync_CrossPartition_ReturnsFromMultiplePartitions()
    {
        // Global autocomplete (empty basePath) should return nodes from multiple partitions
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "", 30, TestContext.Current.CancellationToken)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Global suggestions ({suggestions.Length}):");
        foreach (var s in suggestions)
            Output.WriteLine($"  [{s.Score:F0}] {s.Path} ({s.NodeType})");

        var partitions = suggestions
            .Select(s => s.Path.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Output.WriteLine($"\nPartitions found: {string.Join(", ", partitions)}");

        partitions.Length.Should().BeGreaterThanOrEqualTo(2,
            "global autocomplete should return results from multiple partitions (ACME, Systemorph, etc.)");
    }

    [Fact(Timeout = 30000)]
    public async Task ChatAutocomplete_GlobalFanOut_ReachesOtherPartitions()
    {
        // When typing "@ACM" from Systemorph context, broadening should find ACME
        var batches = await Orchestrator
            .GetCompletionsAsync("@ACM", "Systemorph")
            .ToListAsync(TestContext.Current.CancellationToken);

        var allItems = batches.SelectMany(b => b.Items).ToList();

        Output.WriteLine($"Items for '@ACM' from Systemorph context:");
        foreach (var batch in batches)
        {
            Output.WriteLine($"  Batch '{batch.Category}': {batch.Items.Count} items");
            foreach (var item in batch.Items.Take(3))
                Output.WriteLine($"    [{item.Priority}] {item.Label} => {item.InsertText}");
        }

        allItems.Should().Contain(i =>
            i.InsertText.Contains("ACME", StringComparison.OrdinalIgnoreCase),
            "broadening should find ACME from Systemorph context");
    }

    #endregion
}

[CollectionDefinition("AutocompleteIntegrationTest", DisableParallelization = true)]
public class AutocompleteIntegrationTestDefinition { }
