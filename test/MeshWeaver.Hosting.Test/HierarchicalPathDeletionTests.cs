using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Unit tests for <see cref="HierarchicalPathDeletion.DeleteSubtree"/> — the
/// pure parent-grouped bottom-up traversal that backs
/// <c>HandleDeleteNodeRequest</c>. No hub / persistence / MeshNode is
/// involved; the test injects a fake <c>deleteOne</c> delegate that
/// records the call order and can be primed to fail for specific paths.
///
/// <para>What we're proving:</para>
/// <list type="number">
///   <item>Children are deleted before their parent.</item>
///   <item>Siblings (children of a common parent) run concurrently.</item>
///   <item>Unrelated branches of the tree progress independently — a branch
///         that's already at its leaf doesn't have to wait for a deeper
///         neighbouring branch before its node deletes.</item>
///   <item>A failure at any descendant propagates as <c>OnError</c> and the
///         enclosing parent is **never** deleted.</item>
///   <item>The list of successfully-deleted paths is reported in actual
///         completion order (deepest first within a branch).</item>
/// </list>
/// </summary>
public class HierarchicalPathDeletionTests
{
    /// <summary>Records every <c>deleteOne</c> call in order; optionally fails for primed paths.</summary>
    private sealed class FakeDeleter
    {
        private readonly List<string> _started = new();
        private readonly List<string> _completed = new();
        private readonly object _gate = new();
        private readonly HashSet<string> _failPaths;
        private readonly Dictionary<string, Subject<Unit>> _gates;
        private readonly bool _gated;

        public FakeDeleter(IEnumerable<string>? failPaths = null, IEnumerable<string>? gatedPaths = null)
        {
            _failPaths = new HashSet<string>(failPaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _gates = (gatedPaths ?? Array.Empty<string>())
                .ToDictionary(p => p, _ => new Subject<Unit>(), StringComparer.OrdinalIgnoreCase);
            _gated = _gates.Count > 0;
        }

        public IReadOnlyList<string> Started { get { lock (_gate) return _started.ToList(); } }
        public IReadOnlyList<string> Completed { get { lock (_gate) return _completed.ToList(); } }

        public void Release(string path)
        {
            if (_gates.TryGetValue(path, out var subject))
            {
                subject.OnNext(Unit.Default);
                subject.OnCompleted();
            }
        }

        public IObservable<string> Delete(string path) => Observable.Defer(() =>
        {
            lock (_gate) _started.Add(path);

            if (_failPaths.Contains(path))
                return Observable.Throw<string>(new InvalidOperationException($"primed failure for '{path}'"));

            var emission = _gated && _gates.TryGetValue(path, out var subject)
                ? subject.Select(_ => path)
                : Observable.Return(path);

            return emission.Do(_ =>
            {
                lock (_gate) _completed.Add(path);
            });
        });
    }

    [Fact]
    public async Task SingleNode_no_descendants_deletes_root()
    {
        var fake = new FakeDeleter();
        var deleted = await HierarchicalPathDeletion
            .DeleteSubtree("root", Array.Empty<string>(), fake.Delete)
            .Should().Emit();

        deleted.Should().ContainSingle().Which.Should().Be("root");
        fake.Completed.Should().ContainSingle().Which.Should().Be("root");
    }

    [Fact]
    public async Task LinearChain_deletes_deepest_first()
    {
        var fake = new FakeDeleter();
        var deleted = await HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/b", "a/b/c" }, fake.Delete)
            .Should().Emit();

        deleted.Should().Equal("a/b/c", "a/b", "a");
        fake.Completed.Should().Equal("a/b/c", "a/b", "a");
    }

    [Fact]
    public async Task Siblings_both_complete_before_parent()
    {
        var fake = new FakeDeleter();
        var deleted = await HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/b", "a/c" }, fake.Delete)
            .Should().Emit();

        deleted.Should().HaveCount(3);
        deleted.Last().Should().Be("a", "root must be last");
        deleted.Take(2).Should().BeEquivalentTo(new[] { "a/b", "a/c" }, JsonSerializerOptions.Default,
            because: "siblings complete before the parent, in either order");
    }

