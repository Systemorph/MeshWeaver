using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Verifies the synced-query pipeline shape used by
/// <see cref="MeshWeaver.Graph.SyncedQueryMeshNodes"/> works against PostgreSQL:
/// <c>ObserveQuery</c> → fold Initial / Added / Updated / Removed into a
/// path → <see cref="MeshNode"/> dictionary → emit values. PG drives the
/// Update / Delete events through pg_notify so we can verify the dict folds
/// correctly without the in-memory race the InMemoryMeshQuery exhibits.
/// </summary>
[Collection("PostgreSql")]
public class SyncedQueryPgTest : IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();
    private DataChangeNotifier _notifier = null!;
    private PostgreSqlChangeListener _listener = null!;
    private PostgreSqlMeshQuery _query = null!;

    public SyncedQueryPgTest(PostgreSqlFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.CleanDataAsync();
        await _fixture.AccessControl.GrantAsync("ACME", "Anonymous", "Read",
            isAllow: true, TestContext.Current.CancellationToken);
        _notifier = new DataChangeNotifier();
        _listener = new PostgreSqlChangeListener(_fixture.DataSource, _notifier);
        await _listener.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        _query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, _notifier);
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
        _notifier.Dispose();
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
    public async Task EmptyQuery_EmitsEmptyDictionary()
    {
        var ct = TestContext.Current.CancellationToken;
        var collection = BuildSyncedCollection("namespace:ACME/Project").Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();
        var initial = await collection.Timeout(15.Seconds()).FirstAsync().ToTask(ct);
        initial.Should().BeEmpty();
    }

    /// <summary>Adding a matching node grows the dictionary.</summary>
    [Fact(Timeout = 30000)]
    public async Task Add_NewMatchingNode_GrowsDictionary()
    {
        var ct = TestContext.Current.CancellationToken;
        var collection = BuildSyncedCollection("namespace:ACME/Project").Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();
        await collection.Take(1).Timeout(15.Seconds()).ToTask(ct);

        await WriteNode("Story1", "ACME/Project", "Story");

        await collection
            .Where(d => d.ContainsKey("ACME/Project/Story1"))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }

    /// <summary>Updating a node's content re-emits the dictionary with new value.</summary>
    [Fact(Timeout = 30000)]
    public async Task Update_ExistingNode_ReEmitsWithNewValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteNode("Story1", "ACME/Project", "Story", "Original");

        var collection = BuildSyncedCollection("namespace:ACME/Project").Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();

        await collection
            .Where(d => d.TryGetValue("ACME/Project/Story1", out var n) && n.Name == "Original")
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);

        await WriteNode("Story1", "ACME/Project", "Story", "Updated");

        await collection
            .Where(d => d.TryGetValue("ACME/Project/Story1", out var n) && n.Name == "Updated")
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
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

        var changes = new List<QueryResultChange<MeshNode>>();
        using var sub = _query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:ACME/Project"), _options)
            .Subscribe(c => changes.Add(c));

        // Wait for Initial.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (changes.Count < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct);
        changes.Should().NotBeEmpty();
        changes[0].ChangeType.Should().Be(QueryChangeType.Initial);

        await _fixture.StorageAdapter.DeleteAsync("ACME/Project/Story1", ct);

        // Wait for Removed.
        deadline = DateTime.UtcNow.AddSeconds(15);
        while (!changes.Any(c => c.ChangeType == QueryChangeType.Removed) && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct);

        var removed = changes.FirstOrDefault(c => c.ChangeType == QueryChangeType.Removed);
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

        await collection
            .Where(d => d.ContainsKey("ACME/Project/Story1") && d.ContainsKey("ACME/Project/Story2"))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);

        await _fixture.StorageAdapter.DeleteAsync("ACME/Project/Story1", ct);

        var afterDelete = await collection
            .Where(d => !d.ContainsKey("ACME/Project/Story1"))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        afterDelete.Should().ContainKey("ACME/Project/Story2");
    }

    /// <summary>
    /// Multi-query union: two queries, the synced collection holds the
    /// union of both result sets. Verifies the multi-query Merge shape
    /// SyncedQueryMeshNodes uses for <c>workspace.GetQuery(name, q1, q2, ...)</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UnionOfTwoQueries_HoldsBoth()
    {
        var ct = TestContext.Current.CancellationToken;

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

        await unioned
            .Where(d => d.ContainsKey("ACME/Project/S1") && d.ContainsKey("ACME/Epic/E1"))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }
}
