using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the fan-out contract of the IN-MEMORY storage adapter's change feed — the feed EVERY
/// monolith test mesh (and every in-memory partition in a real portal) publishes writes on.
///
/// <para>These are the in-memory twins of <c>IsolatedChangeFeedTests</c> /
/// <c>MergedFeedFanoutIsolationTests</c> over in the Postgres suite. The defect they killed on the
/// Postgres feeds (issue #889) was never fixed here: <see cref="InMemoryStorageAdapter"/> published
/// through a plain <see cref="Subject{T}"/> wrapped in <c>catch { }</c>, which is fan-out-hostile
/// twice over — <c>Subject.OnNext</c> delivers synchronously in subscription order, so ONE
/// subscriber throwing aborts delivery to every subscriber after it, and the <c>catch</c> then turns
/// that into silence.</para>
///
/// <para>The subscriber that throws is not hypothetical. Every live synced query opens a
/// <c>persistence.Changes → changeBuffer</c> pipeline (<c>StorageAdapterMeshQueryProvider</c>), and
/// a pipeline caught in its teardown window has a DISPOSED <c>changeBuffer</c> while the feeding
/// subscription is still delivering — the <see cref="ObjectDisposedException"/> shape. One-shot
/// queries (<c>IMeshService.QueryAsync</c>, autocomplete, path resolution) open and tear down that
/// pipeline constantly, so the window is hit whenever a write lands during a teardown.</para>
///
/// <para>Downstream symptom (issue #1053): a LIVE children query silently stops re-emitting after a
/// create that completed successfully — the change notification was owed and never delivered, with
/// no error and no log line anywhere.</para>
/// </summary>
public class InMemoryChangeFeedFanoutIsolationTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Node(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0
            ? new MeshNode(path) { NodeType = "Markdown" }
            : new MeshNode(path[(slash + 1)..], path[..slash]) { NodeType = "Markdown" };
    }

    /// <summary>
    /// A query pipeline caught in its teardown window: the buffer <see cref="Subject{T}"/> it feeds
    /// is already disposed while the feed subscription still delivers into it — exactly what a
    /// torn-down <c>StorageAdapterMeshQueryProvider</c> pipeline exposes to the adapter's feed.
    /// </summary>
    private static IDisposable SubscribeDeadPipeline(IStorageAdapter adapter)
    {
        var deadBuffer = new Subject<DataChangeNotification>();
        var feedSub = adapter.Changes.Subscribe(deadBuffer);
        deadBuffer.Dispose();
        return new CompositeDisposable(feedSub, deadBuffer);
    }

    [Fact]
    public void A_disposed_sibling_subscriber_must_not_abort_delivery_to_later_subscribers()
    {
        var adapter = new InMemoryStorageAdapter();

        // Subscription ORDER is the point: the dead pipeline sits BEFORE the victim, exactly like
        // an ephemeral one-shot query relative to the long-lived synced query it out-orders.
        using var dead = SubscribeDeadPipeline(adapter);
        var received = new List<string>();
        using var victim = adapter.Changes.Subscribe(n => received.Add(n.Path));

        adapter.Write(Node("rbuergi/agent-files/two.md"), Options).Subscribe();

        received.Should().Contain("rbuergi/agent-files/two.md",
            "a disposed sibling subscriber must not abort the change-feed fan-out to the "
            + "subscribers after it — that is a silently dropped change notification, and on a live "
            + "children query it is a view that stops updating (issue #1053)");
    }

    [Fact]
    public void A_disposed_sibling_subscriber_must_not_abort_a_delete_notification()
    {
        var adapter = new InMemoryStorageAdapter();
        adapter.Write(Node("rbuergi/agent-files/gone.md"), Options).Subscribe();

        using var dead = SubscribeDeadPipeline(adapter);
        var received = new List<string>();
        using var victim = adapter.Changes.Subscribe(n => received.Add(n.Path));

        adapter.Delete("rbuergi/agent-files/gone.md").Subscribe();

        received.Should().Contain("rbuergi/agent-files/gone.md",
            "a delete is the notification a live collection MUST see — a subscriber that misses it "
            + "keeps rendering a node that no longer exists");
    }

    /// <summary>
    /// Throws on every notification, as an EXPLICIT observer: Rx's <c>Subscribe(Action&lt;T&gt;)</c>
    /// wrapper auto-detaches itself when the handler throws, which would hide whether the FEED kept
    /// the subscriber. This shape asks the feed the question directly.
    /// </summary>
    private sealed class Thrower : IObserver<DataChangeNotification>
    {
        public int Calls { get; private set; }
        public void OnNext(DataChangeNotification value)
        {
            Calls++;
            throw new InvalidOperationException("subscriber blew up");
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    [Fact]
    public void A_transient_throw_isolates_but_keeps_the_subscriber_and_the_write_still_succeeds()
    {
        var adapter = new InMemoryStorageAdapter();

        var thrower = new Thrower();
        using var faulty = adapter.Changes.Subscribe(thrower);
        var received = new List<string>();
        using var victim = adapter.Changes.Subscribe(n => received.Add(n.Path));

        MeshNode? written = null;
        adapter.Write(Node("rbuergi/agent-files/a.md"), Options).Subscribe(n => written = n);
        adapter.Write(Node("rbuergi/agent-files/b.md"), Options).Subscribe();

        written.Should().NotBeNull("a subscriber's fault must never fail the write that published it");
        thrower.Calls.Should().Be(2,
            "a transient throw must not permanently disable a LIVE subscriber — dropping it would "
            + "starve it of every future change, which is the very failure this isolation prevents");
        received.Should().Equal("rbuergi/agent-files/a.md", "rbuergi/agent-files/b.md");
    }
}