    [Fact]
    public async Task Siblings_run_in_parallel_neither_blocks_the_other()
    {
        // Gate both siblings — neither delete completes until we release it.
        // Both Start() calls must happen BEFORE we release either, proving
        // the per-sibling Observable.Merge fired them concurrently.
        var fake = new FakeDeleter(gatedPaths: new[] { "a/b", "a/c" });

        // Eagerly connect so the gated deletes subscribe NOW; replay buffers the
        // final emission so the later blocking assertion still observes it.
        var result = HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/b", "a/c" }, fake.Delete)
            .Replay();
        using var connection = result.Connect();

        // Wait briefly for both subscriptions to register Start().
        SpinWait.SpinUntil(() => fake.Started.Count == 2, TimeSpan.FromSeconds(2))
            .Should().BeTrue("both siblings should start in parallel before any complete");
        fake.Completed.Should().BeEmpty("nothing released yet");

        fake.Release("a/b");
        fake.Release("a/c");

        var deleted = await result.Should().Emit();
        deleted.Last().Should().Be("a");
    }

    [Fact]
    public async Task Unrelated_branches_progress_independently()
    {
        // Tree:  root → branchA → leafA
        //         root → branchB → leafB
        // Gate leafB. leafA should still complete + propagate up to branchA
        // even though leafB / branchB are blocked.
        var fake = new FakeDeleter(gatedPaths: new[] { "root/branchB/leafB" });

        // Eagerly connect so the gated deletes subscribe NOW; replay buffers the
        // final emission so the later blocking assertion still observes it.
        var result = HierarchicalPathDeletion
            .DeleteSubtree("root", new[]
            {
                "root/branchA", "root/branchA/leafA",
                "root/branchB", "root/branchB/leafB"
            }, fake.Delete).Replay();
        using var connection = result.Connect();

        SpinWait.SpinUntil(() => fake.Completed.Contains("root/branchA"),
            TimeSpan.FromSeconds(2))
            .Should().BeTrue("branchA must complete without waiting for branchB");
        fake.Completed.Should().NotContain("root", "root waits for both branches");

        fake.Release("root/branchB/leafB");

        var deleted = await result.Should().Emit();
        deleted.Should().HaveCount(5);
        deleted.Last().Should().Be("root");
    }

    [Fact]
    public async Task Failure_at_leaf_propagates_and_parent_is_not_deleted()
    {
        var fake = new FakeDeleter(failPaths: new[] { "a/b" });

        Func<Task> act = () => HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/b" }, fake.Delete)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(10))
            .Await(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*primed failure for 'a/b'*");

        fake.Completed.Should().BeEmpty("the failing leaf never completes");
        fake.Started.Should().Contain("a/b");
        fake.Started.Should().NotContain("a", "the root must never even start once a descendant has failed");
    }

    [Fact]
    public async Task Failure_in_one_branch_does_not_block_sibling_starting_but_root_is_skipped()
    {
        // branchA leaf fails; branchB succeeds. Root must not be deleted.
        var fake = new FakeDeleter(failPaths: new[] { "root/branchA/leafA" });

        Func<Task> act = () => HierarchicalPathDeletion
            .DeleteSubtree("root", new[]
            {
                "root/branchA", "root/branchA/leafA",
                "root/branchB", "root/branchB/leafB"
            }, fake.Delete)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(10))
            .Await(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*primed failure for 'root/branchA/leafA'*");

        // branchB's subtree may have completed (siblings run concurrently),
        // but root must NEVER be deleted because the merged children stream errored.
        fake.Completed.Should().NotContain("root");
        fake.Completed.Should().NotContain("root/branchA",
            "branchA's parent post never runs once leafA failed");
    }

    [Fact]
    public async Task Failure_exposes_partial_deletion_list_via_Data()
    {
        // Linear chain a → b → c with primed failure at 'a'. c and b succeed
        // before a fails, so the caller should be able to recover the
        // partial-deletion list from the thrown exception.
        var fake = new FakeDeleter(failPaths: new[] { "a" });

        try
        {
            await HierarchicalPathDeletion
                .DeleteSubtree("a", new[] { "a/b", "a/b/c" }, fake.Delete)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(10))
                .Await(TestContext.Current.CancellationToken);
            Assert.Fail("Expected an InvalidOperationException");
        }
        catch (InvalidOperationException ex)
        {
            var partial = ex.Data["DeletedPaths"] as IReadOnlyList<string>;
            partial.Should().NotBeNull();
            partial!.Should().Equal("a/b/c", "a/b");
        }
    }

