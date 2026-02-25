using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that ObserveQuery correctly receives change notifications
/// through the full PostgreSQL LISTEN/NOTIFY pipeline:
/// DB trigger → pg_notify → PostgreSqlChangeListener → DataChangeNotifier → ObserveQuery
/// </summary>
[Collection("PostgreSql")]
public class ObserveQueryTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();
    private DataChangeNotifier _notifier = null!;
    private PostgreSqlChangeListener _listener = null!;
    private PostgreSqlMeshQuery _query = null!;

    public ObserveQueryTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.CleanDataAsync();
        // Grant Public Read access so observe tests work without explicit userId
        await _fixture.AccessControl.GrantAsync("ACME", "Public", "Read", isAllow: true);
        _notifier = new DataChangeNotifier();
        _listener = new PostgreSqlChangeListener(_fixture.DataSource, _notifier);
        await _listener.StartAsync();
        // Give the LISTEN connection a moment to establish
        await Task.Delay(200);
        _query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, _notifier);
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
        _notifier.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_EmitsInitialResults()
    {
        // Arrange: seed data before subscribing
        await WriteNode("Story1", "Demos/ACME/Project", "Story");

        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("path:Demos/ACME/Project scope:children");

        // Act
        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForChanges(changes, 1);

        // Assert
        changes.Should().HaveCount(1);
        changes[0].ChangeType.Should().Be(QueryChangeType.Initial);
        changes[0].Items.Should().HaveCount(1);
        changes[0].Items[0].Id.Should().Be("Story1");
    }

    [Fact]
    public async Task ObserveQuery_DetectsAddedNode()
    {
        // Arrange: seed initial data, subscribe, wait for initial emission
        await WriteNode("Story1", "Demos/ACME/Project", "Story");

        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("path:Demos/ACME/Project scope:children");

        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForChanges(changes, 1); // Initial
        changes.Should().ContainSingle(c => c.ChangeType == QueryChangeType.Initial);

        // Act: add a new node that matches the query
        await WriteNode("Story2", "Demos/ACME/Project", "Story");

        await WaitForChanges(changes, 2, timeout: 5000);

        // Assert
        var added = changes.FirstOrDefault(c => c.ChangeType == QueryChangeType.Added);
        added.Should().NotBeNull("ObserveQuery should detect the added node via pg_notify");
        added!.Items.Should().ContainSingle(n => n.Id == "Story2");
    }

    [Fact]
    public async Task ObserveQuery_DetectsUpdatedNode()
    {
        // Arrange
        await WriteNode("Story1", "Demos/ACME/Project", "Story");

        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("path:Demos/ACME/Project scope:children");

        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForChanges(changes, 1); // Initial

        // Act: update the node
        await WriteNode("Story1", "Demos/ACME/Project", "Story");

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
        await WriteNode("Story1", "Demos/ACME/Project", "Story");
        await WriteNode("Story2", "Demos/ACME/Project", "Story");

        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("path:Demos/ACME/Project scope:children");

        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForChanges(changes, 1); // Initial
        changes[0].Items.Should().HaveCount(2);

        // Act: delete one node
        await _fixture.StorageAdapter.DeleteAsync("Demos/ACME/Project/Story1");

        await WaitForChanges(changes, 2, timeout: 5000);

        // Assert
        var removed = changes.FirstOrDefault(c => c.ChangeType == QueryChangeType.Removed);
        removed.Should().NotBeNull("ObserveQuery should detect the deleted node via pg_notify");
        removed!.Items.Should().ContainSingle(n => n.Id == "Story1");
    }

    [Fact]
    public async Task ObserveQuery_IgnoresChangesOutsideScope()
    {
        // Arrange
        await WriteNode("Story1", "Demos/ACME/Project", "Story");

        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("path:Demos/ACME/Project scope:children");

        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForChanges(changes, 1); // Initial

        // Act: add a node outside the query scope
        await WriteNode("Bob", "Contoso/Team", "Person");

        // Wait a bit and verify no additional changes were emitted
        await Task.Delay(1000);

        // Assert: should still only have the initial emission
        changes.Should().HaveCount(1);
        changes[0].ChangeType.Should().Be(QueryChangeType.Initial);
    }

    [Fact]
    public async Task ObserveQuery_VersionsAreMonotonicallyIncreasing()
    {
        // Arrange
        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("path:Demos/ACME/Project scope:children");

        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForChanges(changes, 1); // Initial (empty)

        // Act: add multiple nodes to generate multiple change notifications
        await WriteNode("Story1", "Demos/ACME/Project", "Story");
        await WaitForChanges(changes, 2, timeout: 5000);

        await WriteNode("Story2", "Demos/ACME/Project", "Story");
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
        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery("path:Demos/ACME/Project scope:children");

        using var sub = _query.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForChanges(changes, 1); // Initial (empty)

        // Act: add 3 nodes in rapid succession (within the 100ms debounce window)
        await WriteNode("Story1", "Demos/ACME/Project", "Story");
        await WriteNode("Story2", "Demos/ACME/Project", "Story");
        await WriteNode("Story3", "Demos/ACME/Project", "Story");

        // Wait for debounce + processing
        await Task.Delay(2000);

        // Assert: all 3 nodes should appear as Added (possibly batched into one emission)
        var addedChanges = changes.Where(c => c.ChangeType == QueryChangeType.Added).ToList();
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
        }, _options);
    }

    private static async Task WaitForChanges(
        List<QueryResultChange<MeshNode>> changes,
        int expectedMinCount,
        int timeout = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (changes.Count < expectedMinCount && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        // Small extra delay for processing to settle
        await Task.Delay(100);
    }
}
