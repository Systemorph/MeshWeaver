using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI.Completion;
using MeshWeaver.Data.Completion;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests <see cref="AutocompleteStreamProvider"/>'s streaming behavior end-to-end:
/// items push through providers one at a time, the snapshot grows incrementally,
/// fast providers emit before slow ones, and the consumer can switch queries
/// (simulating Monaco-style user typing) without leaking subscriptions.
/// </summary>
public class AutocompleteStreamProviderTests
{
    // ──────────────────────── Single provider, incremental snapshots ────────────────────────

    /// <summary>Single provider emits one snapshot per item and the final snapshot contains every item.</summary>
    [Fact]
    public async Task SingleProvider_EmitsSnapshotPerItem_FinalContainsAll()
    {
        var provider = new ScriptedProvider();
        var sut = new AutocompleteStreamProvider([provider], topN: 10);

        var snapshots = new List<IReadOnlyList<AutocompleteItem>>();
        var done = new TaskCompletionSource();
        using var sub = sut.Stream("foo", null).Subscribe(snapshots.Add, () => done.TrySetResult());

        // Push three items, then complete the source
        provider.Emit(new AutocompleteItem("a", "a", Priority: 30));
        provider.Emit(new AutocompleteItem("b", "b", Priority: 20));
        provider.Emit(new AutocompleteItem("c", "c", Priority: 10));
        provider.Complete();

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Initial empty snapshot + one per item = 4
        snapshots.Should().HaveCount(4);
        snapshots[0].Should().BeEmpty();
        snapshots[3].Select(i => i.Label).Should().Equal("a", "b", "c");
    }

    /// <summary>Items emitted out of priority order are sorted by descending priority in the snapshot.</summary>
    [Fact]
    public async Task ItemsArrivingOutOfOrder_AreSortedByPriorityDescending()
    {
        var provider = new ScriptedProvider();
        var sut = new AutocompleteStreamProvider([provider], topN: 10);

        var snapshots = new List<IReadOnlyList<AutocompleteItem>>();
        var done = new TaskCompletionSource();
        using var sub = sut.Stream("foo", null).Subscribe(snapshots.Add, () => done.TrySetResult());

        provider.Emit(new AutocompleteItem("low", "low", Priority: 5));
        provider.Emit(new AutocompleteItem("high", "high", Priority: 100));
        provider.Emit(new AutocompleteItem("mid", "mid", Priority: 50));
        provider.Complete();

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        snapshots[^1].Select(i => i.Label).Should().Equal("high", "mid", "low");
    }

    // ──────────────────────── Multiple providers, fast-then-slow ────────────────────────

    /// <summary>With a fast and a slow provider, fast items appear in early snapshots and slow ones merge in later.</summary>
    [Fact]
    public async Task FastAndSlowProviders_FastItemsAppearBeforeSlowOnes()
    {
        // "fast" emits immediately; we wait for those snapshots to flow through
        // ScanTopN before the "slow" provider starts emitting. Then assert that
        // the snapshot history shows the fast items growing first, the slow ones
        // merging in afterwards.
        var fast = new ScriptedProvider();
        var slow = new ScriptedProvider();
        var sut = new AutocompleteStreamProvider([fast, slow], topN: 10);

        var snapshots = new List<IReadOnlyList<AutocompleteItem>>();
        var done = new TaskCompletionSource();
        using var sub = sut.Stream("foo", null).Subscribe(snapshots.Add, () => done.TrySetResult());

        // Fast provider streams first — wait for both items to be reflected.
        fast.Emit(new AutocompleteItem("local-a", "local-a", Priority: 90));
        fast.Emit(new AutocompleteItem("local-b", "local-b", Priority: 80));
        fast.Complete();
        await WaitForSnapshotCountAsync(snapshots, expected: 3);

        // Snapshot must contain only fast items at this point.
        snapshots[^1].Select(i => i.Label).Should().Equal("local-a", "local-b");

        // Slow provider emits later — final snapshot has all four.
        slow.Emit(new AutocompleteItem("remote-a", "remote-a", Priority: 70));
        slow.Emit(new AutocompleteItem("remote-b", "remote-b", Priority: 60));
        slow.Complete();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        snapshots[^1].Select(i => i.Label).Should()
            .Equal("local-a", "local-b", "remote-a", "remote-b");
    }

