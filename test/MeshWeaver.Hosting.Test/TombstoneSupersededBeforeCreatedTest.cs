using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Deterministic pin for #3008: a delete tombstone must be SUPERSEDED at the recreate's durable
/// commit — strictly before the recreate's <c>Created</c> reaches the <see cref="IMeshChangeFeed"/>.
///
/// <para>The forced interleaving is the seam itself. <c>StorageAdapterChangeFeedExtensions</c>
/// composes the feed publish downstream of the outermost adapter's write emission, so a feed
/// subscriber that reads the tombstone AT PUBLISH TIME observes exactly the ordering a reader in
/// production would: before the fix the tombstone was still live here (the only clear ran later,
/// inside a live per-node hub's change handler — or never, when no hub was alive for the path), and
/// a delivery abandoned by the still-disposing old hub was NACKed with the authoritative, non-retryable
/// <c>"the node was deleted, so this address will not reactivate"</c> for a node that provably
/// existed again. After the fix the write seam supersedes the tombstone on the post-commit emission,
/// upstream of the publish. No hub, no timing — the same assertion fails before and passes after.</para>
///
/// <para>The end-to-end shape (delete → recreate → a fresh subscriber must see the new node) is
/// <c>MeshWeaver.Graph.Test.RecreateSupersedesTombstoneTest</c>; the routing-level NACK
/// classification itself is pinned by <c>DeletedAddressNackClassificationTest</c>.</para>
/// </summary>
public class TombstoneSupersededBeforeCreatedTest
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private const string Partition = "TestData";
    private const string Id = "cache-recreate";
    private const string Path = Partition + "/" + Id;

    // MeshNode(id, @namespace) — Path is DERIVED as "{namespace}/{id}". Version is set explicitly so
    // the recreate-version bookkeeping below asserts on a value the test controls.
    private static MeshNode Node(string name, long version = 1) =>
        new(Id, Partition) { Name = name, NodeType = "Markdown", State = MeshNodeState.Active, Version = version };

    private static (RecentlyDeletedRegistry Registry, IStorageAdapter Adapter, InProcessMeshChangeFeed Feed) Rig()
    {
        var registry = new RecentlyDeletedRegistry();
        // The real outermost decorator over the real in-memory adapter — the exact object graph
        // PersistenceExtensions builds, minus the version-history layers this seam does not touch.
        IStorageAdapter adapter = new SubtreeDeletionGuardStorageAdapter(new InMemoryStorageAdapter(), registry);
        return (registry, adapter, new InProcessMeshChangeFeed());
    }

    [Fact(Timeout = 5000)]
    public void Supersede_LiftsGoneForGood_ButKeepsTheDeleteOnRecord()
    {
        var registry = new RecentlyDeletedRegistry();
        var tombstones = (IAddressTombstones)registry;

        registry.MarkDeleted(Path);
        tombstones.IsDeleted(Path).Should().BeTrue("a just-deleted address is gone for good until something is written there again");
        registry.IsRecreatedAt(Path, 1).Should().BeFalse("nothing has been written after the delete yet");

        registry.Supersede(Path, version: 1);

        tombstones.IsDeleted(Path).Should().BeFalse("a durable write landed after the delete — the address is not gone for good");
        registry.IsRecentlyDeleted(Path).Should().BeFalse("a save to a recreated path is not a resurrection");
        registry.IsRecreatedAt(Path, 1).Should().BeTrue("an emission at the recreate's own version IS the recreate");
        registry.IsRecreatedAt(Path, 2).Should().BeTrue("an emission above the recreate's version is a later update of it");
        registry.IsRecreatedAt(Path, 0).Should().BeFalse("an emission below the recreate's version predates it — a stale replay");

        // A later, higher write does not move the recreate marker: the FIRST write after the delete is the recreate.
        registry.Supersede(Path, version: 2);
        registry.IsRecreatedAt(Path, 1).Should().BeTrue("the recreate is still the version-1 write");

        // A fresh delete re-arms the tombstone from scratch.
        registry.MarkDeleted(Path);
        tombstones.IsDeleted(Path).Should().BeTrue("a new delete is again gone for good");
        registry.IsRecreatedAt(Path, 1).Should().BeFalse("the new delete has no recreate yet");

        // Untracked paths are a no-op — the overwhelming majority of writes.
        registry.Supersede("TestData/never-deleted", 1);
        registry.IsRecreatedAt("TestData/never-deleted", 1).Should().BeFalse();
        tombstones.IsDeleted("TestData/never-deleted").Should().BeFalse();
    }

    [Fact(Timeout = 5000)]
    public async Task Write_SupersedesTheTombstone_BeforeCreatedIsPublished()
    {
        var (registry, adapter, feed) = Rig();
        var tombstones = (IAddressTombstones)registry;
        var tombstonedAtPublish = new ReplaySubject<bool>();
        using var feedSub = feed.Subscribe(ev =>
        {
            if (ev.Path == Path && ev.Kind == MeshChangeKind.Created)
                tombstonedAtPublish.OnNext(tombstones.IsDeleted(Path));
        });

        // The delete marks synchronously (HandleDeleteNodeRequest) — modelled here as the mark itself.
        registry.MarkDeleted(Path);
        tombstones.IsDeleted(Path).Should().BeTrue();

        var saved = await adapter.WriteAndPublishCreated(Node("Second"), JsonOptions, feed).FirstAsync();
        saved.Should().NotBeNull();

        var observed = await tombstonedAtPublish.Should().Emit();
        observed.Should().BeFalse(
            "by the time Created reaches the change feed the tombstone must already be superseded — "
            + "a subscriber acting on Created would otherwise still be told the address will not reactivate");
        tombstones.IsDeleted(Path).Should().BeFalse();
        registry.IsRecreatedAt(Path, saved!.Version).Should().BeTrue(
            "the delete stays on record with the recreate's version so a version rewind is recognised as the recreate");
    }

    [Fact(Timeout = 5000)]
    public async Task WriteMany_SupersedesEveryWrittenPath_BeforeCreatedIsPublished()
    {
        var (registry, adapter, feed) = Rig();
        var tombstones = (IAddressTombstones)registry;
        var tombstonedAtPublish = new ReplaySubject<bool>();
        using var feedSub = feed.Subscribe(ev =>
        {
            if (ev.Path == Path && ev.Kind == MeshChangeKind.Created)
                tombstonedAtPublish.OnNext(tombstones.IsDeleted(Path));
        });
        registry.MarkDeleted(Path);

        var written = await adapter.WriteManyAndPublishCreated(new[] { Node("Second") }, JsonOptions, feed).FirstAsync();
        written.Count.Should().Be(1);

        var observed = await tombstonedAtPublish.Should().Emit();
        observed.Should().BeFalse("the bulk write is a recreate like any other — superseded before its Created is published");
        tombstones.IsDeleted(Path).Should().BeFalse();
    }

    [Fact(Timeout = 5000)]
    public async Task WriteIfVersion_SupersedesOnlyWhenTheCompareAndSetCommitted()
    {
        var (registry, adapter, _) = Rig();
        var tombstones = (IAddressTombstones)registry;

        var first = await adapter.Write(Node("First", version: 1), JsonOptions).FirstAsync();
        first.Should().NotBeNull();
        registry.MarkDeleted(Path);

        // A mismatching expected version writes nothing — the tombstone must stay live.
        var refused = await adapter.WriteIfVersion(Node("Second", version: 2), expectedVersion: 99, JsonOptions).FirstAsync();
        refused.Should().Be(false);
        tombstones.IsDeleted(Path).Should().BeTrue("a compare-and-set that did not commit supersedes nothing");

        var committed = await adapter.WriteIfVersion(Node("Second", version: 2), expectedVersion: 1, JsonOptions).FirstAsync();
        committed.Should().Be(true);
        tombstones.IsDeleted(Path).Should().BeFalse("the compare-and-set committed — the path exists again");
        registry.IsRecreatedAt(Path, 2).Should().BeTrue();
    }
}
