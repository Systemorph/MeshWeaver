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

    public static TheoryData<string> Stores => ["in-memory-chain", "file-system", "file-system-default"];

    private IStorageAdapter Build(string kind)
    {
        if (kind == "file-system")
        {
            Directory.CreateDirectory(_dir);
            return new CachingStorageAdapter(_dir, _ioPools);
        }
        if (kind == "file-system-default")
        {
            // The bare file-system adapter does not override the primitive: this is the interface
            // DEFAULT, over a Read that starts its I/O eagerly.
            Directory.CreateDirectory(_dir);
            return new FileSystemStorageAdapter(_dir, _ioPools);
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

    /// <summary>
    /// COLD: the comparison is taken when the delete is SUBSCRIBED, never when it is built. A delete
    /// built while v3 is stored, then v4 written, then the delete subscribed, must see v4 and refuse.
    /// A comparison read started eagerly (the file-system Read runs on an eager, replaying I/O pool)
    /// captured v3 at build time and removed v4.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheComparison_IsTakenAtSubscription_NotWhenTheDeleteIsBuilt(string kind)
    {
        var store = Build(kind);
        await Seed(store, 3);

        var delete = store.DeleteIfVersionAtMost(Path, 3, JsonOptions);
        // Give an eager comparison read every chance to complete against v3 before the write.
        (await Stored(store))!.Version.Should().Be(3);
        await Seed(store, 4);

        (await delete.FirstAsync().Await()).Should().BeFalse("v4 is stored when the delete runs");
        (await Stored(store))!.Version.Should().Be(4, "the newer row survives");
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

    /// <summary>
    /// A row that is ELIGIBLE throughout — every version it passes through is at or below the limit
    /// — must be removed even while a compare-and-set writer keeps replacing it. The in-memory store
    /// used to replace a row on the <see cref="IStorageAdapter.WriteIfVersion"/> path outside its
    /// mutation section, so a replacement landing between the delete's comparison and its pair
    /// removal made the removal miss, and the delete answered "refused" for a row it was entitled
    /// to remove (reported upstream as a Conflict naming the confirmed version itself).
    /// </summary>
    [Fact]
    public async Task AnEligibleRow_IsRemoved_WhileACompareAndSetWriterReplacesIt()
    {
        var spurious = 0;
        for (var trial = 0; trial < 300; trial++)
        {
            var store = new InMemoryStorageAdapter();
            store.Write(Node(1, "v1"), JsonOptions).Subscribe();
            var writing = 0;
            var stop = 0;
            var writer = Task.Run(() =>
            {
                for (long v = 1; System.Threading.Volatile.Read(ref stop) == 0; v++)
                {
                    bool? applied = null;
                    store.WriteIfVersion(Node(v + 1, $"v{v + 1}"), v, JsonOptions).Subscribe(a => applied = a);
                    System.Threading.Volatile.Write(ref writing, 1);
                    if (applied != true)
                        return; // the row is gone: the delete removed it
                }
            });
            var removed = false;
            try
            {
                System.Threading.SpinWait.SpinUntil(() => System.Threading.Volatile.Read(ref writing) == 1,
                    TimeSpan.FromSeconds(5)).Should().BeTrue("the writer started");
                ((IStorageAdapter)store).DeleteIfVersionAtMost(Path, long.MaxValue, JsonOptions)
                    .Subscribe(r => removed = r);
            }
            finally
            {
                // Stopped and joined whatever happened above, so a failed start assertion cannot
                // leave the writer replacing the row after the test has ended.
                System.Threading.Volatile.Write(ref stop, 1);
                await writer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            if (!removed)
                spurious++;
        }

        spurious.Should().Be(0,
            "every version the row passed through was within the limit, so no delete may be refused");
    }

    /// <summary>
    /// Two writable providers each hold a copy: v3 in one, v4 in the other. A guarded delete naming 3
    /// removes the v3 copy and must refuse the v4 one — and the COMPOSITE must answer "not removed",
    /// because the node still exists. An OR over the providers answered <c>true</c>, and the delete
    /// pipeline then announced the root deleted while the newer row survived.
    /// Control: when every copy is within the limit, the composite answers <c>true</c>.
    /// </summary>
    [Fact]
    public async Task ACompositeRemoval_DoesNotHideAnotherProvidersRefusal()
    {
        var older = new InMemoryStorageAdapter();
        var newer = new InMemoryStorageAdapter();
        await Seed(older, 3);
        await Seed(newer, 4);
        IStorageAdapter composite = new PersistenceService(
            [new WritableProvider("older", older), new WritableProvider("newer", newer)]);

        (await Guarded(composite, 3)).Should().BeFalse(
            "the v4 copy survives, so the node was not deleted whatever the other provider removed");
        (await Stored(newer))!.Version.Should().Be(4, "the newer copy was refused and kept");

        var a = new InMemoryStorageAdapter();
        var b = new InMemoryStorageAdapter();
        await Seed(a, 3);
        await Seed(b, 2);
        IStorageAdapter control = new PersistenceService(
            [new WritableProvider("a", a), new WritableProvider("b", b)]);
        (await Guarded(control, 3)).Should().BeTrue("every copy was within the limit and was removed");
        (await Stored(a)).Should().BeNull();
        (await Stored(b)).Should().BeNull();
    }

    private sealed class WritableProvider(string name, IStorageAdapter adapter) : IPartitionStorageProvider
    {
        public string Name => name;
        public bool IsReadOnly => false;
        public IStorageAdapter Adapter => adapter;
    }

    public void Dispose()
    {
        _ioPools.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