    private static async Task WaitForSnapshotCountAsync<T>(
        IReadOnlyList<T> snapshots, int expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (snapshots.Count < expected && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        snapshots.Count.Should().BeGreaterThanOrEqualTo(expected,
            $"snapshot count never reached {expected} within timeout");
    }

    /// <summary>An exception thrown by one provider does not terminate the merged stream — other providers keep emitting.</summary>
    [Fact]
    public async Task FailingProvider_DoesNotKillTheStream()
    {
        var ok = new ScriptedProvider();
        var bad = new FailingProvider();
        var sut = new AutocompleteStreamProvider([ok, bad], topN: 10);

        var snapshots = new List<IReadOnlyList<AutocompleteItem>>();
        var done = new TaskCompletionSource();
        var errors = new List<Exception>();
        using var sub = sut.Stream("foo", null).Subscribe(
            snapshots.Add,
            ex => { errors.Add(ex); done.TrySetResult(); },
            () => done.TrySetResult());

        ok.Emit(new AutocompleteItem("a", "a", Priority: 10));
        ok.Emit(new AutocompleteItem("b", "b", Priority: 5));
        ok.Complete();

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        errors.Should().BeEmpty("provider exceptions are swallowed by Catch(Empty)");
        snapshots[^1].Select(i => i.Label).Should().Equal("a", "b");
    }

    // ──────────────────────── Top-N truncation ────────────────────────

    /// <summary>Top-N truncation drops items whose priority falls below the current Nth-ranked item.</summary>
    [Fact]
    public async Task TopN_DropsItemsBelowTheCurrentNthPriority()
    {
        var provider = new ScriptedProvider();
        var sut = new AutocompleteStreamProvider([provider], topN: 2);

        var snapshots = new List<IReadOnlyList<AutocompleteItem>>();
        var done = new TaskCompletionSource();
        using var sub = sut.Stream("foo", null).Subscribe(snapshots.Add, () => done.TrySetResult());

        provider.Emit(new AutocompleteItem("a", "a", Priority: 10));
        provider.Emit(new AutocompleteItem("b", "b", Priority: 20));
        provider.Emit(new AutocompleteItem("c", "c", Priority: 5));    // dropped
        provider.Emit(new AutocompleteItem("d", "d", Priority: 100));  // displaces 'a'
        provider.Complete();

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        snapshots[^1].Should().HaveCount(2);
        snapshots[^1].Select(i => i.Label).Should().Equal("d", "b");
    }

    // ──────────────────────── Monaco-style: varying input over time ────────────────────────

    /// <summary>Sequential subscriptions with different queries each receive their own snapshot stream without leakage between queries.</summary>
    [Fact]
    public async Task UserTypingMultipleQueries_EachQueryGetsItsOwnSnapshotStream()
    {
        // Simulates Monaco re-invoking the callback as the user types: "a", "ab", "abc".
        // Each subscription should receive the snapshots for its own query and not leak
        // items from prior subscriptions.
        var provider = new QueryAwareProvider();
        var sut = new AutocompleteStreamProvider([provider], topN: 10);

        var queries = new[] { "a", "ab", "abc" };
        var collected = new Dictionary<string, List<IReadOnlyList<AutocompleteItem>>>();

        foreach (var query in queries)
        {
            var snapshots = new List<IReadOnlyList<AutocompleteItem>>();
            var done = new TaskCompletionSource();
            using var sub = sut.Stream(query, null).Subscribe(snapshots.Add, () => done.TrySetResult());

            // The provider produces (query.Length) items, each labelled with the query
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            collected[query] = snapshots;
        }

        collected["a"][^1].Select(i => i.Label).Should().Equal("a-1");
        collected["ab"][^1].Select(i => i.Label).Should().Equal("ab-1", "ab-2");
        collected["abc"][^1].Select(i => i.Label).Should().Equal("abc-1", "abc-2", "abc-3");
    }

    /// <summary>Disposing the subscription before the provider completes stops further snapshots from reaching the observer.</summary>
    [Fact]
    public async Task UnsubscribeBeforeCompletion_StopsReceivingFurtherSnapshots()
    {
        // Simulates the user clearing the autocomplete (Monaco disposes the subscription)
        // before the provider finishes emitting.
        var provider = new ScriptedProvider();
        var sut = new AutocompleteStreamProvider([provider], topN: 10);

        var snapshots = new List<IReadOnlyList<AutocompleteItem>>();
        var sub = sut.Stream("foo", null).Subscribe(snapshots.Add);

        provider.Emit(new AutocompleteItem("a", "a", Priority: 10));
        await WaitForSnapshotCountAsync(snapshots, expected: 2); // initial empty + a
        var countBeforeDispose = snapshots.Count;

        sub.Dispose();

        // Try to emit more after disposal — provider tolerates the closed downstream.
        try { provider.Emit(new AutocompleteItem("b", "b", Priority: 20)); }
        catch { /* downstream queue may be closed — that's the disposal signal */ }

        // Allow any in-flight scheduled work to drain
        await Task.Delay(100, TestContext.Current.CancellationToken);

        snapshots.Count.Should().Be(countBeforeDispose,
            "after disposal, no further snapshots reach the (now disposed) observer");
    }

    // ──────────────────────── Test doubles ────────────────────────

    /// <summary>
    /// Provider whose <c>GetItems</c> observable forwards everything the test
    /// pushes via <see cref="Emit"/>, and completes when the test calls
    /// <see cref="Complete"/>. The backing <see cref="Subject{T}"/> IS the
    /// observable — no async-enumerable bridge needed.
    /// </summary>
    private sealed class ScriptedProvider : IAutocompleteProvider
    {
        private readonly Subject<AutocompleteItem> subject = new();

        public void Emit(AutocompleteItem item) => subject.OnNext(item);
        public void Complete() => subject.OnCompleted();

        public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null)
            => subject;
    }

    private sealed class FailingProvider : IAutocompleteProvider
    {
        public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null)
            => Observable.Throw<AutocompleteItem>(
                new InvalidOperationException("simulated provider failure"));
    }

    /// <summary>
    /// Provider that emits N items per query, each labelled with the query string.
    /// Lets us verify that subscribing with different queries produces distinct
    /// per-query streams (Monaco user-typing scenario).
    /// </summary>
    private sealed class QueryAwareProvider : IAutocompleteProvider
    {
        public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null)
            => Enumerable.Range(1, query.Length)
                .Select(i => new AutocompleteItem($"{query}-{i}", $"{query}-{i}",
                    Priority: 100 - i))
                .ToObservable();
    }
}
