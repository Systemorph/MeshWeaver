using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Blazor.Components.Monaco;
using Xunit;

namespace MeshWeaver.Autocomplete.Test;

/// <summary>
/// Pins the C# half of the issue-#542 fix: <see cref="MonacoCompletionSession"/> (the state
/// machine behind <c>MonacoEditorView.GetAsyncCompletions</c>) must stamp every snapshot it
/// pushes to the Monaco suggest widget with the query the snapshot answers. The JS provider
/// (MonacoEditorView.razor.js) buffers pushed snapshots and consumes the buffer instead of
/// fetching — keyed by exactly this stamp. An unkeyed push let a snapshot computed for an
/// earlier trigger token (e.g. <c>@</c>) be served as the first result for the current one
/// (e.g. <c>@Zebra</c>) while suppressing the fetch for the real query, so only a manual
/// re-trigger showed the right list. The JS consumption itself is not covered here (no JS
/// harness in this suite); these tests pin the contract the JS keying relies on.
/// </summary>
public class MonacoCompletionSessionTest
{
    private static CompletionItem Item(string label) => new() { Label = label };

    private sealed record Push(string Query, CompletionItem[] Items);

    [Fact]
    public void FirstQuery_SubscribesOnce_PushesSnapshotsKeyedByQuery()
    {
        var subject = new Subject<IReadOnlyList<CompletionItem>>();
        var subscribed = new List<string>();
        var pushes = new List<Push>();
        var session = new MonacoCompletionSession(
            q => { subscribed.Add(q); return subject; },
            (q, items) => pushes.Add(new(q, items)),
            (_, ex) => throw ex);

        // First invoke for a fresh query: no snapshot yet — returns empty synchronously.
        session.GetCompletions("@").Should().BeEmpty();
        subscribed.Should().Equal("@");

        var snapshot = new[] { Item("Alpha"), Item("Beta") };
        subject.OnNext(snapshot);

        // The push carries the query the snapshot answers — the JS pending-buffer key.
        pushes.Should().HaveCount(1);
        pushes[0].Query.Should().Be("@");
        pushes[0].Items.Should().Equal(snapshot);

        // Same query again: serves the live snapshot without opening a second subscription.
        session.GetCompletions("@").Should().Equal(snapshot);
        subscribed.Should().Equal("@");
    }

    [Fact]
    public void SupersededQuery_SnapshotIsNeverServedForTheNewTrigger()
    {
        var subjects = new Dictionary<string, Subject<IReadOnlyList<CompletionItem>>>();
        var pushes = new List<Push>();
        var session = new MonacoCompletionSession(
            q => subjects[q] = new(),
            (q, items) => pushes.Add(new(q, items)),
            (_, ex) => throw ex);

        // The #542 sequence: Monaco's first provider request fires with the bare trigger…
        session.GetCompletions("@");
        var staleItems = new[] { Item("Alpha") };
        subjects["@"].OnNext(staleItems); // …and its snapshot lands while the user typed on.
        pushes.Should().HaveCount(1);
        pushes[0].Query.Should().Be("@");

        // The next provider request carries the full token. The "@" snapshot must not leak
        // into it: the old subscription is disposed and the new query starts empty.
        session.GetCompletions("@Zebra").Should().BeEmpty();
        subjects["@"].HasObservers.Should().BeFalse();

        var zebraItems = new[] { Item("Zebra Fund") };
        subjects["@Zebra"].OnNext(zebraItems);

        // Every push is stamped with the query of the subscription that produced it,
        // so the JS side can discard the "@" buffer when completing "@Zebra".
        pushes.Should().HaveCount(2);
        pushes[1].Query.Should().Be("@Zebra");
        pushes[1].Items.Should().Equal(zebraItems);
        session.GetCompletions("@Zebra").Should().Equal(zebraItems);
    }

    [Fact]
    public void SynchronousEmissionOnSubscribe_IsReturnedByTheFirstCall()
    {
        var items = new[] { Item("Alpha") };
        var pushes = new List<Push>();
        var session = new MonacoCompletionSession(
            _ => Observable.Return<IReadOnlyList<CompletionItem>>(items),
            (q, arr) => pushes.Add(new(q, arr)),
            (_, ex) => throw ex);

        // A source that emits synchronously on Subscribe: the first snapshot for the trigger
        // reflects that trigger's query — returned by the very first call, and pushed keyed.
        session.GetCompletions("@").Should().Equal(items);
        pushes.Should().HaveCount(1);
        pushes[0].Query.Should().Be("@");
        pushes[0].Items.Should().Equal(items);
    }

    [Fact]
    public void StreamError_ReportsTheFailingQuery()
    {
        var failures = new List<(string Query, Exception Error)>();
        var boom = new InvalidOperationException("boom");
        var session = new MonacoCompletionSession(
            _ => Observable.Throw<IReadOnlyList<CompletionItem>>(boom),
            (_, _) => { },
            (q, ex) => failures.Add((q, ex)));

        session.GetCompletions("@x").Should().BeEmpty();
        failures.Should().HaveCount(1);
        failures[0].Query.Should().Be("@x");
        failures[0].Error.Should().BeSameAs(boom);
    }

    [Fact]
    public void Dispose_ClosesTheActiveSubscription()
    {
        var subject = new Subject<IReadOnlyList<CompletionItem>>();
        var session = new MonacoCompletionSession(
            _ => subject,
            (_, _) => { },
            (_, ex) => throw ex);

        session.GetCompletions("@");
        subject.HasObservers.Should().BeTrue();

        session.Dispose();
        subject.HasObservers.Should().BeFalse();
    }
}
