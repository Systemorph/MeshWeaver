using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Reactive;
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
    private static readonly string _cacheDirectory =
        Path.Combine(Path.GetTempPath(), "MeshWeaverAutoIntTests", Guid.NewGuid().ToString());
    static AutocompleteIntegrationTest() => Directory.CreateDirectory(_cacheDirectory);

    protected override bool ShareMeshAcrossTests => true;

    public AutocompleteIntegrationTest(ITestOutputHelper output) : base(output)
    {
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
            BasePath = graphPath,
            // Per-node MapContentCollection wrappers inherit ExposeInChildren
            // from the source; storage defaults to false since the wire-default
            // flip (95f840f34), so set it explicitly here.
            ExposeInChildren = true
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

    // Cache dir is class-static + shared SP — never deleted between tests.

    private new IMeshService MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IChatCompletionOrchestrator Orchestrator => Mesh.ServiceProvider.GetRequiredService<IChatCompletionOrchestrator>();

    #region Multi-Source Chat Autocomplete

    [Fact(Timeout = 30000)]
    public void ChatAutocomplete_AtText_ReturnsFromMultipleSources()
    {
        // Typing "@Sys" in chat — should get results from:
        // Source A: AutocompleteRequest to current hub (local providers)
        // Source B: Subtree query
        // Source C: Cross-partition broadening
        var batches = Orchestrator
            .GetCompletions("@Sys", "Systemorph")
            .ToList().Should().Within(30.Seconds()).Emit();

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
    public void ChatAutocomplete_PartitionDrillDown_ShowsChildrenAndKeywords()
    {
        // Typing "@/ACME/" — should show children of ACME partition AND keyword suggestions
        var batches = Orchestrator
            .GetCompletions("@/ACME/", null)
            .ToList().Should().Within(30.Seconds()).Emit();

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
    public void ChatAutocomplete_LocalResultsFirst_HigherPriorityThanGlobal()
    {
        // When typing in the context of "Systemorph/Marketing", local results
        // should have higher priority than cross-partition results
        var batches = Orchestrator
            .GetCompletions("@", "Systemorph/Marketing")
            .ToList().Should().Within(30.Seconds()).Emit();

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
    public void AutocompleteAsync_ScoreFirstOrdering_HigherScoresFirst()
    {
        // AutocompleteAsync should now order by score descending, then path length
        var suggestions = MeshQuery
            .AutocompleteAsync("", "", 20)
            .ToObservableSequence().ToList().Should().Within(30.Seconds()).Emit().ToArray();

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
    public void AutocompleteAsync_WithContext_ProximityBoostApplied()
    {
        // Items closer to contextPath should get boosted scores
        var withContext = MeshQuery
            .AutocompleteAsync("ACME", "", AutocompleteMode.RelevanceFirst, 20, "ACME")
            .ToObservableSequence().ToList().Should().Within(30.Seconds()).Emit().ToArray();

        var withoutContext = MeshQuery
            .AutocompleteAsync("ACME", "", 20)
            .ToObservableSequence().ToList().Should().Within(30.Seconds()).Emit().ToArray();

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
    public void AutocompleteAsync_ShorterPathsPreferred_WhenScoresEqual()
    {
        // When scores are the same, shorter paths should come first
        var suggestions = MeshQuery
            .AutocompleteAsync("ACME", "", 30)
            .ToObservableSequence().ToList().Should().Within(30.Seconds()).Emit().ToArray();

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
    public void ContentProvider_InsertText_UsesSlashFormat()
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

        // Universal format check over the settled snapshot (the final, complete item set).
        var items = contentProvider
            .GetItems("@", null)
            .TakeLast(1).Should().Within(30.Seconds()).Emit().ToArray();

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
    public void UnifiedReference_Keywords_UseSlashNotColon()
    {
        var providers = Mesh.ServiceProvider.GetServices<IAutocompleteProvider>();
        var unifiedProvider = providers
            .FirstOrDefault(p => p.GetType().Name.Contains("UnifiedReference"));
        unifiedProvider.Should().NotBeNull();

        // Type @/ACME/ProductLaunch/ to get keyword suggestions (needs 2+ completed segments).
        // Reactive: wait for the first snapshot that actually carries the keyword category.
        var items = unifiedProvider!
            .GetItems("@/ACME/ProductLaunch/", null)
            .Should().Within(30.Seconds())
            .Match(snap => snap.Any(i => i.Category == "Keywords"))
            .ToArray();

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
    public void AutocompleteAsync_CrossPartition_ReturnsFromMultiplePartitions()
    {
        // Global autocomplete (empty basePath) should return nodes from multiple partitions
        var suggestions = MeshQuery
            .AutocompleteAsync("", "", 30)
            .ToObservableSequence().ToList().Should().Within(30.Seconds()).Emit().ToArray();

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
    public void ChatAutocomplete_GlobalFanOut_ReachesOtherPartitions()
    {
        // When typing "@ACM" from Systemorph context, broadening should find ACME.
        // Reactive: wait for the FIRST batch whose items contain ACME — we bring results
        // when they come, never block on the slowest cross-partition fan-out completing.
        var acmeBatch = Orchestrator
            .GetCompletions("@ACM", "Systemorph")
            .Should().Within(30.Seconds())
            .Match(batch => batch.Items.Any(i =>
                i.InsertText.Contains("ACME", StringComparison.OrdinalIgnoreCase)),
                "broadening should find ACME from Systemorph context");

        Output.WriteLine($"First ACME-bearing batch '{acmeBatch.Category}': {acmeBatch.Items.Count} items");
        foreach (var item in acmeBatch.Items.Where(i =>
            i.InsertText.Contains("ACME", StringComparison.OrdinalIgnoreCase)).Take(3))
            Output.WriteLine($"    [{item.Priority}] {item.Label} => {item.InsertText}");
    }

    #endregion

    #region Timing Analysis

    /// <summary>
    /// Stopwatch-based latency analysis for the orchestrator. For each scenario,
    /// records: time from subscribe → first batch (per category), time from subscribe →
    /// OnCompleted. Prints a tab-separated table to test output so the user can compare
    /// "partition itself appearing" vs "items inside partition appearing" vs static-vs-Postgres.
    /// Not a pass/fail check — just timing diagnostics.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void TimingAnalysis_OrchestratorScenarios_RecordsLatencies()
    {
        var scenarios = new (string Label, string Query, string? Context)[]
        {
            ("@/ → all partitions",            "@/",                null),
            ("@/Sys → filtered partitions",    "@/Sys",             null),
            ("@/ACME/ → drill down (items)",   "@/ACME/",           null),
            ("@/Doc/ → drill down (static)",   "@/Doc/",            null),
            ("@Mark → in-partition broaden",   "@Mark",             "Systemorph"),
            ("@ACM → cross-partition broaden", "@ACM",              "Systemorph"),
            ("@/ACME/Project → deep path",     "@/ACME/Project",    null),
        };

        Output.WriteLine($"{"Scenario",-38} {"FirstBatch",10} {"BatchTimes (ms by category)",-50} {"Completed",10}");
        Output.WriteLine(new string('-', 120));

        foreach (var (label, query, context) in scenarios)
        {
            var (firstBatchMs, perCategoryMs, completedMs) =
                MeasureCompletionTimings(query, context);

            var perCategory = string.Join(", ",
                perCategoryMs.Select(kv => $"{kv.Key}={kv.Value:F0}"));

            Output.WriteLine(
                $"{label,-38} {firstBatchMs,9:F0}ms {perCategory,-50} {completedMs,9:F0}ms");
        }
    }

    private (double FirstBatchMs, IDictionary<string, double> PerCategoryMs, double CompletedMs)
        MeasureCompletionTimings(string query, string? context)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var firstBatchMs = -1.0;
        var completedMs = -1.0;
        var perCategory = new Dictionary<string, double>();

        // Pure synchronous Subscribe + gate — measures the live observable's batch
        // timings without bridging through a Task ("nothing async ever"). The gate
        // releases on OnCompleted/OnError; a 15s cap matches the previous WaitAsync.
        using var gate = new System.Threading.ManualResetEventSlim(false);
        using (Orchestrator.GetCompletions(query, context).Subscribe(
            batch =>
            {
                var t = sw.Elapsed.TotalMilliseconds;
                if (firstBatchMs < 0) firstBatchMs = t;
                if (!perCategory.ContainsKey(batch.Category))
                    perCategory[batch.Category] = t;
            },
            _ => { completedMs = sw.Elapsed.TotalMilliseconds; gate.Set(); },
            () => { completedMs = sw.Elapsed.TotalMilliseconds; gate.Set(); }))
        {
            gate.Wait(TimeSpan.FromSeconds(15));
        }

        return (firstBatchMs, perCategory, completedMs);
    }

    #endregion
}

[CollectionDefinition("AutocompleteIntegrationTest", DisableParallelization = true)]
public class AutocompleteIntegrationTestDefinition { }
