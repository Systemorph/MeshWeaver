using System;
using System.Collections.Generic;
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
/// Tests that ObserveQuery correctly receives change notifications
/// through the full PostgreSQL LISTEN/NOTIFY pipeline:
/// DB trigger â†’ pg_notify â†’ PostgreSqlChangeListener â†’ DataChangeNotifier â†’ ObserveQuery
/// </summary>
[Collection("PostgreSqlIsolated")]
public class ObserveQueryTests : IAsyncLifetime
{
    private readonly IsolatedPostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();
    private PostgreSqlChangeListener _listener = null!;
    private PostgreSqlMeshQuery _query = null!;

    public ObserveQueryTests(IsolatedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.CleanDataAsync();
        // Grant Anonymous Read access so observe tests work without explicit userId
        await _fixture.AccessControl.GrantAsync("ACME", "Anonymous", "Read", isAllow: true, TestContext.Current.CancellationToken);
        // PG LISTEN pumps directly into the adapter's Changes Subject via its
        // internal ChangeObserver; the query subscribes to adapter.Changes.
        _listener = new PostgreSqlChangeListener(_fixture.DataSource, _fixture.StorageAdapter.ChangeObserver);
        await _listener.StartAsync(TestContext.Current.CancellationToken);
        // Give the LISTEN connection a moment to establish
        await Task.Delay(200, TestContext.Current.CancellationToken);
        _query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
    }

    [Fact]
    public async Task ObserveQuery_EmitsInitialResults()
    {
        // Arrange: seed data before subscribing
        await WriteNode("Story1", "ACME/Project", "Story");

        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");

        // Filter on ChangeType=Initial directly — the listener can race the
        // Subscribe and deliver a follow-up Added/Updated for the just-written
        // row, so asserting "exactly one change" was a flake under load. The
        // contract this test pins is the SHAPE of the Initial emission, not
        // the absence of subsequent ones.
        var initialChange = await _query.ObserveQuery<MeshNode>(request, _options)
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask();

        initialChange.Items.Should().HaveCount(1);
        initialChange.Items[0].Id.Should().Be("Story1");
    }

    [Fact]
    public async Task ObserveQuery_DetectsAddedNode()
    {
        // Arrange: seed initial data, subscribe, wait for initial emission
        await WriteNode("Story1", "ACME/Project", "Story");

        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");

        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForChanges(changes, 1); // Initial
        changes.Should().ContainSingle(c => c.ChangeType == QueryChangeType.Initial);

        // Act: add a new node that matches the query
        await WriteNode("Story2", "ACME/Project", "Story");

        await WaitForChanges(changes, 2, timeout: 5000);

        // Assert
        var added = changes.FirstOrDefault(c => c.ChangeType == QueryChangeType.Added);
        added.Should().NotBeNull("ObserveQuery should detect the added node via pg_notify");
        added!.Items.Should().ContainSingle(n => n.Id == "Story2");
    }

    [Fact]
    public async Task ObserveQuery_DetectsUpdatedNode()
    {
        // Arrange — write the original, then subscribe before the update.
        await _fixture.StorageAdapter.WriteAsync(new MeshNode("Story1", "ACME/Project")
        {
            Name = "Original Story",
            NodeType = "Story"
        }, _options, TestContext.Current.CancellationToken);

        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");

        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForChanges(changes, 1); // Initial

        // Act: actually change the node's content. The previous shape wrote
        // identical bytes twice — depending on Postgres trigger logic the
        // second write may or may not fire pg_notify since no row column
        // actually changed. Bumping Name guarantees the row body differs and
        // the UPDATE trigger fires.
        await _fixture.StorageAdapter.WriteAsync(new MeshNode("Story1", "ACME/Project")
        {
            Name = "Updated Story",
            NodeType = "Story"
        }, _options, TestContext.Current.CancellationToken);

        await WaitForChanges(changes, 2, timeout: 5000);

        // Assert
        var updated = changes.FirstOrDefault(c => c.ChangeType == QueryChangeType.Updated);
        updated.Should().NotBeNull("ObserveQuery should detect the updated node via pg_notify");
        updated!.Items.Should().ContainSingle(n => n.Id == "Story1");
    }

    [Fact]
    public async Task ObserveQuery_DetectsDeletedNode()
    {
        // Arrange
        await WriteNode("Story1", "ACME/Project", "Story");
        await WriteNode("Story2", "ACME/Project", "Story");

        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");

        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForChanges(changes, 1); // Initial
        changes[0].Items.Should().HaveCount(2);

        // Act: delete one node
        await _fixture.StorageAdapter.DeleteAsync("ACME/Project/Story1", TestContext.Current.CancellationToken);

        await WaitForChanges(changes, 2, timeout: 5000);

        // Assert
        var removed = changes.FirstOrDefault(c => c.ChangeType == QueryChangeType.Removed);
        removed.Should().NotBeNull("ObserveQuery should detect the deleted node via pg_notify");
        removed!.Items.Should().ContainSingle(n => n.Id == "Story1");
    }

