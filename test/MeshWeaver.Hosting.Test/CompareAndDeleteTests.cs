#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins <see cref="IStorageAdapter.DeleteIfVersionAtMost"/> — the storage-level compare-and-delete a
/// version-guarded delete commits through (#6856), so its guarantee no longer rests on a seal held in
/// one process's memory. The condition is "stored version AT MOST the confirmed one": an older stored
/// row is the same node one sampler flush behind and goes; a NEWER one is a write the deleter never
/// saw and stays.
///
/// <para>Each case runs against the production decorator chain over the in-memory store (resolved
/// from DI — SubtreeDeletionGuard → MonotonicWriteGuard → VersionWriting → leaf), so it also pins
/// that every decorator FORWARDS the primitive; and against the file-system caching adapter, which
/// takes the single-writer path.</para>
/// </summary>
public sealed class CompareAndDeleteTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private const string Path = "Test/guarded";

    private readonly string _dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "mw-compare-and-delete-" + Guid.NewGuid().ToString("N"));
    private readonly MeshWeaver.Mesh.Threading.IoPoolRegistry _ioPools = new();

    public static TheoryData<string> Stores => ["in-memory-chain", "file-system"];

    private IStorageAdapter Build(string kind)
    {
        if (kind == "file-system")
        {
            Directory.CreateDirectory(_dir);
            return new CachingStorageAdapter(_dir, _ioPools);
        }
        var services = new ServiceCollection();
        services.AddInMemoryPersistence(new InMemoryStorageAdapter());
        return services.BuildServiceProvider().GetRequiredService<IStorageAdapter>();
    }

    private static MeshNode Node(long version, string name) =>
        new("guarded", "Test") { NodeType = "Markdown", Name = name, Version = version };

    private static async Task Seed(IStorageAdapter store, long version)
        => await store.Write(Node(version, $"v{version}"), JsonOptions).FirstAsync().Await();

    private static async Task<bool> Guarded(IStorageAdapter store, long maxVersion)
        => await store.DeleteIfVersionAtMost(Path, maxVersion, JsonOptions).FirstAsync().Await();

    private static async Task<MeshNode?> Stored(IStorageAdapter store)
        => await store.Read(Path, JsonOptions).DefaultIfEmpty(null).FirstAsync().Await();

    /// <summary>CONTROL: the row the deleter confirmed is removed.</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheConfirmedVersion_IsRemoved(string kind)
    {
        var store = Build(kind);
        await Seed(store, 3);

        (await Guarded(store, 3)).Should().BeTrue("the stored row is the version the deleter confirmed");
        (await Stored(store)).Should().BeNull();
    }

    /// <summary>
    /// "At most", not "equal": the owner confirmed V while its write of V is still on its way to the
    /// store, which holds V-1. That row is the node the deleter read, one flush behind — removing it
    /// is right, and refusing would answer a conflict that never happened.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AnOlderStoredRow_IsRemoved(string kind)
    {
        var store = Build(kind);
        await Seed(store, 2);

        (await Guarded(store, 3)).Should().BeTrue("the store is one flush behind the confirmed version");
        (await Stored(store)).Should().BeNull();
    }

    /// <summary>
    /// THE FALSIFIER: a row NEWER than the confirmed version is a write that landed after the check.
    /// It is refused and left in place, and no Deleted notification is published for it. An
    /// unconditional delete — what the commit was before #6856 — removes it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ANewerStoredRow_IsRefused_AndSurvives(string kind)
    {
        var store = Build(kind);
        await Seed(store, 4);
        var deletions = new ConcurrentBag<string>();
        using var feed = store.Changes
            .Where(c => c.Kind == DataChangeKind.Deleted)
            .Subscribe(c => deletions.Add(c.Path));

        (await Guarded(store, 3)).Should().BeFalse("version 4 was written after the deleter confirmed 3");

        var stored = await Stored(store);
        stored.Should().NotBeNull("a refused compare-and-delete removes nothing");
        stored!.Version.Should().Be(4);
        stored.Name.Should().Be("v4");
        deletions.Should().BeEmpty("nothing was deleted, so nothing may be announced as deleted");
    }

    /// <summary>No row: nothing removed, and the call says so rather than claiming the delete.</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AnAbsentRow_RemovesNothing(string kind)
    {
        var store = Build(kind);

        (await Guarded(store, 3)).Should().BeFalse("there was no row to remove");
    }

    /// <summary>
    /// Atomicity on the store of record: N guarded deletes of the same row, and exactly ONE is told it
    /// removed it. The file-system adapter is single-writer by contract and is not held to this.
    /// </summary>
    [Fact]
    public async Task ConcurrentGuardedDeletes_ExactlyOneRemoves()
    {
        var store = Build("in-memory-chain");
        await Seed(store, 3);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => store.DeleteIfVersionAtMost(Path, 3, JsonOptions).FirstAsync().Await())));

        outcomes.Count(o => o).Should().Be(1, "one row, one removal");
        (await Stored(store)).Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
