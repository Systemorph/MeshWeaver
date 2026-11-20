using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The two store-level halves of "a status type keeps no history" over the REAL
/// <see cref="FileSystemVersionStore"/>: the BOUNDED per-write discard
/// (<see cref="IVersionQuery.DiscardNewestVersions"/>, called by the version writer) and the drain the
/// retention sweep runs (<see cref="IVersionQuery.DrainHistory"/>, the rule
/// <see cref="VersionHistoryDrain"/>). The store's own <see cref="IVersionQuery.GetVersions"/> is the
/// instrument.
/// </summary>
public sealed class NoHistoryDrainOnTheFileSystemTest : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private const string StatusType = "Hosting/TriageStatus";
    private const string OrdinaryType = "Markdown";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "nohistory-drain-" + Guid.NewGuid().ToString("N"));
    private readonly IoPoolRegistry pools = new();
    private readonly FileSystemVersionStore versions;

    public NoHistoryDrainOnTheFileSystemTest() => versions = new FileSystemVersionStore(directory, pools);

    public void Dispose()
    {
        pools.Dispose();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static VersionHistoryDrain Drain(int maxRows) =>
        new(ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, StatusType), maxRows);

    private async Task Write(string path, string nodeType, int count)
    {
        for (var v = 1; v <= count; v++)
            await versions.WriteVersion(MeshNode.FromPath(path) with { NodeType = nodeType, Version = v, Name = $"v{v}" }, Options)
                .Timeout(Budget).Await();
    }

    private async Task<long[]> Remaining(string path)
        => (await versions.GetVersions(path).ToList().Timeout(Budget).Await()).Select(v => v.Version).Order().ToArray();

    [Fact]
    public void The_rule_keeps_each_drained_nodes_newest_version_and_touches_no_other_type()
    {
        var rows = new[]
        {
            new VersionHistoryRow("Hosting/Triage/Status", 1, DateTimeOffset.UnixEpoch),
            new VersionHistoryRow("Hosting/Triage/Status", 2, DateTimeOffset.UnixEpoch),
            new VersionHistoryRow("Hosting/Triage/Status", 3, DateTimeOffset.UnixEpoch) { NodeType = "hosting/triagestatus" },
            new VersionHistoryRow("Hosting/Doc", 1, DateTimeOffset.UnixEpoch),
            new VersionHistoryRow("Hosting/Doc", 2, DateTimeOffset.UnixEpoch) { NodeType = OrdinaryType },
            // Once a status type, now an ordinary one: its NEWEST row decides, so nothing goes.
            new VersionHistoryRow("Hosting/Retyped", 1, DateTimeOffset.UnixEpoch) { NodeType = StatusType },
            new VersionHistoryRow("Hosting/Retyped", 2, DateTimeOffset.UnixEpoch) { NodeType = OrdinaryType },
        };

        Drain(100).Select(rows).Select(r => (r.Path, r.Version)).Should().Equal(
            [("Hosting/Triage/Status", 1L), ("Hosting/Triage/Status", 2L)],
            "every version but the newest of a node whose newest version is a drained type (compared ignoring case)");
        Drain(1).Select(rows).Should().ContainSingle("the bound holds");
        Drain(0).Select(rows).Should().BeEmpty("a zero bound removes nothing");
        new VersionHistoryDrain(ImmutableHashSet<string>.Empty, 100).Select(rows).Should().BeEmpty("no type, no drain");
    }

    [Fact]
    public async Task The_store_drains_in_bounded_calls_down_to_the_newest_version()
    {
        await Write("Hosting/Triage/Status", StatusType, 10);

        (await versions.DrainHistory("Hosting", Drain(4)).Timeout(Budget).Await()).Should().Be(VersionHistoryPruneOutcome.Pruned(4));
        (await Remaining("Hosting/Triage/Status")).Should().Equal(5L, 6, 7, 8, 9, 10);
        (await versions.DrainHistory("Hosting", Drain(4)).Timeout(Budget).Await()).Removed.Should().Be(4);
        (await versions.DrainHistory("Hosting", Drain(4)).Timeout(Budget).Await()).Removed.Should().Be(1);
        (await Remaining("Hosting/Triage/Status")).Should().Equal(10L);
        (await versions.DrainHistory("Hosting", Drain(4)).Timeout(Budget).Await()).Removed.Should().Be(0,
            "the newest recorded version always stays");
    }

    [Fact]
    public async Task Negative_control_an_ordinary_type_and_another_partition_are_untouched()
    {
        await Write("Hosting/Doc", OrdinaryType, 6);
        await Write("Other/Triage/Status", StatusType, 6);

        (await versions.DrainHistory("Hosting", Drain(1000)).Timeout(Budget).Await()).Removed.Should().Be(0);
        (await Remaining("Hosting/Doc")).Should().HaveCount(6, "an ordinary type is never drained");
        (await Remaining("Other/Triage/Status")).Should().HaveCount(6, "a drain of Hosting never reaches another partition");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a b")]
    public async Task A_name_that_is_not_one_partition_is_refused_and_drains_nothing(string partition)
    {
        await Write("Hosting/Triage/Status", StatusType, 5);

        var refused = await versions.DrainHistory(partition, Drain(1000))
            .Select(_ => (Exception?)null)
            .Catch<Exception?, ArgumentException>(ex => Observable.Return<Exception?>(ex))
            .Timeout(Budget).Await();

        refused.Should().NotBeNull();
        (await Remaining("Hosting/Triage/Status")).Should().HaveCount(5);
    }

    /// <summary>
    /// The write-path half: a node that accumulated a backlog before its type opted out loses only
    /// its newest <see cref="VersionWritingStorageAdapter.DiscardPerWrite"/> versions per write — never
    /// the whole backlog inside one write (on Postgres that was one unbounded <c>DELETE</c> of every
    /// row of the path, 373 760 rows for the measured triage status node). The backlog is the sweep's.
    /// </summary>
    [Fact]
    public async Task A_write_of_a_no_history_type_discards_a_bounded_number_of_versions()
    {
        const string path = "Hosting/Triage/Status";
        const int backlog = 20;
        await Write(path, StatusType, backlog);
        var store = new InMemoryStorageAdapter();
        var definition = new MeshNode("TriageStatus", "Hosting") { NodeType = MeshNode.NodeTypePath, Content = new NodeTypeDefinition { KeepsHistory = false } };
        await store.Write(definition with { Version = 1 }, Options).Timeout(Budget).Await();
        var adapter = new VersionWritingStorageAdapter(store, versions, readOptions: () => Options);

        await adapter.Write(MeshNode.FromPath(path) with { NodeType = StatusType, Version = backlog + 1, Name = "tick" }, Options)
            .Timeout(Budget).Await();

        (await Remaining(path)).Should().Equal(
            Enumerable.Range(1, backlog - VersionWritingStorageAdapter.DiscardPerWrite).Select(v => (long)v),
            "the write records no snapshot and discards only the newest DiscardPerWrite versions");
    }

    /// <summary>
    /// The interface default, for a store that implemented only the UNBOUNDED purge (the shape the
    /// Postgres store had before it implemented the bounded discard): it purges a short history — a
    /// mail copy still leaves none — and DECLINES a long one, so no platform/store version skew can
    /// turn a write into a mass delete.
    /// </summary>
    [Fact]
    public async Task The_default_discard_purges_a_short_history_and_declines_a_long_one()
    {
        IVersionQuery purgeOnly = new PurgeOnly(versions);
        await Write("Hosting/Short", StatusType, 3);
        await Write("Hosting/Long", StatusType, 30);

        (await purgeOnly.DiscardNewestVersions("Hosting/Short", 8).Timeout(Budget).Await()).Should().BeTrue();
        (await Remaining("Hosting/Short")).Should().BeEmpty("3 rows are within the bound, so the purge runs");

        (await purgeOnly.DiscardNewestVersions("Hosting/Long", 8).Timeout(Budget).Await()).Should().BeFalse(
            "30 rows exceed the bound: the default says it did not discard");
        (await Remaining("Hosting/Long")).Should().HaveCount(30, "and deletes nothing — the backlog is the sweep's");
    }

    /// <summary>A real store seen through only the members an older store implemented.</summary>
    private sealed class PurgeOnly(IVersionQuery inner) : IVersionQuery
    {
        public IObservable<MeshNodeVersion> GetVersions(string path) => inner.GetVersions(path);
        public IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options) => inner.GetVersion(path, version, options);
        public IObservable<MeshNode?> GetVersionBefore(string path, long beforeVersion, JsonSerializerOptions options) => inner.GetVersionBefore(path, beforeVersion, options);
        public IObservable<bool> PurgeVersions(string path) => inner.PurgeVersions(path);
    }
}
