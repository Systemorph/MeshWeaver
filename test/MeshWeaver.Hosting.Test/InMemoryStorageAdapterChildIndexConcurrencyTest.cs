using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the CHILDREN INDEX of <see cref="InMemoryStorageAdapter"/> against a REBUILD IN FLIGHT
/// (MeshWeaver.Plugins#1827 — the query half of MeshWeaver#4280; the index itself is #4169).
///
/// <para><b>What was measured.</b> MeshWeaver.Plugins' red bake (run 34792068713, job
/// 103825681160): the synced source query of <c>Essentials/OperationRequest</c>, opened 0.5 s
/// into the Essentials install, answered 10 of the 18 <c>Store/Core/Source/*</c> nodes — written
/// three minutes earlier and READ BACK by Store's idempotence re-install ("0 written, 208
/// unchanged") — and kept answering the same 39 of 49 declared nodes for 20 s after the install
/// ended. No read was swallowed (zero <c>[MatchScope.Read]</c> lines), so the WALK was handed a
/// short child list. No <c>Added</c> event can ever come for a node written before the
/// subscription, so that snapshot was permanent: the live source fingerprint was folded over it,
/// the prebuilt bundle was DECLINED against it, the fallback compile read the same frozen
/// snapshot and failed <c>CS0246</c> on the eight missing files, and no publication sealed for
/// two days.</para>
///
/// <para><b>The mechanism.</b> <c>ChildrenOf</c> rebuilt the index IN PLACE — <c>Clear()</c>,
/// then re-index every key — whenever <c>IndexedCount != _nodes.Count</c>, and a writer's
/// <c>Added()</c> reset <c>IndexedCount</c> to <c>_nodes.Count</c> WITHOUT the rebuild lock. A
/// writer landing mid-rebuild therefore made the consistency flag read true while the index was
/// half-cleared; the next reader passed the check, skipped the lock, and read the partial index.</para>
///
/// <para><b>The pin.</b> A rebuild is parked mid-flight through the adapter's test seam (a
/// volatile int under a bounded <c>SpinWait.SpinUntil</c>, released in a <c>finally</c>); an
/// unrelated write lands while it is parked; a second reader then asks about a directory whose
/// nodes were all written before the rebuild began. It must get every one of them, the write must
/// not have waited, and the reader must not have waited: the park is released by the TEST, and a
/// rebuild released by its own budget instead is the proof that someone waited for it. Before the
/// fix the reader got ZERO: the index had been cleared and the writer had just declared it
/// consistent.</para>
/// </summary>
public class InMemoryStorageAdapterChildIndexConcurrencyTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Node(string path)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(path[(slash + 1)..], slash < 0 ? "" : path[..slash])
        {
            Name = path, NodeType = "Code", State = MeshNodeState.Active,
        };
    }

    // Write and ListChildPaths on this adapter are Observable.Defer over synchronous dictionary
    // work: Subscribe runs the side effect to completion on the calling thread. No bridge, no
    // await — the threads below are plain threads over a plain data structure.
    private static void WriteNow(InMemoryStorageAdapter adapter, string path)
        => adapter.Write(Node(path), Options).Subscribe();

    private static int CountNow(InMemoryStorageAdapter adapter, string parent)
    {
        var count = -1;
        adapter.ListChildPaths(parent).Subscribe(level => count = level.NodePaths.Count());
        return count;
    }

    [Fact]
    public void A_reader_during_a_rebuild_gets_the_whole_directory_and_nobody_waits_for_it()
    {
        const int settled = 18;   // Store/Core/Source at c3b0224c: 18 files, 10 answered

        var nodes = new ConcurrentDictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase);
        var adapter = new InMemoryStorageAdapter(nodes, new(StringComparer.OrdinalIgnoreCase));
        for (var k = 0; k < settled; k++)
            WriteNow(adapter, $"Store/Core/Source/F{k}");
        Assert.Equal(settled, CountNow(adapter, "Store/Core/Source"));

        // The one sanctioned rebuild trigger: the dictionary mutated behind the adapter's back.
        nodes["Bulk/Seeded"] = Node("Bulk/Seeded");

        var parked = 0;
        var release = 0;
        var releasedByBudget = 0;
        adapter.OnRebuildBuilt = () =>
        {
            Volatile.Write(ref parked, 1);
            var releasedByTest = SpinWait.SpinUntil(
                () => Volatile.Read(ref release) == 1, TestTimeouts.Convergence);
            if (!releasedByTest)
                Volatile.Write(ref releasedByBudget, 1);
        };

        var rebuilder = new Thread(() => CountNow(adapter, "Bulk")) { IsBackground = true, Name = "rebuilder" };
        var writeDone = 0;
        var writer = new Thread(() =>
        {
            WriteNow(adapter, "Essentials/OperationRequest/Source/OperationDsl");
            Volatile.Write(ref writeDone, 1);
        }) { IsBackground = true, Name = "unrelated-install" };

        int seenDuringRebuild;
        try
        {
            rebuilder.Start();
            Assert.True(
                SpinWait.SpinUntil(() => Volatile.Read(ref parked) == 1, TestTimeouts.Convergence),
                "the seeded dictionary did not trigger a rebuild — the seam never fired");

            // An install lands while the rebuild is in flight. It must complete: a writer never
            // waits for a rebuild (the gate installs 1,100 nodes while its subtree queries run).
            writer.Start();
            Assert.True(
                SpinWait.SpinUntil(() => Volatile.Read(ref writeDone) == 1, TestTimeouts.Convergence),
                "the write did not complete while a rebuild was in flight — a writer waited for it");

            // A second reader asks about a directory whose nodes were ALL written before the
            // rebuild began. Before the fix: 0 — the index was cleared and the writer had just
            // declared it consistent.
            seenDuringRebuild = CountNow(adapter, "Store/Core/Source");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
        rebuilder.Join();
        writer.Join();

        Assert.True(releasedByBudget == 0,
            "the parked rebuild was released by its budget, not by the test — a reader or a writer "
            + "waited for a rebuild in flight (readers must read the live index; writers must index "
            + "into the pending one)");
        Assert.True(seenDuringRebuild == settled,
            $"ListChildPaths(\"Store/Core/Source\") answered {seenDuringRebuild} of {settled} node(s) "
            + "while a rebuild was in flight — a partial listing returned as complete, which the "
            + "synced source query caches and never repairs (MeshWeaver.Plugins#1827).");
        // And once the rebuild lands, everything is there: the settled directory, the node that
        // landed between the rebuild's iteration and its swap (it indexed into the pending index
        // itself), and the node seeded behind the adapter's back.
        Assert.Equal(settled, CountNow(adapter, "Store/Core/Source"));
        Assert.Equal(1, CountNow(adapter, "Essentials/OperationRequest/Source"));
        Assert.Equal(1, CountNow(adapter, "Bulk"));
    }

    private static (string[] Nodes, string[] Dirs) ListNow(InMemoryStorageAdapter adapter, string? parent)
    {
        string[] nodes = [], dirs = [];
        adapter.ListChildPaths(parent).Subscribe(level =>
        {
            nodes = level.NodePaths.OrderBy(x => x).ToArray();
            dirs = level.DirectoryPaths.OrderBy(x => x).ToArray();
        });
        return (nodes, dirs);
    }

    /// <summary>
    /// A delete that lands AFTER the rebuild's key snapshot and BEFORE the loop visits that key
    /// (Copilot on #4295): <c>Removed</c> runs <c>Unindex</c> against a pending index that does not
    /// hold the key yet — a no-op — and an unconditional <c>Index</c> in the loop then put the
    /// deleted key back. A deleted leaf is filtered by every listing; a deleted node whose
    /// descendants were deleted too came back as a PHANTOM implied directory that every walk
    /// descends into, and nothing repairs it because the tally is exact and nothing rebuilds.
    /// The loop now indexes only a key that is still a node, checked in the same Mutate section.
    /// </summary>
    [Fact]
    public void A_key_deleted_after_the_snapshot_and_before_its_visit_is_not_resurrected()
    {
        var nodes = new ConcurrentDictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase);
        var adapter = new InMemoryStorageAdapter(nodes, new(StringComparer.OrdinalIgnoreCase));
        WriteNow(adapter, "Store/Core/Source/F0");
        WriteNow(adapter, "Ghost/Dir");
        WriteNow(adapter, "Ghost/Dir/Leaf");
        var (beforeNodes, beforeDirs) = ListNow(adapter, "Ghost");
        Assert.Equal(new[] { "Ghost/Dir" }, beforeNodes);
        Assert.Empty(beforeDirs);

        nodes["Bulk/Seeded"] = Node("Bulk/Seeded");   // the sanctioned rebuild trigger

        var parked = 0;
        var release = 0;
        var releasedByBudget = 0;
        adapter.OnRebuildSnapshot = () =>
        {
            Volatile.Write(ref parked, 1);
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence))
                Volatile.Write(ref releasedByBudget, 1);
        };

        var rebuilder = new Thread(() => CountNow(adapter, "Bulk")) { IsBackground = true, Name = "rebuilder" };
        var deleteDone = 0;
        var deleter = new Thread(() =>
        {
            adapter.Delete("Ghost/Dir/Leaf").Subscribe();
            adapter.Delete("Ghost/Dir").Subscribe();
            Volatile.Write(ref deleteDone, 1);
        }) { IsBackground = true, Name = "deleter" };
        try
        {
            rebuilder.Start();
            Assert.True(
                SpinWait.SpinUntil(() => Volatile.Read(ref parked) == 1, TestTimeouts.Convergence),
                "the seeded dictionary did not trigger a rebuild — the seam never fired");
            deleter.Start();
            Assert.True(
                SpinWait.SpinUntil(() => Volatile.Read(ref deleteDone) == 1, TestTimeouts.Convergence),
                "the deletes did not complete while a rebuild was in flight — a writer waited for it");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
        rebuilder.Join();
        deleter.Join();

        Assert.True(releasedByBudget == 0, "the parked rebuild was released by its budget, not by the test");
        var (ghostNodes, ghostDirs) = ListNow(adapter, "Ghost");
        Assert.True(ghostNodes.Length == 0 && ghostDirs.Length == 0,
            $"after the rebuild, 'Ghost' lists nodes=[{string.Join(", ", ghostNodes)}] dirs=[{string.Join(", ", ghostDirs)}] — "
            + "a key deleted between the snapshot and its visit was indexed back into the swapped-in "
            + "index as a phantom directory");
        var (rootNodes, rootDirs) = ListNow(adapter, null);
        Assert.DoesNotContain("Ghost", rootDirs);
        Assert.DoesNotContain("Ghost", rootNodes);
        Assert.Equal(new[] { "Bulk", "Store" }, rootDirs);
        Assert.Equal(1, CountNow(adapter, "Store/Core/Source"));
    }

    /// <summary>
    /// The unwidened shape — concurrent writers and readers in a tight loop — as a control over
    /// the fixed adapter's contract that the count is EXACT: adapter-only traffic, however
    /// concurrent, must never make the index look drifted, so it must never rebuild at all. On the
    /// unfixed adapter (measured 2026-09-14, 4 writers × 2,000 writes, 4 readers, a 60,000-key
    /// store) every one of 12 readings rebuilt — two writers left <c>IndexedCount</c> stale
    /// (W1 read the count before W2's add and stored it after) and each read then cleared and
    /// re-indexed the whole store — yet none was short, because the reader reads
    /// <c>IndexedCount</c> BEFORE <c>_nodes.Count</c> (which takes every bucket lock), so under
    /// hammering writers every check mismatched and took the slow path. The partial fast-path
    /// answer needs writes that are SPARSE relative to reads — the seeded install's shape — which
    /// the parked-rebuild test above reproduces deterministically. This one reds the moment the
    /// tally regresses to a racy heuristic: a rebuild during adapter-only traffic is the defect's
    /// precondition coming back.
    /// </summary>
    [Fact]
    public void Adapter_only_traffic_never_rebuilds_and_never_lists_short()
    {
        const int settled = 18;
        const int bulk = 6_000;
        const int writers = 4;         // the install writes its nodes concurrently (Merge(batchSize))
        const int churnPerWriter = 1_000;
        const int readers = 4;

        var adapter = new InMemoryStorageAdapter();
        for (var k = 0; k < settled; k++)
            WriteNow(adapter, $"Store/Core/Source/F{k}");
        for (var i = 0; i < bulk; i++)
            WriteNow(adapter, $"Bulk/P{i % 200}/Source/N{i}");
        Assert.Equal(settled, CountNow(adapter, "Store/Core/Source"));

        var rebuilds = 0;
        adapter.OnRebuildBuilt = () => Interlocked.Increment(ref rebuilds);

        var partials = new List<int>();
        var readings = 0;
        var writersDone = 0;
        var writerThreads = Enumerable.Range(0, writers).Select(w => new Thread(() =>
        {
            for (var i = 0; i < churnPerWriter; i++)
                WriteNow(adapter, $"Essentials/W{w}/Source/N{i}");
            Interlocked.Increment(ref writersDone);
        }) { IsBackground = true, Name = $"install-writer-{w}" }).ToArray();

        var readerThreads = Enumerable.Range(0, readers).Select(r => new Thread(() =>
        {
            while (Volatile.Read(ref writersDone) < writers)
            {
                var n = CountNow(adapter, "Store/Core/Source");
                Interlocked.Increment(ref readings);
                if (n != settled)
                    lock (partials) partials.Add(n);
            }
        }) { IsBackground = true, Name = $"reader-{r}" }).ToArray();

        foreach (var t in readerThreads) t.Start();
        foreach (var t in writerThreads) t.Start();
        foreach (var t in writerThreads) t.Join();
        foreach (var t in readerThreads) t.Join();

        Assert.True(readings > 0, "the readers never ran against the writers — the control is vacuous");
        Assert.True(rebuilds == 0,
            $"the index rebuilt {rebuilds} time(s) under adapter-only traffic — the count is no "
            + "longer an exact tally, which is the precondition of the partial-listing defect "
            + "(MeshWeaver.Plugins#1827)");
        Assert.True(partials.Count == 0,
            $"ListChildPaths(\"Store/Core/Source\") answered fewer than {settled} node(s) in "
            + $"{partials.Count} of {readings} reading(s) while unrelated writers were active "
            + $"(observed: {string.Join(", ", partials.Distinct().OrderBy(x => x))}).");
        Assert.Equal(settled, CountNow(adapter, "Store/Core/Source"));
        for (var w = 0; w < writers; w++)
            Assert.Equal(churnPerWriter, CountNow(adapter, $"Essentials/W{w}/Source"));
    }
}
