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
/// </summary>
public class EventLogTest
{
    private static MeshChangeEvent Created(string id) =>
        MeshChangeEvent.Created(new MeshNode(id) { NodeType = "X" });

    [Fact]
    public async Task Writer_persists_and_dedups()
    {
        var feed = new InProcessMeshChangeFeed();
        var store = new InMemoryEventLogStore();
        var writer = new EventLogWriter(feed, store);
        await writer.StartAsync(default);

        var a = Created("A");
        feed.Publish(a);
        feed.Publish(Created("B"));
        Assert.Equal(2, (await store.ReadFrom(0).FirstAsync().ToTask()).Count);

        // Re-publishing the SAME event (same Path/Kind/Version) does not add a row.
        feed.Publish(a);
        Assert.Equal(2, (await store.ReadFrom(0).FirstAsync().ToTask()).Count);
        Assert.Equal(2, await store.MaxSeq().FirstAsync().ToTask());
    }

    [Fact]
    public async Task Replay_redelivers_unprocessed_and_advances_cursor()
    {
        var feed = new InProcessMeshChangeFeed();
        var store = new InMemoryEventLogStore();

        // Two events already durably logged (as if written before this consumer existed).
        await store.Append(Created("A")).ToTask();
        await store.Append(Created("B")).ToTask();

        // A fresh subscriber (stands in for the runner) attached AFTER those were logged.
        var received = new List<string>();
        using var sub = feed.Subscribe(c => received.Add(c.Path));

        var replay = new EventLogReplayService(feed, store);
        await replay.StartAsync(default);   // synchronous store ops → publishes on start

        Assert.Contains("A", received);
        Assert.Contains("B", received);
        Assert.Equal(2, await store.GetCursor(EventLogReplayService.RunnerConsumerId).FirstAsync().ToTask());

        // A second replay (e.g. another restart) with the cursor already at 2 redelivers nothing.
        received.Clear();
        await new EventLogReplayService(feed, store).StartAsync(default);
        Assert.Empty(received);
    }

    [Fact]
    public async Task Replay_drains_all_pages_when_backlog_exceeds_one_page()
    {
        var feed = new InProcessMeshChangeFeed();
        var store = new InMemoryEventLogStore();

        // A backlog LARGER than the replay page size (500): a single ReadFrom page would leave the
        // remainder unreplayed forever. The drain must paginate until the whole backlog is delivered.
        const int count = 1201;
        for (var i = 0; i < count; i++)
            await store.Append(Created($"N{i}")).ToTask();

        var received = new List<string>();
        using var sub = feed.Subscribe(c => received.Add(c.Path));

        await new EventLogReplayService(feed, store).StartAsync(default);

        Assert.Equal(count, received.Count);   // every page drained, not just the first 500
        Assert.Equal(count, await store.GetCursor(EventLogReplayService.RunnerConsumerId).FirstAsync().ToTask());
    }
}
