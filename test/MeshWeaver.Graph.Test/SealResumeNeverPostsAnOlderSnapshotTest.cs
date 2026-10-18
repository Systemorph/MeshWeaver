using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #6856, review thread 4307186139: a save that a delete seal refused is re-dispatched when the seal
/// lifts — and it must never be ordered after a NEWER own value. The persistence sampler's posting
/// decision (<see cref="MeshDataSourceExtensions.OwnSaveTicks"/>) is driven here with CONTROLLED
/// switches — the own stream, the nudge and the sample ticks are subjects the test fires in order —
/// so the dangerous interleaving is reproduced deterministically:
/// <list type="number">
/// <item><description>V is posted, then refused while the path is sealed (pending again);</description></item>
/// <item><description>the seal lifts, and the resume is HELD between noticing it and reaching the sampler;</description></item>
/// <item><description>the owner writes V+1, and the sampler posts it;</description></item>
/// <item><description>the held resume is let through.</description></item>
/// </list>
/// The resume is a payload-free nudge, so the next tick reads the latest state itself: nothing is
/// posted after V+1. NEGATIVE CONTROL: the previous composition, where the resume carried the snapshot
/// it captured (<c>TakeUnsaved()</c> before the merge, on e257b48be), posts V after V+1.
/// </summary>
public class SealResumeNeverPostsAnOlderSnapshotTest
{
    private static MeshNode Node(long version, string name) =>
        new("Doc", "Resume") { Name = name, NodeType = "Markdown", Version = version };

    private sealed class Harness
    {
        public Subject<MeshNode> Own { get; } = new();
        public Subject<Unit> Nudges { get; } = new();
        public Subject<long> Ticks { get; } = new();
        public MeshDataSourceExtensions.PendingOwnSave Pending { get; } = new();
        public List<MeshNode> Posted { get; } = new();

        /// <summary>What the sampler does with each decision: post it, and mark it requested.</summary>
        public void Post(MeshNode node)
        {
            Posted.Add(node);
            Pending.MarkRequested(node);
        }

        public void Tick() => Ticks.OnNext(0L);
    }

    [Fact(Timeout = 5000)]
    public void AResumeHeldAcrossANewerOwnWrite_NeverPostsTheOlderState()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var h = new Harness();
        using var _ = MeshDataSourceExtensions.OwnSaveTicks(h.Own, h.Nudges, h.Pending, h.Ticks, TimeSpan.FromMilliseconds(200))
            .Subscribe(h.Post);

        var v = Node(2, "acknowledged before the seal");
        h.Own.OnNext(v);
        h.Tick();
        h.Pending.MarkWriteFailed(v).Should().BeTrue("the sealed row refused the save; V is pending again");

        // The seal lifts; the resume is held (the nudge has not reached the sampler yet).
        var newer = Node(3, "written after the abort");
        h.Own.OnNext(newer);
        h.Tick();

        h.Nudges.OnNext(Unit.Default); // the held resume goes through
        h.Tick();

        h.Posted.Select(n => n.Version).Should().Equal(new long[] { 2, 3 },
            "the resume reads the latest state at the tick: nothing older follows V+1");
    }

    /// <summary>
    /// NEGATIVE CONTROL — the composition the review found on e257b48be, replayed with the same
    /// switches: the resume captured <c>TakeUnsaved()</c> (V) when the seal lifted and carried it into the
    /// sampler's merge. Delivered after V+1, it is the last arrival, and the next tick posts it.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void NegativeControl_ASnapshotCarryingResume_PostsTheOlderStateAfterTheNewer()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var h = new Harness();
        var heldSnapshot = new Subject<MeshNode>();
        using var _ = h.Own
            .Do(h.Pending.Track)
            .Merge(heldSnapshot) // the old resumes: a snapshot, merged ahead of the Sample
            .Sample(h.Ticks)
            .Subscribe(h.Post);

        var v = Node(2, "acknowledged before the seal");
        h.Own.OnNext(v);
        h.Tick();
        h.Pending.MarkWriteFailed(v).Should().BeTrue();

        var captured = h.Pending.TakeUnsaved(); // captured as the seal lifts — then held
        captured.Should().BeSameAs(v);
        var newer = Node(3, "written after the abort");
        h.Own.OnNext(newer);
        h.Tick();
        heldSnapshot.OnNext(captured!); // the held resume arrives last
        h.Tick();

        h.Posted.Select(n => n.Version).Should().Equal(new long[] { 2, 3, 2 },
            "the old composition posts the older snapshot after the newer save — the defect");
    }
}