    [Fact]
    public async Task RootPath_added_if_missing_from_descendants_set()
    {
        // descendants set doesn't contain the root — DeleteSubtree must add it.
        var fake = new FakeDeleter();
        var deleted = await HierarchicalPathDeletion
            .DeleteSubtree("only-root", Array.Empty<string>(), fake.Delete)
            .Should().Emit();

        deleted.Should().ContainSingle().Which.Should().Be("only-root");
    }

    [Fact]
    public async Task Each_path_invoked_at_most_once_even_if_present_twice()
    {
        // Defensive: even if the descendants list duplicates 'a/b', the
        // ImmutableHashSet dedup should ensure deleteOne fires once per path.
        var fake = new FakeDeleter();
        var deleted = await HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/b", "a/b" }, fake.Delete)
            .Should().Emit();

        deleted.Should().Equal("a/b", "a");
        fake.Started.Count(p => p == "a/b").Should().Be(1);
    }

    // ─── Virtual (node-less) intermediate levels — issue #839 ────────────────

    [Fact]
    public async Task Descendants_behind_nodeless_intermediate_levels_are_deleted()
    {
        // The set contains 'a/x/y/z' but NO node at 'a/x' or 'a/x/y' — the shape every
        // satellite dictionary produces ({path}/_Thread/{id}, {nodeType}/Release/{v}).
        // The old set-connected recursion never visited the branch: 'a/x/y/z' was
        // counted in the plan, silently skipped, and survived a "successful" delete.
        var fake = new FakeDeleter();
        var deleted = await HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/x/y/z", "a/b" }, fake.Delete)
            .Should().Emit();

        deleted.Should().HaveCount(3);
        deleted.Should().Contain("a/x/y/z");
        deleted.Should().Contain("a/b");
        deleted.Last().Should().Be("a", "root must be deleted last");
        // Virtual levels carry no node — deleteOne must never fire for them.
        fake.Started.Should().NotContain("a/x");
        fake.Started.Should().NotContain("a/x/y");
    }

    [Fact]
    public async Task Nodeless_level_descendant_deleted_before_its_ancestor_node()
    {
        // Node at 'a/nt' AND a release satellite at 'a/nt/Release/1' with no node at
        // 'a/nt/Release': bottom-up order must still hold across the virtual level —
        // the satellite goes first, then 'a/nt', then the root.
        var fake = new FakeDeleter();
        var deleted = await HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/nt", "a/nt/Release/1" }, fake.Delete)
            .Should().Emit();

        deleted.Should().Equal("a/nt/Release/1", "a/nt", "a");
        fake.Started.Should().NotContain("a/nt/Release");
    }

    // ─── One admission lane for the whole tree — issue #6351 ────────────────

    /// <summary>Two branches of ten gated leaves each: a per-LEVEL cap could not hold this tree to
    /// three, because each branch's sibling merge would take three of its own.</summary>
    private static readonly ImmutableArray<string> TwoWideBranches = Enumerable.Range(0, 10)
        .SelectMany(i => new[] { $"root/a/{i}", $"root/b/{i}" })
        .Concat(new[] { "root/a", "root/b" })
        .ToImmutableArray();

