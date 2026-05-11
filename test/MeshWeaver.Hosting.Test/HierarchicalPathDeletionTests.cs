using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using Xunit;

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

        public IObservable<Unit> Delete(string path) => Observable.Defer(() =>
        {
            lock (_gate) _started.Add(path);

            if (_failPaths.Contains(path))
                return Observable.Throw<Unit>(new InvalidOperationException($"primed failure for '{path}'"));

            var emission = _gated && _gates.TryGetValue(path, out var subject)
                ? subject.AsObservable()
                : Observable.Return(Unit.Default);

            return emission.Do(_ =>
            {
                lock (_gate) _completed.Add(path);
            });
        });
    }

    [Fact]
    public void SingleNode_no_descendants_deletes_root()
    {
        var fake = new FakeDeleter();
        var deleted = HierarchicalPathDeletion
            .DeleteSubtree("root", Array.Empty<string>(), fake.Delete)
            .Wait();

        deleted.Should().ContainSingle().Which.Should().Be("root");
        fake.Completed.Should().ContainSingle().Which.Should().Be("root");
    }

    [Fact]
    public void LinearChain_deletes_deepest_first()
    {
        var fake = new FakeDeleter();
        var deleted = HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/b", "a/b/c" }, fake.Delete)
            .Wait();

        deleted.Should().Equal("a/b/c", "a/b", "a");
        fake.Completed.Should().Equal("a/b/c", "a/b", "a");
    }

    [Fact]
    public void Siblings_both_complete_before_parent()
    {
        var fake = new FakeDeleter();
        var deleted = HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/b", "a/c" }, fake.Delete)
            .Wait();

        deleted.Should().HaveCount(3);
        deleted.Last().Should().Be("a", "root must be last");
        deleted.Take(2).Should().BeEquivalentTo(new[] { "a/b", "a/c" },
            "siblings complete before the parent, in either order");
    }

    [Fact]
    public void Siblings_run_in_parallel_neither_blocks_the_other()
    {
        // Gate both siblings — neither delete completes until we release it.
        // Both Start() calls must happen BEFORE we release either, proving
        // the per-sibling Observable.Merge fired them concurrently.
        var fake = new FakeDeleter(gatedPaths: new[] { "a/b", "a/c" });

        var resultTask = HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/b", "a/c" }, fake.Delete)
            .ToTask();

        // Wait briefly for both subscriptions to register Start().
        SpinWait.SpinUntil(() => fake.Started.Count == 2, TimeSpan.FromSeconds(2))
            .Should().BeTrue("both siblings should start in parallel before any complete");
        fake.Completed.Should().BeEmpty("nothing released yet");

        fake.Release("a/b");
        fake.Release("a/c");

        var deleted = resultTask.GetAwaiter().GetResult();
        deleted.Last().Should().Be("a");
    }

    [Fact]
    public void Unrelated_branches_progress_independently()
    {
        // Tree:  root → branchA → leafA
        //         root → branchB → leafB
        // Gate leafB. leafA should still complete + propagate up to branchA
        // even though leafB / branchB are blocked.
        var fake = new FakeDeleter(gatedPaths: new[] { "root/branchB/leafB" });

        var resultTask = HierarchicalPathDeletion
            .DeleteSubtree("root", new[]
            {
                "root/branchA", "root/branchA/leafA",
                "root/branchB", "root/branchB/leafB"
            }, fake.Delete).ToTask();

        SpinWait.SpinUntil(() => fake.Completed.Contains("root/branchA"),
            TimeSpan.FromSeconds(2))
            .Should().BeTrue("branchA must complete without waiting for branchB");
        fake.Completed.Should().NotContain("root", "root waits for both branches");

        fake.Release("root/branchB/leafB");

        var deleted = resultTask.GetAwaiter().GetResult();
        deleted.Should().HaveCount(5);
        deleted.Last().Should().Be("root");
    }

    [Fact]
    public void Failure_at_leaf_propagates_and_parent_is_not_deleted()
    {
        var fake = new FakeDeleter(failPaths: new[] { "a/b" });

        Action act = () => HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/b" }, fake.Delete)
            .Wait();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*primed failure for 'a/b'*");

        fake.Completed.Should().BeEmpty("the failing leaf never completes");
        fake.Started.Should().Contain("a/b");
        fake.Started.Should().NotContain("a", "the root must never even start once a descendant has failed");
    }

    [Fact]
    public void Failure_in_one_branch_does_not_block_sibling_starting_but_root_is_skipped()
    {
        // branchA leaf fails; branchB succeeds. Root must not be deleted.
        var fake = new FakeDeleter(failPaths: new[] { "root/branchA/leafA" });

        Action act = () => HierarchicalPathDeletion
            .DeleteSubtree("root", new[]
            {
                "root/branchA", "root/branchA/leafA",
                "root/branchB", "root/branchB/leafB"
            }, fake.Delete).Wait();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*primed failure for 'root/branchA/leafA'*");

        // branchB's subtree may have completed (siblings run concurrently),
        // but root must NEVER be deleted because the merged children stream errored.
        fake.Completed.Should().NotContain("root");
        fake.Completed.Should().NotContain("root/branchA",
            "branchA's parent post never runs once leafA failed");
    }

    [Fact]
    public void Failure_exposes_partial_deletion_list_via_Data()
    {
        // Linear chain a → b → c with primed failure at 'a'. c and b succeed
        // before a fails, so the caller should be able to recover the
        // partial-deletion list from the thrown exception.
        var fake = new FakeDeleter(failPaths: new[] { "a" });

        try
        {
            HierarchicalPathDeletion
                .DeleteSubtree("a", new[] { "a/b", "a/b/c" }, fake.Delete)
                .Wait();
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
    public void RootPath_added_if_missing_from_descendants_set()
    {
        // descendants set doesn't contain the root — DeleteSubtree must add it.
        var fake = new FakeDeleter();
        var deleted = HierarchicalPathDeletion
            .DeleteSubtree("only-root", Array.Empty<string>(), fake.Delete)
            .Wait();

        deleted.Should().ContainSingle().Which.Should().Be("only-root");
    }

    [Fact]
    public void Each_path_invoked_at_most_once_even_if_present_twice()
    {
        // Defensive: even if the descendants list duplicates 'a/b', the
        // ImmutableHashSet dedup should ensure deleteOne fires once per path.
        var fake = new FakeDeleter();
        var deleted = HierarchicalPathDeletion
            .DeleteSubtree("a", new[] { "a/b", "a/b" }, fake.Delete)
            .Wait();

        deleted.Should().Equal("a/b", "a");
        fake.Started.Count(p => p == "a/b").Should().Be(1);
    }
}
