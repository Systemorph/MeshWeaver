#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the storage-layer write-integrity invariant: a node's durable
/// <see cref="MeshNode.Version"/> NEVER moves backward.
///
/// <para><b>The defect this closes.</b> Every path that mints a node version floors it at
/// <c>current + 1</c> (<see cref="MeshNode.NextVersion"/>, <c>Doc/Architecture/MeshNodeVersioning.md</c>),
/// so a write whose Version is BELOW the stored one is never a newer state — it is a stale
/// snapshot that some component adopted as live. Before the guard the store accepted it
/// silently; the production shape was <c>Version=12 / ApiKey=sk-v6</c> →
/// <c>Version=2 / ApiKey=sk-v0</c>, six acknowledged writes destroyed while the write
/// reported success.</para>
///
/// <para>Every case resolves <see cref="IStorageAdapter"/> from DI — the surface every
/// framework write path uses — so these also pin that the guard is actually WIRED into the
/// decorator chain, not merely implementable.</para>
/// </summary>
public class MonotonicWriteGuardTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>Builds the production decorator chain over a caller-owned in-memory leaf,
    /// so a test can also mutate the LEAF directly to simulate an out-of-band store change
    /// that this process never observed.</summary>
    private static (IStorageAdapter Guarded, InMemoryStorageAdapter Leaf) BuildStore()
    {
        var leaf = new InMemoryStorageAdapter();
        var services = new ServiceCollection();
        services.AddInMemoryPersistence(leaf);
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IStorageAdapter>(), leaf);
    }

    private static MeshNode Node(string name, long version) =>
        new("guard-target", "TestData")
        {
            Name = name,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Version = version
        };

    private const string Path = "TestData/guard-target";

    [Fact]
    public async Task BackwardWrite_IsRefused_AndTheStoredNodeSurvives()
    {
        var (store, _) = BuildStore();

        await store.Write(Node("v12", 12), JsonOptions).Should().Emit();

        // The rollback: a stale snapshot from before six acked writes.
        var refused = await store.Write(Node("v2-stale", 2), JsonOptions).Should().Emit();

        // The write does NOT report the stale node as durable — it reports what IS durable.
        refused!.Version.Should().Be(12);
        refused.Name.Should().Be("v12");

        var stored = await store.Read(Path, JsonOptions).Should().Emit();
        stored!.Version.Should().Be(12, "a backward write must never replace newer durable state");
        stored.Name.Should().Be("v12");
    }

    [Fact]
    public async Task ForwardWrite_IsAccepted()
    {
        var (store, _) = BuildStore();

        await store.Write(Node("v12", 12), JsonOptions).Should().Emit();
        await store.Write(Node("v13", 13), JsonOptions).Should().Emit();

        var stored = await store.Read(Path, JsonOptions).Should().Emit();
        stored!.Version.Should().Be(13);
        stored.Name.Should().Be("v13");
    }

    [Fact]
    public async Task EqualVersionWrite_IsAccepted()
    {
        // Only STRICT regressions are refused. Re-persisting at the same version is a
        // legitimate, common shape: a never-mutated node sits at its seed version forever,
        // and content can change without a version-minting write path having run.
        var (store, _) = BuildStore();

        await store.Write(Node("first", 7), JsonOptions).Should().Emit();
        await store.Write(Node("second", 7), JsonOptions).Should().Emit();

        var stored = await store.Read(Path, JsonOptions).Should().Emit();
        stored!.Name.Should().Be("second");
        stored.Version.Should().Be(7);
    }

    [Fact]
    public async Task FirstWriteOfANeverPersistedNode_IsAccepted()
    {
        // Brand-new nodes must not be gated by anything: there is no stored row to regress.
        var (store, _) = BuildStore();

        await store.Write(Node("brand-new", 1), JsonOptions).Should().Emit();

        var stored = await store.Read(Path, JsonOptions).Should().Emit();
        stored!.Version.Should().Be(1);
    }

    [Fact]
    public async Task RecreateAfterDelete_AtVersionOne_IsAccepted()
    {
        // Delete-then-recreate legitimately restarts the version at 1. The guard forgets
        // the path on delete, so the recreate faces no high-water mark AND no stored row.
        var (store, _) = BuildStore();

        await store.Write(Node("v50", 50), JsonOptions).Should().Emit();
        await store.Delete(Path).Should().Emit();
        await store.Write(Node("recreated", 1), JsonOptions).Should().Emit();

        var stored = await store.Read(Path, JsonOptions).Should().Emit();
        stored!.Version.Should().Be(1);
        stored.Name.Should().Be("recreated");
    }

    [Fact]
    public async Task RecreateAfterDeleteIfExists_AtVersionOne_IsAccepted()
    {
        var (store, _) = BuildStore();

        await store.Write(Node("v50", 50), JsonOptions).Should().Emit();
        (await store.DeleteIfExists(Path).Should().Emit()).Should().BeTrue();
        await store.Write(Node("recreated", 1), JsonOptions).Should().Emit();

        var stored = await store.Read(Path, JsonOptions).Should().Emit();
        stored!.Version.Should().Be(1);
    }

    [Fact]
    public async Task StaleHighWaterMark_DoesNotRefuse_WhenTheStoreMovedOutOfBand()
    {
        // The in-process high-water mark is a cheap FILTER, never the verdict. Another replica
        // deleting + recreating the node leaves this process holding a mark ABOVE the real durable
        // row; the guard must verify against the store and let the write through, not refuse on a
        // stale suspicion.
        var (store, leaf) = BuildStore();

        await store.Write(Node("v50", 50), JsonOptions).Should().Emit();

        // The out-of-band rewind, straight on the leaf — this process never sees it.
        //
        // 🚨 It is a DELETE + recreate, not a bare write of v1 over v50 (#971). The leaf itself is
        // now version-conditional, so an in-place rewind through IStorageAdapter is refused by the
        // STORE — that is the whole point of the store-level compare-and-set, and it is what makes a
        // fresh replica's first write safe. Delete-then-recreate is how a legitimate rewind has
        // always been expressed (see the guard's "Legitimate rewinds" note and the
        // RecreateAfterDelete cases above): the row is gone, so the recreate at version 1 faces
        // nothing to regress against. What this test pins is unchanged and orthogonal — that a mark
        // left stale by that sequence cannot by itself refuse the next legitimate write.
        await leaf.Delete(Path).Should().Emit();
        await leaf.Write(Node("recreated-elsewhere", 1), JsonOptions).Should().Emit();

        await store.Write(Node("v2-legitimate", 2), JsonOptions).Should().Emit();

        var stored = await store.Read(Path, JsonOptions).Should().Emit();
        stored!.Version.Should().Be(2, "the verification read saw version 1, so version 2 is a forward write");
        stored.Name.Should().Be("v2-legitimate");
    }

    [Fact]
    public async Task BackwardWrite_IsRefused_EvenWhenTheMarkCameOnlyFromAReadNotAWrite()
    {
        // The activation shape: a hub READS the durable node (v12), then a stale in-RAM
        // snapshot is flushed at v2. Nothing in this process ever WROTE v12, so the guard
        // has to learn the high-water mark from reads too.
        var (store, leaf) = BuildStore();

        await leaf.Write(Node("v12", 12), JsonOptions).Should().Emit();
        (await store.Read(Path, JsonOptions).Should().Emit())!.Version.Should().Be(12);

        await store.Write(Node("v2-stale", 2), JsonOptions).Should().Emit();

        var stored = await store.Read(Path, JsonOptions).Should().Emit();
        stored!.Version.Should().Be(12);
        stored.Name.Should().Be("v12");
    }

    [Fact]
    public async Task NodesAreGuardedIndependently()
    {
        var (store, _) = BuildStore();

        await store.Write(new MeshNode("a", "TestData") { NodeType = "Markdown", Version = 90 }, JsonOptions)
            .Should().Emit();
        // A low version on a DIFFERENT path is not a regression.
        await store.Write(new MeshNode("b", "TestData") { NodeType = "Markdown", Version = 1 }, JsonOptions)
            .Should().Emit();

        (await store.Read("TestData/b", JsonOptions).Should().Emit())!.Version.Should().Be(1);
        (await store.Read("TestData/a", JsonOptions).Should().Emit())!.Version.Should().Be(90);
    }

    /// <summary>
    /// 🚨 The durable trace must IDENTIFY the row that won, not merely count what was dropped
    /// (#2403). The losing write is often not a stale own-node snapshot at all but a SECOND,
    /// legitimate writer that reached the path first — and a cross-partition writer records where
    /// its row came from in <see cref="MeshNode.MainNode"/>. That is the one field that names it:
    /// the plugins gate's intermittent <c>Skill/presentation</c> failure was a pre-installed-skill
    /// MIRROR created from <c>Publish/Skill/presentation</c>, and identifying it took two sessions
    /// and three full local gate reproductions precisely because neither the warning nor this trace
    /// carried the provenance. <c>LastModifiedBy</c> does not close the gap — every
    /// system-impersonated writer stamps the same principal.
    /// </summary>
    [Fact]
    public async Task ConflictTrace_NamesTheDurableRowsProvenance()
    {
        var (store, leaf) = BuildStore();

        // The winner: a MIRROR of a node in ANOTHER partition — its MainNode is the source path.
        await store.Write(
                Node("mirror", 1) with { MainNode = "Publish/Skill/presentation" }, JsonOptions)
            .Should().Emit();

        // The loser: an installer's authored node, written at the version its repo file carries (0).
        await store.Write(Node("authored", 0), JsonOptions).Should().Emit();

        var trace = await leaf.Read($"{Path}/_Activity/write-conflict-1", JsonOptions).Should().Emit();
        var log = trace!.ContentAs<ActivityLog>(JsonOptions);
        log.Should().NotBeNull("the guard records a durable trace for every resolved conflict");
        log!.Messages.Select(m => m.Message).Should().Contain(
            m => m.Contains("mainNode='Publish/Skill/presentation'", StringComparison.Ordinal),
            "the trace has to name the writer, and provenance is what names a cross-partition mirror");
    }
}
