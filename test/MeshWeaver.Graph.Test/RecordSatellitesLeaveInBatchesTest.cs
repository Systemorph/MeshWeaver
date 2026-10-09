using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A recursive delete removes RECORD satellites (<c>_Activity</c> by default,
/// <see cref="MeshOperationOptions.RecordSatelliteSegments"/>) in batches through
/// <see cref="IStorageAdapter.DeleteMany"/>, not one per-node hub round-trip each.
///
/// <para>Production shape (memex.systemorph.com, 2026-10-09): deleting the retired NodeType
/// <c>Crm/Client</c> took more than 60 s. It held several hundred
/// <c>_Activity/compile-*</c> records, and every one of them was activated twice, once for its
/// pre-flight <c>ValidateDeleteRequest</c> and once for its own <c>DeleteNodeRequest</c>.</para>
///
/// <para>Here: a root with <see cref="Records"/> activity records and one satellite whose owner is
/// NOT in the plan. That satellite must keep the per-node lane, because nothing in this delete
/// validated its owner. The negative control (<see cref="RecordSatellitesPerNodeWhenUndeclaredTest"/>)
/// clears the declaration, and every record then goes one at a time, as before.</para>
/// </summary>
public abstract class RecordSatelliteDeleteTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The activity records under the deleted root.</summary>
    protected const int Records = 120;

    /// <summary>Rows per <c>DeleteMany</c> in the batched lane.</summary>
    protected const int BatchSize = 50;

    /// <summary>The store of record, with counters for both delete lanes.</summary>
    private protected readonly LatentDeleteStorageAdapter Storage = new(new InMemoryStorageAdapter());

    /// <summary>The record segments this run declares.</summary>
    protected abstract ImmutableHashSet<string> RecordSegments { get; }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStorageAdapter>(Storage);
            services.AddSingleton(new MeshOperationOptions
            {
                RecordSatelliteSegments = RecordSegments,
                RecordSatelliteBatchSize = BatchSize,
            });
            return services;
        }));

    /// <summary>The path of the satellite whose owner (<c>{root}/Ghost</c>) has no node.</summary>
    protected static string OrphanOwnedSatellite(string rootPath) => $"{rootPath}/Ghost/_Activity/orphan";

    /// <summary>Seeds the root, its activity records and the orphan-owned satellite.</summary>
    protected async Task<string> SeedAsync(string rootId)
    {
        var rootPath = $"{TestPartition}/{rootId}";
        await NodeFactory.CreateNode(
                new MeshNode(rootId, TestPartition) { Name = rootId, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit();
        var options = new JsonSerializerOptions();
        // Straight into the store of record, as WideDeleteOnASerialisedWriteLaneTestBase does: the plan
        // is enumerated from storage, and the rows need no create round-trip each.
        await Enumerable.Range(0, Records)
            .Select(i => Storage.Inner.Write(Activity($"compile-{i:D3}", $"{rootPath}/_Activity", rootPath), options))
            .Append(Storage.Inner.Write(Activity("orphan", $"{rootPath}/Ghost/_Activity", $"{rootPath}/Ghost"), options))
            .Merge()
            .ToList()
            .Should().Within(TestTimeouts.Convergence).Emit();
        return rootPath;
    }

    private static MeshNode Activity(string id, string ns, string owner) => new(id, ns)
    {
        Name = id,
        NodeType = "Activity",
        MainNode = owner,
        State = MeshNodeState.Active,
        Content = new ActivityLog(ActivityCategory.Compilation) { Id = id, Status = ActivityStatus.Succeeded },
    };

    /// <summary>Deletes the root recursively and asserts the store of record is empty under it.</summary>
    protected async Task DeleteAndAssertEmptyAsync(string rootPath)
    {
        (await NodeFactory.DeleteNode(rootPath).Should().Within(90.Seconds()).Emit("the recursive delete completes"))
            .Should().BeTrue();
        (await Storage.Inner.ListDescendantPaths(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeEmpty("every record and the orphan-owned satellite must be gone");
        (await Storage.Inner.Exists(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeFalse("the root must be gone");
    }

    /// <summary>Single-row deletes the store saw for paths under <paramref name="prefix"/>.</summary>
    protected int SingleDeletesUnder(string prefix)
        => Storage.SingleDeletes.Count(p => p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Batched deletes whose paths are all under <paramref name="prefix"/>.</summary>
    protected IReadOnlyList<IReadOnlyCollection<string>> BatchesUnder(string prefix)
        => Storage.BatchDeletes
            .Where(b => b.Count > 0 && b.All(p => p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
            .ToList();
}

/// <summary>The fix: the records leave in batches, the orphan-owned satellite takes the per-node lane.</summary>
public class RecordSatellitesLeaveInBatchesTest(ITestOutputHelper output) : RecordSatelliteDeleteTestBase(output)
{
    /// <inheritdoc />
    protected override ImmutableHashSet<string> RecordSegments => new MeshOperationOptions().RecordSatelliteSegments;

    [Fact]
    public async Task ARecursiveDelete_RemovesActivityRecords_InBatches_NotOneLeafEach()
    {
        var rootPath = await SeedAsync("records-batched");

        await DeleteAndAssertEmptyAsync(rootPath);

        var activities = $"{rootPath}/_Activity";
        var batches = BatchesUnder(activities);
        batches.Sum(b => b.Count).Should().Be(Records, "every record leaves through DeleteMany");
        batches.Should().HaveCount((Records + BatchSize - 1) / BatchSize, "at most BatchSize rows per call");
        SingleDeletesUnder(activities).Should().Be(0, "no record pays a one-row delete of its own");

        // In-test negative control: the owner of this satellite has no node and is therefore not in
        // the plan, so nothing validated it, and the row keeps the per-node lane.
        Storage.SingleDeletes.Should().Contain(OrphanOwnedSatellite(rootPath),
            "a satellite whose owner this delete did not validate is deleted through its own hub");
    }
}

/// <summary>
/// Negative control: with no record segments declared, every activity record is deleted as its own
/// leaf, which is the shape the production delete had.
/// </summary>
public class RecordSatellitesPerNodeWhenUndeclaredTest(ITestOutputHelper output) : RecordSatelliteDeleteTestBase(output)
{
    /// <inheritdoc />
    protected override ImmutableHashSet<string> RecordSegments => ImmutableHashSet<string>.Empty;

    [Fact]
    public async Task WithoutTheDeclaration_EveryRecordIsDeletedAsItsOwnLeaf()
    {
        var rootPath = await SeedAsync("records-per-node");

        await DeleteAndAssertEmptyAsync(rootPath);

        var activities = $"{rootPath}/_Activity";
        BatchesUnder(activities).Should().BeEmpty("nothing is declared a record, so nothing is batched");
        SingleDeletesUnder(activities).Should().BeGreaterThanOrEqualTo(Records,
            "every record pays its own leaf delete");
    }
}
