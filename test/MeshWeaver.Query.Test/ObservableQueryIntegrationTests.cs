using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
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
    // Shared mesh: each [Fact] uses a per-method partition prefix derived from the
    // caller's name, so node creates/deletes never collide across tests.
    protected override bool ShareMeshAcrossTests => true;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    private IMeshService Query => MeshQuery;

    /// <summary>Returns the calling test method's name — used as a partition prefix.</summary>
    private static string P([CallerMemberName] string name = "") => name;

    private IObservable<ImmutableList<QueryResultChange<MeshNode>>> ObserveAccumulated(string queryText)
        => Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(queryText))
            .Scan(ImmutableList<QueryResultChange<MeshNode>>.Empty, (acc, c) => acc.Add(c));

    [Fact]
    public async Task MultipleConcurrentSubscriptions_EachReceivesCorrectChanges()
    {
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Task1") with { Name = "Task 1", NodeType = "Code" });

        var ct = TestContext.Current.CancellationToken;

        var projects = new System.Reactive.Subjects.BehaviorSubject<ImmutableList<QueryResultChange<MeshNode>>>(
            ImmutableList<QueryResultChange<MeshNode>>.Empty);
        var tasks = new System.Reactive.Subjects.BehaviorSubject<ImmutableList<QueryResultChange<MeshNode>>>(
            ImmutableList<QueryResultChange<MeshNode>>.Empty);

        using var projectSub = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants").Subscribe(projects);
        using var taskSub = ObserveAccumulated($"path:{p} nodeType:Code scope:descendants").Subscribe(tasks);

        await projects.FirstAsync(acc => acc.Count >= 1 && acc[0].Items.Count >= 1)
            .Timeout(WaitTimeout).ToTask(ct);
        await tasks.FirstAsync(acc => acc.Count >= 1 && acc[0].Items.Count >= 1)
            .Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        var projectChanges = await projects.FirstAsync(acc => acc.Count >= 2)
            .Timeout(WaitTimeout).ToTask(ct);
        projectChanges[1].ChangeType.Should().Be(QueryChangeType.Added);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Task2") with { Name = "Task 2", NodeType = "Code" });
        var taskChanges = await tasks.FirstAsync(acc => acc.Count >= 2)
            .Timeout(WaitTimeout).ToTask(ct);
        taskChanges[1].ChangeType.Should().Be(QueryChangeType.Added);

        projects.Value.Should().HaveCount(2, "project subscription must not see task changes");
        tasks.Value.Should().HaveCount(2, "task subscription must not see project changes");
    }

    [Fact]
    public async Task ScopeExact_OnlyNotifiesOnExactPathChanges()
    {
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath(p) with { Name = "ExactOrg", NodeType = "Group" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated($"path:{p}").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "ExactOrg Updated", NodeType = "Group" });
        var afterUpdate = await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);
        afterUpdate[1].ChangeType.Should().Be(QueryChangeType.Updated);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "ExactOrg Updated 2", NodeType = "Group" });

        var changes = await accumulated.Where(acc => acc.Count >= 3)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        changes[2].ChangeType.Should().Be(QueryChangeType.Updated);
        changes.Should().HaveCount(3, "child create must not produce an emission for exact-path query");
    }

    [Fact]
    public async Task ScopeChildren_OnlyNotifiesOnDirectChildChanges()
    {
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated($"namespace:{p}").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        var afterChild = await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);
        afterChild[1].ChangeType.Should().Be(QueryChangeType.Added);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1/Task") with { Name = "Task", NodeType = "Code" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project3") with { Name = "Project 3", NodeType = "Markdown" });

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
        var p = P();
        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated($"path:{p} scope:descendants").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);
        var initial = await accumulated.FirstAsync().Timeout(WaitTimeout).ToTask(ct);
        var initialCount = initial.Count;
        initialCount.Should().BeGreaterThanOrEqualTo(1, "Should have at least one initial emission");

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/Task") with { Name = "Task", NodeType = "Code" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/Task/Subtask") with { Name = "Subtask", NodeType = "Code" });

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
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath(p) with { Name = "AncOrg", NodeType = "Group" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/Task") with { Name = "Task", NodeType = "Code" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated($"path:{p}/Project/Task scope:ancestors").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "AncOrg Updated", NodeType = "Group" });
        var afterAncestorUpdate = await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);
        afterAncestorUpdate[1].ChangeType.Should().Be(QueryChangeType.Updated);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/Task2") with { Name = "Task 2", NodeType = "Code" });
        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "AncOrg Updated 2", NodeType = "Group" });

        var changes = await accumulated.Where(acc => acc.Count >= 3)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        changes.Should().HaveCount(3, "sibling create must not emit for ancestors scope");
        changes[2].ChangeType.Should().Be(QueryChangeType.Updated);
    }

    [Fact]
    public async Task ScopeSubtree_NotifiesOnSelfAndDescendantChanges()
    {
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath(p) with { Name = "SubOrg", NodeType = "Group" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated($"path:{p} scope:subtree").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "SubOrg Updated", NodeType = "Group" });
        await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" });
        var changes = await accumulated.Where(acc => acc.Count >= 3)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        changes.Should().HaveCount(3);
    }

    [Fact]
    public async Task ScopeHierarchy_NotifiesOnAncestorsSelfAndDescendantChanges()
    {
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath(p) with { Name = "HRoot", NodeType = "Group" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/HCo") with { Name = "HCo", NodeType = "Code" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/HCo/Project") with { Name = "Project", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated($"path:{p}/HCo scope:hierarchy").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "HRoot Updated", NodeType = "Group" });
        await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.UpdateNode(MeshNode.FromPath($"{p}/HCo") with { Name = "HCo Updated", NodeType = "Code" });
        await accumulated.Where(acc => acc.Count >= 3)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.UpdateNode(MeshNode.FromPath($"{p}/HCo/Project") with { Name = "Project Updated", NodeType = "Markdown" });
        await accumulated.Where(acc => acc.Count >= 4)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/HCo/Project/Task") with { Name = "Task", NodeType = "Code" });
        var changes = await accumulated.Where(acc => acc.Count >= 5)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        changes.Should().HaveCount(5);
    }

    [Fact]
    public async Task RecursiveDelete_EmitsRemovedForAllDeletedNodes()
    {
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath(p) with { Name = "DelOrg", NodeType = "Group" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/Task") with { Name = "Task", NodeType = "Code" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated($"path:{p} scope:subtree").Replay();
        using var connection = accumulated.Connect();

        await accumulated
            .Where(acc => acc.SelectMany(c => c.Items).Select(n => n.Path).Distinct().Count() >= 2)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        await NodeFactory.DeleteNode(p);

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
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Where(acc => acc.Count >= 1)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await accumulated.Where(acc => acc.Count >= 2)
            .FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Task1") with { Name = "Task 1", NodeType = "Code" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project3") with { Name = "Project 3", NodeType = "Markdown" });

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
