using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Integration tests for observable queries with InMemory persistence.
/// Tests end-to-end scenarios including multiple concurrent subscriptions.
/// </summary>
public class ObservableQueryIntegrationTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    private IMeshService Query => MeshQuery;

    private IObservable<ImmutableList<QueryResultChange<MeshNode>>> ObserveAccumulated(string queryText)
        => Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(queryText))
            .Scan(ImmutableList<QueryResultChange<MeshNode>>.Empty, (acc, c) => acc.Add(c));

    [Fact]
    public async Task MultipleConcurrentSubscriptions_EachReceivesCorrectChanges()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Task1") with { Name = "Task 1", NodeType = "Code" });

        var ct = TestContext.Current.CancellationToken;

        // BehaviorSubject<ImmutableList<...>> holds the latest accumulated snapshot;
        // .FirstAsync(predicate) on the subject returns the latest matching value.
        var projects = new System.Reactive.Subjects.BehaviorSubject<ImmutableList<QueryResultChange<MeshNode>>>(
            ImmutableList<QueryResultChange<MeshNode>>.Empty);
        var tasks = new System.Reactive.Subjects.BehaviorSubject<ImmutableList<QueryResultChange<MeshNode>>>(
            ImmutableList<QueryResultChange<MeshNode>>.Empty);

        using var projectSub = ObserveAccumulated("path:ACME nodeType:Markdown scope:descendants").Subscribe(projects);
        using var taskSub = ObserveAccumulated("path:ACME nodeType:Code scope:descendants").Subscribe(tasks);

        // Wait for both initial emissions.
        await projects.FirstAsync(acc => acc.Count >= 1 && acc[0].Items.Count >= 1)
            .Timeout(WaitTimeout).ToTask(ct);
        await tasks.FirstAsync(acc => acc.Count >= 1 && acc[0].Items.Count >= 1)
            .Timeout(WaitTimeout).ToTask(ct);

        // Act - Add a new project (should only affect project subscription).
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        var projectChanges = await projects.FirstAsync(acc => acc.Count >= 2)
            .Timeout(WaitTimeout).ToTask(ct);
        projectChanges[1].ChangeType.Should().Be(QueryChangeType.Added);

        // Act - Add a new task (should only affect task subscription).
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Task2") with { Name = "Task 2", NodeType = "Code" });
        var taskChanges = await tasks.FirstAsync(acc => acc.Count >= 2)
            .Timeout(WaitTimeout).ToTask(ct);
        taskChanges[1].ChangeType.Should().Be(QueryChangeType.Added);

        // Assert isolation — the BehaviorSubjects hold the latest snapshots.
        projects.Value.Should().HaveCount(2, "project subscription must not see task changes");
        tasks.Value.Should().HaveCount(2, "task subscription must not see project changes");
    }

    [Fact]
    public async Task ScopeExact_OnlyNotifiesOnExactPathChanges()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("ExactOrg") with { Name = "ExactOrg", NodeType = "Group" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:ExactOrg").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Update exact path.
        await NodeFactory.UpdateNode(MeshNode.FromPath("ExactOrg") with { Name = "ExactOrg Updated", NodeType = "Group" });
        var afterUpdate = await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);
        afterUpdate[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Add child (should NOT trigger). Use a follow-up exact-path update as positive signal.
        await NodeFactory.CreateNode(MeshNode.FromPath("ExactOrg/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.UpdateNode(MeshNode.FromPath("ExactOrg") with { Name = "ExactOrg Updated 2", NodeType = "Group" });

        var changes = await accumulated.Where(acc => acc.Count >= 3)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Third emission must be the second update — child create produced nothing.
        changes[2].ChangeType.Should().Be(QueryChangeType.Updated);
        changes.Should().HaveCount(3, "child create must not produce an emission for exact-path query");
    }

    [Fact]
    public async Task ScopeChildren_OnlyNotifiesOnDirectChildChanges()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("namespace:ACME").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Add direct child.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        var afterChild = await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);
        afterChild[1].ChangeType.Should().Be(QueryChangeType.Added);

        // Act - Add grandchild (should NOT trigger). Add a direct child as positive signal.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1/Task") with { Name = "Task", NodeType = "Code" });
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project3") with { Name = "Project 3", NodeType = "Markdown" });

        var changes = await accumulated.Where(acc => acc.Count >= 3)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        var addedNames = changes.Skip(1)
            .Where(c => c.ChangeType == QueryChangeType.Added)
            .SelectMany(c => c.Items.Select(i => i.Name))
            .ToList();
        addedNames.Should().NotContain("Task", "grandchild must not emit for namespace: query");
        addedNames.Should().Contain("Project 3");
    }

    [Fact]
    public async Task ScopeDescendants_NotifiesOnAllDescendantChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:ACME scope:descendants").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);
        var initial = await accumulated.FirstAsync().Timeout(WaitTimeout).ToTask(ct);
        var initialCount = initial.Count;
        initialCount.Should().BeGreaterThanOrEqualTo(1, "Should have at least one initial emission");

        // Add child + grandchild + great-grandchild.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project/Task") with { Name = "Task", NodeType = "Code" });
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project/Task/Subtask") with { Name = "Subtask", NodeType = "Code" });

        // Wait until all 3 distinct descendants appear in Added emissions.
        var changes = await accumulated
            .Where(acc => acc.Skip(initialCount)
                .Where(c => c.ChangeType == QueryChangeType.Added)
                .SelectMany(c => c.Items)
                .Select(n => n.Path)
                .Distinct()
                .Count() >= 3)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        var addedItems = changes.Skip(initialCount)
            .Where(c => c.ChangeType == QueryChangeType.Added)
            .SelectMany(c => c.Items)
            .DistinctBy(n => n.Path)
            .ToList();
        addedItems.Should().HaveCount(3, "Each created descendant should emit an Added change");
    }

    [Fact]
    public async Task ScopeAncestors_NotifiesOnAncestorChanges()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("AncOrg") with { Name = "AncOrg", NodeType = "Group" });
        await NodeFactory.CreateNode(MeshNode.FromPath("AncOrg/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath("AncOrg/Project/Task") with { Name = "Task", NodeType = "Code" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:AncOrg/Project/Task scope:ancestors").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Update an ancestor.
        await NodeFactory.UpdateNode(MeshNode.FromPath("AncOrg") with { Name = "AncOrg Updated", NodeType = "Group" });
        var afterAncestorUpdate = await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);
        afterAncestorUpdate[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Add a sibling of Task (should NOT trigger). Update ancestor again as positive signal.
        await NodeFactory.CreateNode(MeshNode.FromPath("AncOrg/Project/Task2") with { Name = "Task 2", NodeType = "Code" });
        await NodeFactory.UpdateNode(MeshNode.FromPath("AncOrg") with { Name = "AncOrg Updated 2", NodeType = "Group" });

        var changes = await accumulated.Where(acc => acc.Count >= 3)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        changes.Should().HaveCount(3, "sibling create must not emit for ancestors scope");
        changes[2].ChangeType.Should().Be(QueryChangeType.Updated);
    }

    [Fact]
    public async Task ScopeSubtree_NotifiesOnSelfAndDescendantChanges()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("SubOrg") with { Name = "SubOrg", NodeType = "Group" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:SubOrg scope:subtree").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Update self.
        await NodeFactory.UpdateNode(MeshNode.FromPath("SubOrg") with { Name = "SubOrg Updated", NodeType = "Group" });
        await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Add descendant.
        await NodeFactory.CreateNode(MeshNode.FromPath("SubOrg/Project") with { Name = "Project", NodeType = "Markdown" });
        var changes = await accumulated.Where(acc => acc.Count >= 3)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        changes.Should().HaveCount(3);
    }

    [Fact]
    public async Task ScopeHierarchy_NotifiesOnAncestorsSelfAndDescendantChanges()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("HRoot") with { Name = "HRoot", NodeType = "Group" });
        await NodeFactory.CreateNode(MeshNode.FromPath("HRoot/HCo") with { Name = "HCo", NodeType = "Code" });
        await NodeFactory.CreateNode(MeshNode.FromPath("HRoot/HCo/Project") with { Name = "Project", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:HRoot/HCo scope:hierarchy").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.UpdateNode(MeshNode.FromPath("HRoot") with { Name = "HRoot Updated", NodeType = "Group" });
        await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.UpdateNode(MeshNode.FromPath("HRoot/HCo") with { Name = "HCo Updated", NodeType = "Code" });
        await accumulated.Where(acc => acc.Count >= 3)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.UpdateNode(MeshNode.FromPath("HRoot/HCo/Project") with { Name = "Project Updated", NodeType = "Markdown" });
        await accumulated.Where(acc => acc.Count >= 4)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.CreateNode(MeshNode.FromPath("HRoot/HCo/Project/Task") with { Name = "Task", NodeType = "Code" });
        var changes = await accumulated.Where(acc => acc.Count >= 5)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        changes.Should().HaveCount(5);
    }

    [Fact]
    public async Task RecursiveDelete_EmitsRemovedForAllDeletedNodes()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("DelOrg") with { Name = "DelOrg", NodeType = "Group" });
        await NodeFactory.CreateNode(MeshNode.FromPath("DelOrg/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath("DelOrg/Project/Task") with { Name = "Task", NodeType = "Code" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:DelOrg scope:subtree").Replay();
        using var connection = accumulated.Connect();

        // Wait for an initial emission that contains at least the parent + a descendant.
        await accumulated
            .Where(acc => acc.SelectMany(c => c.Items).Select(n => n.Path).Distinct().Count() >= 2)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        // Act - Recursive delete.
        await NodeFactory.DeleteNode("DelOrg");

        // Wait until at least 2 distinct paths show up under Removed emissions.
        var changes = await accumulated
            .Where(acc => acc
                .Where(c => c.ChangeType == QueryChangeType.Removed)
                .SelectMany(c => c.Items)
                .Select(n => n.Path)
                .Distinct()
                .Count() >= 2)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        var allRemovedItems = changes
            .Where(c => c.ChangeType == QueryChangeType.Removed)
            .SelectMany(c => c.Items)
            .DistinctBy(n => n.Path)
            .ToList();

        allRemovedItems.Should().HaveCountGreaterThanOrEqualTo(2, "Should emit Removed for at least parent and child");
    }

    [Fact]
    public async Task QueryWithFilter_OnlyEmitsMatchingChanges()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:ACME nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Add matching node.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Add non-matching node (different nodeType). Trigger another match for a positive signal.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Task1") with { Name = "Task 1", NodeType = "Code" });
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project3") with { Name = "Project 3", NodeType = "Markdown" });

        var changes = await accumulated.Where(acc => acc.Count >= 3)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        var addedNames = changes.Skip(1)
            .Where(c => c.ChangeType == QueryChangeType.Added)
            .SelectMany(c => c.Items.Select(i => i.Name))
            .ToList();
        addedNames.Should().NotContain("Task 1", "filter-mismatched creates must not emit");
        addedNames.Should().Contain("Project 3");
    }
}
