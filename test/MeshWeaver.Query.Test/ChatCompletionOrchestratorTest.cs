using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Tests for IChatCompletionOrchestrator — the new layered autocomplete system.
/// Verifies partition fan-out, current-node priority, drill-down, and async streaming.
/// </summary>
[Collection("ChatCompletionOrchestratorTest")]
public class ChatCompletionOrchestratorTest : MonolithMeshTestBase
{
    private static readonly string _cacheDirectory =
        Path.Combine(Path.GetTempPath(), "MeshWeaverChatCompleteTests", Guid.NewGuid().ToString());
    static ChatCompletionOrchestratorTest() => Directory.CreateDirectory(_cacheDirectory);

    protected override bool ShareMeshAcrossTests => true;

    public ChatCompletionOrchestratorTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(TestPaths.SamplesGraphData)
            .AddRowLevelSecurity()
            .AddSystemorph()
            .AddAcme()
            .AddMeshNodes(TestUsers.PublicAdminAccess())
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .AddGraph()
            .ConfigureHub(hub => hub.AddMeshNavigation());
    }

    // Cache dir is class-static + shared SP — never deleted between tests.

    private IChatCompletionOrchestrator GetOrchestrator()
        => Mesh.ServiceProvider.GetRequiredService<IChatCompletionOrchestrator>();

    [Fact(Timeout = 30000)]
    public void AtSlash_ReturnsPartitionList()
    {
        var orchestrator = GetOrchestrator();

        var batches = orchestrator.GetCompletions("@/", null).ToList().Should().Within(10.Seconds()).Emit();

        batches.Should().NotBeEmpty("@/ should return partition suggestions");
        var allItems = batches.SelectMany(b => b.Items).ToList();
        allItems.Should().NotBeEmpty("should have at least one partition");

        // All InsertText should be absolute paths with trailing slash (for drill-down)
        foreach (var item in allItems)
        {
            item.InsertText.Should().StartWith("@/", "partition items should be absolute");
        }
    }

    [Fact(Timeout = 30000)]
    public void AtSlashPartition_DrillsIntoPartition()
    {
        var orchestrator = GetOrchestrator();

        // ACME is a known partition from sample data
        var batches = orchestrator.GetCompletions("@/ACME/", null).ToList().Should().Within(10.Seconds()).Emit();

        batches.Should().NotBeEmpty("@/ACME/ should return children of ACME partition");
        var allItems = batches.SelectMany(b => b.Items).ToList();
        allItems.Should().NotBeEmpty("ACME should have child nodes");

        // Results should be scoped to ACME partition
        var nodeItems = allItems.Where(i => i.Path != null && !i.Category.Equals("Keywords")).ToList();
        foreach (var item in nodeItems)
        {
            item.Path.Should().StartWith("ACME", "drill-down results should be within ACME partition");
        }
    }

    [Fact(Timeout = 30000)]
    public void AtSlashWithFilter_FiltersPartitions()
    {
        var orchestrator = GetOrchestrator();

        // "Sys" should match "Systemorph" partition
        var batches = orchestrator.GetCompletions("@/Sys", null).ToList().Should().Within(10.Seconds()).Emit();

        var allItems = batches.SelectMany(b => b.Items).ToList();
        allItems.Should().Contain(i => i.Path != null &&
            i.Path.StartsWith("Systemorph", StringComparison.OrdinalIgnoreCase),
            "should find Systemorph when filtering with 'Sys'");
    }

    [Fact(Timeout = 30000)]
    public void AtText_StaysWithinPartition()
    {
        var orchestrator = GetOrchestrator();

        // Search for "ACME" with Systemorph as context — should NOT cross into ACME partition
        var batches = orchestrator.GetCompletions("@ACME", "Systemorph").ToList().Should().Within(10.Seconds()).Emit();

        batches.Should().NotBeEmpty("@ACME should return results");
        var allItems = batches.SelectMany(b => b.Items).ToList();
        allItems.Should().NotBeEmpty("should find ACME-related nodes");

        // No Global batch — non-/ queries must stay within the current partition
        batches.Should().NotContain(b => b.Category == "Global",
            "non-/ queries must stay within the current partition; use @/ for cross-partition search");

        foreach (var item in allItems)
            item.InsertText.Should().StartWith("@", "every reference InsertText should start with '@'");
    }

    [Fact(Timeout = 30000)]
    public void AtText_NearbyResultsHaveHigherPriority()
    {
        var orchestrator = GetOrchestrator();

        // Search with context set to "ACME" — items under ACME should rank higher than partition-level results
        var batches = orchestrator.GetCompletions("@Project", "ACME").ToList().Should().Within(10.Seconds()).Emit();

        var nearbyBatch = batches.FirstOrDefault(b => b.Category == "Nearby");
        var partitionBatch = batches.FirstOrDefault(b => b.Category == "Partition");

        if (nearbyBatch != null && partitionBatch != null)
        {
            nearbyBatch.CategoryPriority.Should().BeGreaterThan(partitionBatch.CategoryPriority,
                "Nearby (current node) should have higher priority than Partition-level results");
        }
    }

    [Fact(Timeout = 30000)]
    public void AcceptItem_HasAbsolutePath()
    {
        var orchestrator = GetOrchestrator();

        var batches = orchestrator.GetCompletions("@/ACME/", null).ToList().Should().Within(10.Seconds()).Emit();
        var allItems = batches.SelectMany(b => b.Items).ToList();

        foreach (var item in allItems)
        {
            if (!string.IsNullOrEmpty(item.InsertText))
            {
                item.InsertText.Should().StartWith("@/",
                    "accepted items must have absolute path for attachment");
            }
        }
    }

    [Fact(Timeout = 30000)]
    public void EmptyQuery_ReturnsNothing()
    {
        var orchestrator = GetOrchestrator();

        // Rx ToList() emits a single (empty) list on OnCompleted — the orchestrator
        // short-circuits a non-@ / empty query to an immediately-completing stream.
        var batches = orchestrator.GetCompletions("", null).ToList().Should().Within(10.Seconds()).Emit();
        batches.Should().BeEmpty("empty query should return no results");
    }

    [Fact(Timeout = 30000)]
    public void NonAtQuery_ReturnsNothing()
    {
        var orchestrator = GetOrchestrator();

        var batches = orchestrator.GetCompletions("hello", null).ToList().Should().Within(10.Seconds()).Emit();
        batches.Should().BeEmpty("non-@ query should return no results");
    }

    [Fact(Timeout = 30000)]
    public void StreamsNearbyBeforePartition()
    {
        var orchestrator = GetOrchestrator();

        // With a current namespace, Nearby (local hub) should arrive before Partition
        // (broadening). Rx ToList() preserves OnNext order, so the collected sequence
        // reflects the order batches streamed in.
        var batchCategories = orchestrator.GetCompletions("@Project", "ACME")
            .ToList().Should().Within(10.Seconds()).Emit()
            .Select(b => b.Category)
            .ToList();

        batchCategories.Should().NotContain("Global",
            "non-/ queries must stay within the current partition");

        var nearbyIndex = batchCategories.IndexOf("Nearby");
        var partitionIndex = batchCategories.IndexOf("Partition");
        if (nearbyIndex >= 0 && partitionIndex >= 0)
        {
            nearbyIndex.Should().BeLessThan(partitionIndex,
                "Nearby (current node) should stream before Partition (broadening)");
        }
    }
}
