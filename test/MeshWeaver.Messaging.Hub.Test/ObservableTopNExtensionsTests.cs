using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Reactive;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

public class ObservableTopNExtensionsTests
{
    private static readonly IComparer<int> AscInt = Comparer<int>.Default;
    private static readonly IComparer<int> DescInt =
        Comparer<int>.Create((a, b) => b.CompareTo(a));

    // ──────────────────────── Argument validation ────────────────────────

    [Fact]
    public void ScanTopN_NullSource_Throws()
    {
        IObservable<int> source = null!;
        Action act = () => source.ScanTopN(3, AscInt);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ScanTopN_NullComparer_Throws()
    {
        Action act = () => Observable.Empty<int>().ScanTopN(3, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ScanTopN_NonPositiveTopN_Throws(int topN)
    {
        Action act = () => Observable.Empty<int>().ScanTopN(topN, AscInt);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ──────────────────────── Subscription contract ────────────────────────

    [Fact]
    public void EmptySubject_EmitsInitialEmptySnapshot()
    {
        var subject = new Subject<int>();
        var snapshots = new List<IReadOnlyList<int>>();

        using var sub = subject.ScanTopN(3, AscInt).Subscribe(snapshots.Add);

        snapshots.Should().HaveCount(1);
        snapshots[0].Should().BeEmpty();
    }

    [Fact]
    public void EmptySource_OnlyInitialSnapshotThenCompletes()
    {
        var snapshots = new List<IReadOnlyList<int>>();
        var completed = false;

        Observable.Empty<int>().ScanTopN(3, AscInt)
            .Subscribe(snapshots.Add, () => completed = true);

        snapshots.Should().HaveCount(1).And.SatisfyRespectively(
            s => s.Should().BeEmpty());
        completed.Should().BeTrue();
    }

    [Fact]
    public void ErrorPropagates_ToOnError()
    {
        var subject = new Subject<int>();
        Exception? caught = null;

        subject.ScanTopN(3, AscInt).Subscribe(_ => { }, ex => caught = ex);

        var boom = new InvalidOperationException("boom");
        subject.OnError(boom);

        caught.Should().BeSameAs(boom);
    }

    // ──────────────────────── Ordering ────────────────────────

    [Fact]
    public void Ascending_FirstItemIsBest()
    {
        var snapshots = ScanInts(AscInt, topN: 3, items: [5, 1, 3]);

        // Initial empty + 3 inputs = 4 snapshots
        snapshots.Should().HaveCount(4);
        snapshots[3].Should().Equal(1, 3, 5);
    }

    [Fact]
    public void Descending_LargestItemIsBest()
    {
        var snapshots = ScanInts(DescInt, topN: 3, items: [5, 1, 3]);

        snapshots[3].Should().Equal(5, 3, 1);
    }

    [Fact]
    public void EachItemTriggersAFreshSnapshot()
    {
        var snapshots = ScanInts(AscInt, topN: 3, items: [3, 1, 2]);

        snapshots.Should().HaveCount(4); // initial empty + 3 items
        snapshots[0].Should().BeEmpty();
        snapshots[1].Should().Equal(3);
        snapshots[2].Should().Equal(1, 3);
        snapshots[3].Should().Equal(1, 2, 3);
    }

    // ──────────────────────── Top-N truncation ────────────────────────

    [Fact]
    public void TopN_DropsItemsWorseThanCurrentNth()
    {
        // topN=2, ascending. Items: 1,2,3 — 3 should be dropped silently.
        var snapshots = ScanInts(AscInt, topN: 2, items: [1, 2, 3]);

        snapshots.Should().HaveCount(3); // initial + first two emit, third drops
        snapshots[2].Should().Equal(1, 2);
    }

    [Fact]
    public void TopN_DisplacesNthWhenBetterArrives()
    {
        // topN=2, ascending. Sequence 5,3,1: each new item is better than the worst.
        var snapshots = ScanInts(AscInt, topN: 2, items: [5, 3, 1]);

        snapshots.Should().HaveCount(4);
        snapshots[1].Should().Equal(5);
        snapshots[2].Should().Equal(3, 5);
        snapshots[3].Should().Equal(1, 3); // 5 displaced
    }

    [Fact]
    public void TopN_EqualsOne_DegenerateCase()
    {
        var snapshots = ScanInts(AscInt, topN: 1, items: [3, 1, 2, 0, 5]);

        snapshots.Should().HaveCount(4); // initial + 3, 1, 0; 2 and 5 are dropped
        snapshots[1].Should().Equal(3);
        snapshots[2].Should().Equal(1);
        snapshots[3].Should().Equal(0);
    }

    [Fact]
    public void TopN_LargerThanInput_KeepsEverything()
    {
        var snapshots = ScanInts(AscInt, topN: 100, items: [3, 1, 4, 1, 5, 9, 2, 6]);

        snapshots[^1].Should().Equal(1, 1, 2, 3, 4, 5, 6, 9);
    }

    // ──────────────────────── Equal-score items (no dedup) ────────────────────────

    [Fact]
    public void EqualItems_AreNotDeduplicated()
    {
        var snapshots = ScanInts(AscInt, topN: 5, items: [2, 2, 2]);

        snapshots[^1].Should().Equal(2, 2, 2);
    }

    [Fact]
    public void EqualScoreDifferentIdentity_BothKept()
    {
        // Comparer is by Score only — different Names with the same Score must
        // both survive (the SortedSet pitfall).
        var items = new[]
        {
            new Suggestion("alpha", 1),
            new Suggestion("beta",  1),
            new Suggestion("gamma", 1),
        };

        var snapshots = new List<IReadOnlyList<Suggestion>>();
        items.ToObservable().ScanTopN(5, ScoreComparer).Subscribe(snapshots.Add);

        snapshots[^1].Should().HaveCount(3);
        snapshots[^1].Select(s => s.Name).Should().BeEquivalentTo(new[] { "alpha", "beta", "gamma" });
    }

    // ──────────────────────── Performance / no-resort proxy ────────────────────────

    [Fact]
    public void ComparerInvocations_AreLogarithmicPerInsert()
    {
        // If the implementation re-sorted on every insert, comparisons would scale
        // O(n log n) per element, i.e. ~n² total over n inputs. Binary insert into
        // an ImmutableList is O(log n) per element, so total compares stay below
        // a generous bound.
        var compareCount = 0;
        var counting = Comparer<int>.Create((a, b) =>
        {
            Interlocked.Increment(ref compareCount);
            return a.CompareTo(b);
        });

        const int n = 1024;
        var items = Enumerable.Range(0, n).Reverse(); // worst case for naive sort
        var sink = new List<IReadOnlyList<int>>();
        items.ToObservable().ScanTopN(n, counting).Subscribe(sink.Add);

        // Binary insert: ~ sum_{k=1..n} log2(k) ≈ n*log2(n) - n/ln(2)
        // For n=1024: ≈ 1024*10 - 1477 ≈ 8763. A re-sort approach would be ≈ n²/2 ≈ 524k.
        // Bound at 4x the theoretical to leave headroom.
        var bound = 4 * (int)(n * Math.Log2(n));
        compareCount.Should().BeLessThan(bound,
            $"binary insertion into ImmutableList must remain ~O(n log n) total ({n * Math.Log2(n):F0}); re-sort would blow this");
    }

    // ──────────────────────── IAsyncEnumerable bridge ────────────────────────

    [Fact]
    public async Task IAsyncEnumerableOverload_StreamsSnapshots()
    {
        var source = AsyncRange(new[] { 5, 1, 3, 2, 4 });
        var snapshots = new List<IReadOnlyList<int>>();
        var done = new TaskCompletionSource();

        source.ScanTopN(3, AscInt).Subscribe(
            snapshots.Add,
            () => done.TrySetResult());

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        snapshots[^1].Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task IAsyncEnumerableOverload_DisposalCancelsIterator()
    {
        var cancelled = false;
        var started = new TaskCompletionSource();

        var sub = StallingSource(started, () => cancelled = true)
            .ScanTopN(3, AscInt)
            .Subscribe(_ => { });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sub.Dispose();

        // Give the producer a moment to observe the cancellation.
        for (var i = 0; i < 50 && !cancelled; i++)
            await Task.Delay(20);

        cancelled.Should().BeTrue();
    }

    // ──────────────────────── Helpers ────────────────────────

    private static List<IReadOnlyList<int>> ScanInts(IComparer<int> cmp, int topN, IEnumerable<int> items)
    {
        var snapshots = new List<IReadOnlyList<int>>();
        items.ToObservable().ScanTopN(topN, cmp).Subscribe(snapshots.Add);
        return snapshots;
    }

    private record Suggestion(string Name, int Score);

    private static readonly IComparer<Suggestion> ScoreComparer =
        Comparer<Suggestion>.Create((a, b) => a.Score.CompareTo(b.Score));

    private static async IAsyncEnumerable<int> AsyncRange(IEnumerable<int> values)
    {
        foreach (var v in values)
        {
            await Task.Yield();
            yield return v;
        }
    }

    private static async IAsyncEnumerable<int> StallingSource(
        TaskCompletionSource started,
        Action onCancelled,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            onCancelled();
            throw;
        }
        yield break;
    }
}
