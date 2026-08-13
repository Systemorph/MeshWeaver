#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins <see cref="IStorageAdapter.WriteIfVersion"/> — the atomic compare-and-set that makes a
/// claim exclusive across processes and Orleans clusters (#1424), and the write-side twin of
/// <see cref="IStorageAdapter.DeleteIfExists"/>.
///
/// <para>Every case resolves the adapter from DI, so these pin that the primitive SURVIVES the
/// production decorator chain (SubtreeDeletionGuard → MonotonicWriteGuard → VersionWriting → leaf).
/// That matters more than it looks: the interface default is a non-atomic read-compare-write, so a
/// decorator that forgets to forward silently downgrades every caller to the behaviour the
/// primitive exists to replace — and nothing else would notice.</para>
/// </summary>
public class CompareAndSetWriteTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static IStorageAdapter BuildStore()
    {
        var services = new ServiceCollection();
        services.AddInMemoryPersistence(new InMemoryStorageAdapter());
        return services.BuildServiceProvider().GetRequiredService<IStorageAdapter>();
    }

    private static MeshNode Node(long version, string? name = null) =>
        new("claim", "Test") { NodeType = "Build", Name = name, Version = version };

    /// <summary>
    /// The exclusivity property itself: N writers that all read "no row" and all try to create it,
    /// and exactly ONE is told it won. This is the whole fix in one assertion — the ordinary write
    /// path would have accepted all of them (its condition applies at equal versions) and told none
    /// of them they lost.
    /// </summary>
    [Fact]
    public async Task ConcurrentCreates_ExactlyOneWins()
    {
        var store = BuildStore();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => store
                .WriteIfVersion(Node(version: 1, name: $"claimant-{i}"), expectedVersion: 0, JsonOptions)
                .FirstAsync()
                .ToTask())));

        outcomes.Count(o => o is true).Should().Be(1,
            "an insert-only compare-and-set is how a claim becomes exclusive; two winners is #1424");
        outcomes.Count(o => o is false).Should().Be(7, "every loser must be TOLD it lost");

        var stored = await store.Read("Test/claim", JsonOptions).FirstAsync().ToTask();
        stored!.Name.Should().StartWith("claimant-");
    }

    /// <summary>
    /// The same property one step on: N writers that all read the row at v and all try to succeed
    /// it. This is the takeover case — several clusters timing out on the same dead holder — and it
    /// must not produce two successors.
    /// </summary>
    [Fact]
    public async Task ConcurrentSuccessions_ExactlyOneWins()
    {
        var store = BuildStore();
        (await store.WriteIfVersion(Node(1, "holder"), 0, JsonOptions).FirstAsync().ToTask())
            .Should().Be(true);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => store
                .WriteIfVersion(Node(version: 2, name: $"successor-{i}"), expectedVersion: 1, JsonOptions)
                .FirstAsync()
                .ToTask())));

        outcomes.Count(o => o is true).Should().Be(1);
        var stored = await store.Read("Test/claim", JsonOptions).FirstAsync().ToTask();
        stored!.Name.Should().StartWith("successor-");
        stored.Version.Should().Be(2);
    }

    /// <summary>A version that does not match is refused, and the stored row is left alone.</summary>
    [Fact]
    public async Task WrongExpectedVersion_IsRefused_AndLeavesTheRow()
    {
        var store = BuildStore();
        await store.WriteIfVersion(Node(1, "holder"), 0, JsonOptions).FirstAsync().ToTask();

        (await store.WriteIfVersion(Node(9, "impostor"), expectedVersion: 7, JsonOptions)
            .FirstAsync().ToTask())
            .Should().Be(false);

        var stored = await store.Read("Test/claim", JsonOptions).FirstAsync().ToTask();
        stored!.Name.Should().Be("holder");
        stored.Version.Should().Be(1);
    }

    /// <summary>
    /// 🚨 A compare-and-set expecting a version must NOT resurrect a deleted row. "The row still
    /// carries exactly v" is false when there is no row at all, and an upsert here would report
    /// success — which for the build claim means a heartbeat racing a release re-creates the lock
    /// its holder just dropped, blocking the next candidate for the whole staleness budget. Postgres
    /// therefore issues a plain UPDATE for this case rather than INSERT … ON CONFLICT; this pins the
    /// semantic the two backends must agree on.
    /// </summary>
    [Fact]
    public async Task ExpectingAVersion_DoesNotResurrectADeletedRow()
    {
        var store = BuildStore();
        await store.WriteIfVersion(Node(1, "holder"), 0, JsonOptions).FirstAsync().ToTask();
        (await store.DeleteIfExists("Test/claim").FirstAsync().ToTask()).Should().Be(true);

        (await store.WriteIfVersion(Node(2, "zombie"), expectedVersion: 1, JsonOptions)
            .FirstAsync().ToTask())
            .Should().Be(false, "an absent row does not carry the expected version");

        (await store.Read("Test/claim", JsonOptions).FirstAsync().ToTask())
            .Should().BeNull("the refused write must not have re-created the row");
    }
}