    private static readonly ImmutableArray<string> GatedLeaves =
        TwoWideBranches.Where(p => p.Count(c => c == '/') == 2).ToImmutableArray();

    [Fact]
    public async Task Bounded_lane_holds_the_whole_tree_to_N_legs_in_flight()
    {
        var fake = new FakeDeleter(gatedPaths: GatedLeaves);
        var result = HierarchicalPathDeletion
            .DeleteSubtreeBounded("root", TwoWideBranches, fake.Delete, maxConcurrentDeletes: 3)
            .Replay();
        using var connection = result.Connect();

        SpinWait.SpinUntil(() => fake.Started.Count == 3, TimeSpan.FromSeconds(2))
            .Should().BeTrue("three legs are admitted at once");
        SpinWait.SpinUntil(() => fake.Started.Count > 3, TimeSpan.FromMilliseconds(300))
            .Should().BeFalse("no fourth leg starts while three hold the lane — across BOTH branches");

        // Release whatever is running, one at a time; the lane refills to three each time.
        var released = ImmutableHashSet<string>.Empty;
        while (released.Count < GatedLeaves.Length)
        {
            string? next = null;
            SpinWait.SpinUntil(() => (next = fake.Started.FirstOrDefault(
                    p => GatedLeaves.Contains(p) && !released.Contains(p))) is not null,
                TimeSpan.FromSeconds(2)).Should().BeTrue("the lane admits the next leaf after a release");
            released = released.Add(next!);
            fake.Release(next!);
            (fake.Started.Count(p => GatedLeaves.Contains(p)) - fake.Completed.Count(p => GatedLeaves.Contains(p)))
                .Should().BeLessThanOrEqualTo(3, "never more than three legs in flight");
        }

        var deleted = await result.Should().Emit();
        deleted.Should().HaveCount(TwoWideBranches.Length + 1);
        deleted.Last().Should().Be("root", "bottom-up order is unchanged by the lane");
    }

    [Fact]
    public void Unbounded_traversal_starts_every_leaf_at_once_negative_control()
    {
        // The shape before #6351: every leaf of both branches is subscribed simultaneously, which is
        // what queued a whole subtree's writes on one cap-1 pool.
        var fake = new FakeDeleter(gatedPaths: GatedLeaves);
        var result = HierarchicalPathDeletion
            .DeleteSubtree("root", TwoWideBranches, fake.Delete)
            .Replay();
        using var connection = result.Connect();

        SpinWait.SpinUntil(() => fake.Started.Count == GatedLeaves.Length, TimeSpan.FromSeconds(2))
            .Should().BeTrue("the unbounded overload subscribes all twenty leaves together");
    }

    [Fact]
    public void Bounded_lane_never_starts_a_queued_leg_once_the_traversal_is_disposed()
    {
        var fake = new FakeDeleter(gatedPaths: GatedLeaves);
        var connection = HierarchicalPathDeletion
            .DeleteSubtreeBounded("root", TwoWideBranches, fake.Delete, maxConcurrentDeletes: 1)
            .Replay()
            .Connect();
        SpinWait.SpinUntil(() => fake.Started.Count == 1, TimeSpan.FromSeconds(2)).Should().BeTrue();
        var running = fake.Started.Single();

        connection.Dispose();
        fake.Release(running);

        SpinWait.SpinUntil(() => fake.Started.Count > 1, TimeSpan.FromMilliseconds(300))
            .Should().BeFalse("a leg still queued when its traversal went away must never run its delete");
    }

    [Fact]
    public async Task Bounded_lane_still_fails_fast_and_never_deletes_the_parent()
    {
        var fake = new FakeDeleter(failPaths: new[] { "root/a/3" });

        Func<Task> act = () => HierarchicalPathDeletion
            .DeleteSubtreeBounded("root", TwoWideBranches, fake.Delete, maxConcurrentDeletes: 2)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(10))
            .Await(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*primed failure for 'root/a/3'*");
        fake.Started.Should().NotContain("root/a");
        fake.Started.Should().NotContain("root");
    }
}
