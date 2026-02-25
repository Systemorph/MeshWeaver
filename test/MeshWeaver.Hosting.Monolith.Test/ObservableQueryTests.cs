using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class ObservableQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly DataChangeNotifier _changeNotifier = new();
    private InMemoryPersistenceService? _persistence;
    private IMeshQueryProvider? _meshQuery;
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    private InMemoryPersistenceService Persistence => _persistence ??= new InMemoryPersistenceService(changeNotifier: _changeNotifier);
    private IMeshQueryProvider MeshQuery => _meshQuery ??= new InMemoryMeshQuery(Persistence, changeNotifier: _changeNotifier);

    [Fact]
    public async Task ObserveQuery_EmitsInitialResults()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1") with { Name = "Project 1", NodeType = "Project" }, JsonOptions);
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project2") with { Name = "Project 2", NodeType = "Project" }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        // Act
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Demos/ACME nodeType:Project scope:descendants"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Assert
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().HaveCount(2);
        receivedChanges[0].Items.Select(n => n.Name).Should().Contain(["Project 1", "Project 2"]);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_EmitsAddedOnNewNode()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1") with { Name = "Project 1", NodeType = "Project" }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Demos/ACME nodeType:Project scope:descendants"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Add a new matching node
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project2") with { Name = "Project 2", NodeType = "Project" }, JsonOptions);

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Project 2");

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_EmitsUpdatedOnModifiedNode()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1") with { Name = "Project 1", NodeType = "Project" }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Demos/ACME nodeType:Project scope:descendants"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Update the existing node
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1") with { Name = "Updated Project 1", NodeType = "Project" }, JsonOptions);

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Updated Project 1");

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_EmitsRemovedOnDeletedNode()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1") with { Name = "Project 1", NodeType = "Project" }, JsonOptions);
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project2") with { Name = "Project 2", NodeType = "Project" }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Demos/ACME nodeType:Project scope:descendants"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Delete a node
        await Persistence.DeleteNodeAsync("Demos/ACME/Project1");

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Removed);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Project 1");

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_IgnoresChangesOutsideScope()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1") with { Name = "Project 1", NodeType = "Project" }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Demos/ACME nodeType:Project scope:descendants"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Add a node outside the scope (different path)
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Other/Project2") with { Name = "Project 2", NodeType = "Project" }, JsonOptions);

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert - Should still only have initial emission
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_IgnoresChangesNotMatchingFilter()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1") with { Name = "Project 1", NodeType = "Project" }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Demos/ACME nodeType:Project scope:descendants"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Add a node within scope but not matching filter
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Task1") with { Name = "Task 1", NodeType = "Task" }, JsonOptions);

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert - Should still only have initial emission (the new node doesn't match nodeType:Project)
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_BatchesRapidChanges()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Demos/ACME nodeType:Project scope:descendants"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial empty emission
        await Task.Delay(200);

        // Act - Add multiple nodes rapidly (within debounce window)
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1") with { Name = "Project 1", NodeType = "Project" }, JsonOptions);
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project2") with { Name = "Project 2", NodeType = "Project" }, JsonOptions);
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project3") with { Name = "Project 3", NodeType = "Project" }, JsonOptions);

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert - Changes should be batched into one Added emission
        // Should have: 1 initial (empty) + 1 added (with 3 items)
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items.Should().HaveCount(3);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_VersionIncrementsWithEachChange()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Demos/ACME nodeType:Project scope:descendants"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Add nodes one at a time with delay
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1") with { Name = "Project 1", NodeType = "Project" }, JsonOptions);
        await Task.Delay(300);

        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project2") with { Name = "Project 2", NodeType = "Project" }, JsonOptions);
        await Task.Delay(300);

        // Assert - Versions should be incrementing
        receivedChanges.Should().HaveCountGreaterThanOrEqualTo(2);
        for (int i = 1; i < receivedChanges.Count; i++)
        {
            receivedChanges[i].Version.Should().BeGreaterThan(receivedChanges[i - 1].Version);
        }

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_DisposalStopsNotifications()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1") with { Name = "Project 1", NodeType = "Project" }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Demos/ACME nodeType:Project scope:descendants"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Dispose subscription
        subscription.Dispose();

        // Add more nodes after disposal
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project2") with { Name = "Project 2", NodeType = "Project" }, JsonOptions);
        await Task.Delay(300);

        // Assert - Should only have initial emission (no changes after disposal)
        receivedChanges.Should().HaveCount(1);
    }

    [Fact]
    public async Task ObserveQuery_ScopeExact_OnlyNotifiesOnExactPath()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME") with { Name = "ACME", NodeType = "Organization" }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Demos/ACME scope:exact"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Modify the exact path
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME") with { Name = "ACME Updated", NodeType = "Organization" }, JsonOptions);
        await Task.Delay(300);

        // Should get updated notification
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Add a child (should NOT trigger notification for scope:exact)
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project") with { Name = "Project", NodeType = "Project" }, JsonOptions);
        await Task.Delay(300);

        // Should still only have 2 notifications
        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_ScopeChildren_OnlyNotifiesOnDirectChildren()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1") with { Name = "Project 1", NodeType = "Project" }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Demos/ACME scope:children"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Add a direct child
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project2") with { Name = "Project 2", NodeType = "Project" }, JsonOptions);
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        // Act - Add a grandchild (should NOT trigger notification for scope:children)
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project1/Task") with { Name = "Task", NodeType = "Task" }, JsonOptions);
        await Task.Delay(300);

        // Should still only have 2 notifications
        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }
}
