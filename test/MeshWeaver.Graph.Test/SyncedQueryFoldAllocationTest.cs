using System;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins that a synced query's path set is FOLDED alongside its snapshot, never re-derived from it on
/// every frame (MeshWeaver#5799).
///
/// <para><b>What went wrong.</b> <c>SyncedQueryMeshNodes</c> published its path set as
/// <c>dict.Keys.ToImmutableHashSet()</c> after every change frame. The process-wide query cache backs
/// GUI queries with these collections, so ONE node's update on an N-result query rebuilt an N-entry
/// set — on the order of N·log N <c>SortedInt32KeyNode&lt;ImmutableHashSet&lt;string&gt;.HashBucket&gt;</c>
/// tree nodes per change, the type that led a 2.57 GiB heap step on a production portal.</para>
///
/// <para>The bound is per FRAME, independent of N; the negative control measures the old derivation
/// with the same instrument on the same snapshot and shows it fails the same bound — so the
/// instrument can fail.</para>
/// </summary>
public class SyncedQueryFoldAllocationTest(ITestOutputHelper output)
{
    private const int ResultCount = 2_000;
    private const int Frames = 50;

    // An Updated frame on a 2,000-entry snapshot replaces one dictionary path (~11 tree nodes) and
    // must not touch the set. Generous against JIT noise, and two orders of magnitude below the old
    // per-frame rebuild.
    private const long BytesPerFrameBound = 8 * 1024;

    private static MeshNode Node(int i) => new($"n{i}", "Synced/Fold") { Name = $"Node {i}" };

    private static SyncedQueryFold.State Seeded()
    {
        var initial = new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Initial,
            Items = Enumerable.Range(0, ResultCount).Select(Node).ToArray(),
        };
        return SyncedQueryFold.Apply(SyncedQueryFold.Seed(seenInitial: false), initial);
    }

    private static QueryResultChange<MeshNode>[] UpdateFrames() =>
        Enumerable.Range(0, Frames)
            .Select(i => new QueryResultChange<MeshNode>
            {
                ChangeType = QueryChangeType.Updated,
                Items = [Node(i * 7 % ResultCount) with { Name = $"updated {i}" }],
            })
            .ToArray();

    [Fact]
    public void AnUpdateFrame_DoesNotRebuildThePathSet()
    {
        var seeded = Seeded();
        var frames = UpdateFrames();

        var perFrame = Settled(() =>
        {
            var state = seeded;
            foreach (var frame in frames)
                state = SyncedQueryFold.Apply(state, frame);
            return state.Paths.Count;
        }) / Frames;

        output.WriteLine($"Folded: {perFrame:N0} bytes per Updated frame on a {ResultCount:N0}-entry snapshot.");
        perFrame.Should().BeLessThan(BytesPerFrameBound,
            "an update of a path already in the set must cost the set nothing — only the "
            + "dictionary's one replaced path is allocated");
    }

    [Fact]
    public void NegativeControl_TheOldPerFrameDerivation_FailsTheSameBound()
    {
        var seeded = Seeded();
        var frames = UpdateFrames();

        // The shape this fix removed: fold the dictionary, then derive the set from its keys.
        var perFrame = Settled(() =>
        {
            var dict = seeded.Dict;
            var count = 0;
            foreach (var frame in frames)
            {
                dict = dict.SetItem(frame.Items[0].Path, frame.Items[0]);
                count += dict.Keys.ToImmutableHashSet().Count;
            }
            return count;
        }) / Frames;

        output.WriteLine($"Derived: {perFrame:N0} bytes per Updated frame on a {ResultCount:N0}-entry snapshot.");
        perFrame.Should().BeGreaterThan(BytesPerFrameBound * 10,
            "the instrument must be able to see the per-frame rebuild it exists to catch");
    }

    [Fact]
    public void ThePathSet_IsExactlyTheSnapshotKeys_AcrossEveryChangeType()
    {
        var state = SyncedQueryFold.Seed(seenInitial: false);
        state = SyncedQueryFold.Apply(state, new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Added,
            Items = [Node(1), Node(2)],
        });
        state.SeenInitial.Should().BeFalse("only Initial/Reset opens the gate; pre-Initial changes still fold");

        state = SyncedQueryFold.Apply(state, new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Initial,
            Items = [Node(3), Node(4), new MeshNode("") { Name = "path-less is ignored" }],
        });
        state.SeenInitial.Should().BeTrue();

        state = SyncedQueryFold.Apply(state, new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Removed,
            Items = [Node(2), Node(99)],
        });
        state = SyncedQueryFold.Apply(state, new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Updated,
            Items = [Node(5)],
        });

        state.Paths.SetEquals(state.Dict.Keys).Should().BeTrue("the set is exactly the snapshot's keys");
        state.Paths.SetEquals(new[] { Node(1).Path, Node(3).Path, Node(4).Path, Node(5).Path })
            .Should().BeTrue($"expected n1, n3, n4, n5 but held [{string.Join(", ", state.Paths)}]");
    }

    /// <summary>
    /// Bytes the action allocates on this thread, as the MINIMUM over repeated samples: tier-0 code
    /// allocates what optimised code does not, so a transient shows up in some samples and a real
    /// allocation in all of them.
    /// </summary>
    private static long Settled(Func<int> action)
    {
        var best = long.MaxValue;
        for (var sample = 0; sample < 10; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(action());
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return best;
    }
}
