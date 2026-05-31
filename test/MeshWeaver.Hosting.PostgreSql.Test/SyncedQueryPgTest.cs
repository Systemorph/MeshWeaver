using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Verifies the synced-query pipeline shape used by
/// <see cref="MeshWeaver.Graph.SyncedQueryMeshNodes"/> works against PostgreSQL:
/// <c>ObserveQuery</c> â†’ fold Initial / Added / Updated / Removed into a
/// path â†’ <see cref="MeshNode"/> dictionary â†’ emit values. PG drives the
/// Update / Delete events through pg_notify so we can verify the dict folds
/// correctly without the in-memory race the MeshQueryEngine exhibits.
/// </summary>
[Collection("PostgreSql")]
public class SyncedQueryPgTest : IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();
    private PostgreSqlChangeListener _listener = null!;
    private PostgreSqlMeshQuery _query = null!;

    public SyncedQueryPgTest(PostgreSqlFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.CleanDataAsync();
        await _fixture.AccessControl.GrantAsync("ACME", "Anonymous", "Read",
            isAllow: true, TestContext.Current.CancellationToken);
        // PG LISTEN pumps into the adapter's Changes Subject; query subscribes
        // to adapter.Changes for live updates.
        _listener = new PostgreSqlChangeListener(_fixture.DataSource, _fixture.StorageAdapter.ChangeObserver);
        await _listener.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        _query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
    }

    /// <summary>
    /// Build the same fold-into-dict pipeline SyncedQueryMeshNodes uses,
    /// but inline against PostgreSqlMeshQuery so we can verify add/update/
    /// delete each cause a re-emission with the correct collection.
    /// </summary>
    private IObservable<IReadOnlyDictionary<string, MeshNode>> BuildSyncedCollection(string queryString)
    {
        return _query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(queryString), _options)
            .Scan(
                ImmutableDictionary<string, MeshNode>.Empty,
                (dict, change) => change.ChangeType switch
                {
                    QueryChangeType.Initial or QueryChangeType.Reset
                        or QueryChangeType.Added or QueryChangeType.Updated =>
                        change.Items.Aggregate(dict, (d, n) =>
                            string.IsNullOrEmpty(n.Path) ? d : d.SetItem(n.Path, n)),
                    QueryChangeType.Removed =>
                        change.Items.Aggregate(dict, (d, n) =>
                            string.IsNullOrEmpty(n.Path) ? d : d.Remove(n.Path)),
                    _ => dict,
                })
            .Select(d => (IReadOnlyDictionary<string, MeshNode>)d);
    }

    private async Task WriteNode(string id, string ns, string nodeType, string? name = null)
    {
        await _fixture.StorageAdapter.WriteAsync(new MeshNode(id, ns)
        {
            Name = name ?? id,
            NodeType = nodeType,
            State = MeshNodeState.Active,
        }, _options, TestContext.Current.CancellationToken);
    }

    /// <summary>Initial empty result set emits an empty dictionary.</summary>
    [Fact(Timeout = 30000)]
    public void EmptyQuery_EmitsEmptyDictionary()
    {
        var collection = BuildSyncedCollection("namespace:ACME/Project").Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();
        var initial = collection.Should().Within(15.Seconds()).Emit();
        initial.Should().BeEmpty();
    }

    /// <summary>Adding a matching node grows the dictionary.</summary>
    [Fact(Timeout = 30000)]
    public async Task Add_NewMatchingNode_GrowsDictionary()
    {
        var collection = BuildSyncedCollection("namespace:ACME/Project").Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();
        collection.Should().Within(15.Seconds()).Emit();

        await WriteNode("Story1", "ACME/Project", "Story");

        collection.Should().Within(15.Seconds()).Match(d => d.ContainsKey("ACME/Project/Story1"));
    }

    /// <summary>Updating a node's content re-emits the dictionary with new value.</summary>
    [Fact(Timeout = 30000)]
    public async Task Update_ExistingNode_ReEmitsWithNewValue()
    {
        await WriteNode("Story1", "ACME/Project", "Story", "Original");

        var collection = BuildSyncedCollection("namespace:ACME/Project").Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();

        collection.Should().Within(15.Seconds())
            .Match(d => d.TryGetValue("ACME/Project/Story1", out var n) && n.Name == "Original");

        await WriteNode("Story1", "ACME/Project", "Story", "Updated");

        collection.Should().Within(15.Seconds())
            .Match(d => d.TryGetValue("ACME/Project/Story1", out var n) && n.Name == "Updated");
    }

    /// <summary>
    /// Diagnostic: capture every ObserveQuery emission for a delete to see
    /// what the Removed event payload actually carries (Path? Id?). Tells
    /// us whether the dict-Remove is the bug or the upstream event itself.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Diagnostic_DeleteEmitsRemoved_WithPath()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteNode("Story1", "ACME/Project", "Story");
        await WriteNode("Story2", "ACME/Project", "Story");

        // Replay(1).RefCount() instead of Publish()+Connect():
        // Publish loses any emission fired before subscribers attach —
        // the previous Connect()-then-Subscribe() shape raced the Initial
        // emission and intermittently missed it under CI load.
        // Replay(1) buffers the latest emission so a late subscriber still
        // sees it; RefCount starts the upstream on the first subscriber.
        // The Removed predicate ignores the buffered Initial — only the
        // post-delete Removed event matches.
        var stream = _query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:ACME/Project"), _options)
            .Replay(1)
            .RefCount();

        // Hold a keep-alive subscription for the whole test so RefCount never
        // drops to 0. Without it, waiting for Initial on one subscription and
        // then attaching the Removed subscription separately tears the upstream
        // ObserveQuery down (RefCount 1→0) and reconnects it (0→1) — re-issuing
        // the PG LISTEN. If the delete's pg_notify races that reconnect gap the
        // Removed event is lost and the test times out at 15s. A single
        // persistent subscriber pins the LISTEN open from Initial through delete.
        using var keepAlive = stream.Subscribe();

        // Wait for the Initial emission deterministically via the tests-only
        // Rx→Task bridge — NOT a blocking .Should().Match(...). This method must
        // stay async (it awaits DeleteAsync + the Removed payload out-of-band),
        // and a blocking ManualResetEventSlim.Wait() inside an xUnit async
        // SynchronizationContext can starve the continuations delivering the PG
        // emission → deadlock/timeout under CI load.
        var initial = await stream
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);
        initial.ChangeType.Should().Be(QueryChangeType.Initial);

        // Pre-subscribe to Removed BEFORE issuing the delete. The hot Task
        // completes when the next Removed emission arrives — Replay(1)'s
        // buffer holds Initial (not Removed), so this filter only matches
        // the post-delete event. This pre-subscription ordering is load-bearing
        // (it must attach before the delete races in), so it stays an explicit
        // Task rather than a post-delete .Should().Match(...).
        var removedTask = stream
            .Where(c => c.ChangeType == QueryChangeType.Removed)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);

        await _fixture.StorageAdapter.DeleteAsync("ACME/Project/Story1", ct);

        var removed = await removedTask;
        removed.Should().NotBeNull("PG ObserveQuery must emit Removed after delete");
        var removedItem = removed!.Items.SingleOrDefault();
        removedItem.Should().NotBeNull();
        removedItem!.Path.Should().Be("ACME/Project/Story1",
            "Removed payload must carry Path so dict-Remove finds the entry");
    }

    /// <summary>Deleting a node removes it from the dictionary.</summary>
    [Fact(Timeout = 30000)]
    public async Task Delete_RemovesFromDictionary()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteNode("Story1", "ACME/Project", "Story");
        await WriteNode("Story2", "ACME/Project", "Story");

        var collection = BuildSyncedCollection("namespace:ACME/Project").Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();

        collection.Should().Within(15.Seconds())
            .Match(d => d.ContainsKey("ACME/Project/Story1") && d.ContainsKey("ACME/Project/Story2"));

        await _fixture.StorageAdapter.DeleteAsync("ACME/Project/Story1", ct);

        var afterDelete = collection.Should().Within(15.Seconds())
            .Match(d => !d.ContainsKey("ACME/Project/Story1"));
        afterDelete.ContainsKey("ACME/Project/Story2").Should().BeTrue();
    }

    /// <summary>
    /// Multi-query union: two queries, the synced collection holds the
    /// union of both result sets. Verifies the multi-query Merge shape
    /// SyncedQueryMeshNodes uses for <c>workspace.GetQuery(name, q1, q2, ...)</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UnionOfTwoQueries_HoldsBoth()
    {
        // Two queries that match disjoint partitions but contribute to the same union.
        await WriteNode("S1", "ACME/Project", "Story");
        await WriteNode("E1", "ACME/Epic", "Epic");

        var unioned = _query.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:ACME/Project"), _options)
            .Merge(_query.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:ACME/Epic"), _options))
            .Scan(
                ImmutableDictionary<string, MeshNode>.Empty,
                (dict, change) => change.ChangeType switch
                {
                    QueryChangeType.Initial or QueryChangeType.Reset
                        or QueryChangeType.Added or QueryChangeType.Updated =>
                        change.Items.Aggregate(dict, (d, n) =>
                            string.IsNullOrEmpty(n.Path) ? d : d.SetItem(n.Path, n)),
                    QueryChangeType.Removed =>
                        change.Items.Aggregate(dict, (d, n) =>
                            string.IsNullOrEmpty(n.Path) ? d : d.Remove(n.Path)),
                    _ => dict,
                })
            .Replay(1).RefCount();

        using var keepAlive = unioned.Subscribe();

        unioned.Should().Within(15.Seconds())
            .Match(d => d.ContainsKey("ACME/Project/S1") && d.ContainsKey("ACME/Epic/E1"));
    }
}
