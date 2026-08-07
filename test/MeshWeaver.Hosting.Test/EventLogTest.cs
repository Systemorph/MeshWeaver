using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Unit tests for the app-level event-log outbox (in-memory store): the writer persists every
/// change-feed event (idempotent by Path/Kind/Version), and the replay service redelivers
/// not-yet-processed entries into the feed and advances the cursor.
///
/// <para>🚨 The change feed fans out on its OWN dispatch loop, never on the publisher's thread
/// (issue #899 — a synchronous fan-out deadlocked two concurrently-deleting hubs). So every
/// assertion here waits for the delivery it depends on instead of reading straight after
/// <c>Publish</c>; the pre-#899 shape only passed because publish and delivery shared a thread.</para>
/// </summary>
public class EventLogTest
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    private static MeshChangeEvent Created(string id) =>
        MeshChangeEvent.Created(new MeshNode(id) { NodeType = "X" });

    /// <summary>Waits until the store holds at least <paramref name="expected"/> rows.</summary>
    private static Task<IReadOnlyList<EventLogEntry>> RowsReach(IEventLogStore store, int expected) =>
        Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .SelectMany(_ => store.ReadFrom(0))
            .Where(rows => rows.Count >= expected)
            .FirstAsync()
            .Timeout(DeliveryTimeout)
            .ToTask();

    [Fact(Timeout = 30000)]
    public async Task Writer_persists_and_dedups()
    {
        using var feed = new InProcessMeshChangeFeed();
        var store = new InMemoryEventLogStore();
        var writer = new EventLogWriter(feed, store);
        await writer.StartAsync(default);

        var a = Created("A");
        feed.Publish(a);
        feed.Publish(Created("B"));
        (await RowsReach(store, 2)).Count.Should().Be(2);

        // Re-publishing the SAME event (same Path/Kind/Version) must not add a row. The feed
        // delivers in publish order on a single loop, so once the LATER "C" has landed the
        // duplicate has provably already been processed — no sleep, no polling for absence.
        feed.Publish(a);
        feed.Publish(Created("C"));
        (await RowsReach(store, 3)).Count.Should().Be(3, "the duplicate must not have added a row");
        (await store.MaxSeq().FirstAsync().ToTask()).Should().Be(3);
    }

    [Fact(Timeout = 30000)]
    public async Task Replay_redelivers_unprocessed_and_advances_cursor()
    {
        using var feed = new InProcessMeshChangeFeed();
        var store = new InMemoryEventLogStore();

        // Two events already durably logged (as if written before this consumer existed).
        await store.Append(Created("A")).ToTask();
        await store.Append(Created("B")).ToTask();

        // A fresh subscriber (stands in for the runner) attached AFTER those were logged.
        var received = new ConcurrentQueue<string>();
        var bothDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = feed.Subscribe(c =>
        {
            received.Enqueue(c.Path);
            if (received.Count >= 2)
                bothDelivered.TrySetResult();
        });

        var replay = new EventLogReplayService(feed, store);
        await replay.StartAsync(default);

        await bothDelivered.Task.WaitAsync(DeliveryTimeout);
        received.Should().Contain("A").And.Contain("B");
        (await store.GetCursor(EventLogReplayService.RunnerConsumerId).FirstAsync().ToTask()).Should().Be(2);

        // A second replay (e.g. another restart) with the cursor already at 2 redelivers nothing.
        // Publish a marker afterwards and wait for IT: because the loop is FIFO, the marker's
        // arrival proves any replayed event would already have landed.
        received.Clear();
        var markerSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var marker = feed.Subscribe(c => { if (c.Path == "marker") markerSeen.TrySetResult(); });
        await new EventLogReplayService(feed, store).StartAsync(default);
        feed.Publish(Created("marker"));

        await markerSeen.Task.WaitAsync(DeliveryTimeout);
        received.Should().Equal("marker");
    }

    [Fact(Timeout = 30000)]
    public async Task Replay_drains_all_pages_when_backlog_exceeds_one_page()
    {
        using var feed = new InProcessMeshChangeFeed();
        var store = new InMemoryEventLogStore();

        // A backlog LARGER than the replay page size (500): a single ReadFrom page would leave the
        // remainder unreplayed forever. The drain must paginate until the whole backlog is delivered.
        const int count = 1201;
        for (var i = 0; i < count; i++)
            await store.Append(Created($"N{i}")).ToTask();

        var received = new ConcurrentQueue<string>();
        var allDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = feed.Subscribe(c =>
        {
            received.Enqueue(c.Path);
            if (received.Count >= count)
                allDelivered.TrySetResult();
        });

        await new EventLogReplayService(feed, store).StartAsync(default);

        await allDelivered.Task.WaitAsync(DeliveryTimeout);
        received.Count.Should().Be(count, "every page must drain, not just the first 500");
        (await store.GetCursor(EventLogReplayService.RunnerConsumerId).FirstAsync().ToTask()).Should().Be(count);
    }
}