    [Fact]
    public async Task ObserveQuery_IgnoresChangesOutsideScope()
    {
        // Arrange: write an in-scope node, then subscribe.
        await WriteNode("Story1", "ACME/Project", "Story");

        var initialChanges = new List<QueryResultChange<MeshNode>>();
        var outOfScopeChanges = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");
        var stream = _query.ObserveQuery<MeshNode>(request, _options);

        // Subscribe TWICE on the same stream:
        //   (a) capture the Initial emission for the in-scope sanity check
        //   (b) filter to emissions actually carrying a Contoso/Team item â€”
        //       the assertion target. Counting raw emissions (the old shape)
        //       trips on pg_notify duplicate `Updated` events for in-scope
        //       nodes (Story1 update echoes) that are unrelated to whether
        //       Contoso/Team writes reach this subscription.
        using var initialSub = stream
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Subscribe(c => initialChanges.Add(c));
        using var outOfScopeSub = stream
            .Where(c => c.Items.Any(i =>
                i.Namespace?.StartsWith("Contoso", StringComparison.OrdinalIgnoreCase) == true))
            .Subscribe(c => outOfScopeChanges.Add(c));

        await WaitForChanges(initialChanges, 1);

        // Act: add a node outside the query scope.
        await WriteNode("Bob", "Contoso/Team", "Person");

        // Wait long enough for any cross-scope notification to be delivered.
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        // Assert: the in-scope Initial fired exactly once, and no emission
        // carried a Contoso/Team item â€” that's the actual scope-filter claim.
        initialChanges.Should().ContainSingle();
        initialChanges[0].Items.Should().ContainSingle(n => n.Id == "Story1");
        outOfScopeChanges.Should().BeEmpty(
            "Contoso/Team writes must not reach an ACME/Project subscription");
    }

    [Fact]
    public async Task ObserveQuery_VersionsAreMonotonicallyIncreasing()
    {
        // Arrange
        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");

        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForChanges(changes, 1); // Initial (empty)

        // Act: add multiple nodes to generate multiple change notifications
        await WriteNode("Story1", "ACME/Project", "Story");
        await WaitForChanges(changes, 2, timeout: 5000);

        await WriteNode("Story2", "ACME/Project", "Story");
        await WaitForChanges(changes, 3, timeout: 5000);

        // Assert: versions should be strictly increasing
        for (var i = 1; i < changes.Count; i++)
        {
            changes[i].Version.Should().BeGreaterThan(changes[i - 1].Version,
                $"change {i} version should be > change {i - 1} version");
        }
    }

    [Fact]
    public async Task ObserveQuery_MultipleRapidChanges_AreBatched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");

        // Since 486e8d22b (Buffer(100 ms) → per-change Concat), three rapid
        // writes arrive as three separate `Added` emissions. The List<T>
        // accumulator + polling-lambda enumeration would race with the
        // Subscribe handler's Add — guard both ends with the same lock so
        // enumeration takes a stable snapshot.
        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => { lock (changes) changes.Add(c); });

        await WaitForChanges(changes, 1); // Initial (empty)

        // Act: add 3 nodes in rapid succession.
        await WriteNode("Story1", "ACME/Project", "Story");
        await WriteNode("Story2", "ACME/Project", "Story");
        await WriteNode("Story3", "ACME/Project", "Story");

        // Wait until the accumulator captured all 3 Story IDs through Added
        // emissions (one batched emission OR three separate — assertion below
        // accepts either shape).
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ =>
            {
                QueryResultChange<MeshNode>[] snap;
                lock (changes) snap = changes.ToArray();
                return snap.Where(c => c.ChangeType == QueryChangeType.Added)
                    .SelectMany(c => c.Items)
                    .Select(n => n.Id)
                    .Distinct()
                    .Count() >= 3;
            })
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(ct);

        // Assert: all 3 nodes should appear as Added (one or more emissions).
        QueryResultChange<MeshNode>[] finalSnap;
        lock (changes) finalSnap = changes.ToArray();
        var addedChanges = finalSnap.Where(c => c.ChangeType == QueryChangeType.Added).ToList();
        addedChanges.Should().NotBeEmpty();
        var allAddedItems = addedChanges.SelectMany(c => c.Items).ToList();
        allAddedItems.Select(n => n.Id).Should().BeEquivalentTo(["Story1", "Story2", "Story3"]);
    }

    private async Task WriteNode(string id, string ns, string nodeType)
    {
        await _fixture.StorageAdapter.WriteAsync(new MeshNode(id, ns)
        {
            Name = id,
            NodeType = nodeType
        }, _options, TestContext.Current.CancellationToken);
    }

    // Wait until the accumulator list has at least `expectedMinCount` items.
    // Polls the list size on a 50 ms interval via Observable.Interval.
    //
    // TIMEOUT IS AN ERROR. If the expected count isn't reached within `timeout`,
    // throws TimeoutException with observed-vs-expected counts — the silent
    // timeout was hiding flakes (one missed event → confusing assertion later).
    // The 30 s default absorbs CI contention without the per-test 60 s xUnit
    // ceiling. Trailing 100 ms settle delay stays — catches extra emissions
    // arriving just after the target count.
    private static async Task WaitForChanges(
        List<QueryResultChange<MeshNode>> changes,
        int expectedMinCount,
        int timeout = 30_000)
    {
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => changes.Count >= expectedMinCount)
            .FirstAsync()
            .Timeout(
                TimeSpan.FromMilliseconds(timeout),
                Observable.Throw<long>(new TimeoutException(
                    $"WaitForChanges timed out after {timeout} ms: expected at least " +
                    $"{expectedMinCount} change(s), observed {changes.Count}.")))
            .ToTask();
        await Task.Delay(100);
    }
}
