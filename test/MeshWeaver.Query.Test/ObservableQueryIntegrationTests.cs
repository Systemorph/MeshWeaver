using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
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
            .Query<MeshNode>(MeshQueryRequest.FromQuery(queryText))
            .Scan(ImmutableList<QueryResultChange<MeshNode>>.Empty, (acc, c) => acc.Add(c));

    [Fact]
    public async Task MultipleConcurrentSubscriptions_EachReceivesCorrectChanges()
    {
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Task1") with { Name = "Task 1", NodeType = "Code" }).Should().Emit();

        var projects = new System.Reactive.Subjects.BehaviorSubject<ImmutableList<QueryResultChange<MeshNode>>>(
            ImmutableList<QueryResultChange<MeshNode>>.Empty);
        var tasks = new System.Reactive.Subjects.BehaviorSubject<ImmutableList<QueryResultChange<MeshNode>>>(
            ImmutableList<QueryResultChange<MeshNode>>.Empty);

        using var projectSub = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants").Subscribe(projects);
        using var taskSub = ObserveAccumulated($"path:{p} nodeType:Code scope:descendants").Subscribe(tasks);

        await projects.Should(WaitTimeout).Match(acc => acc.Count >= 1 && acc[0].Items.Count >= 1);
        await tasks.Should(WaitTimeout).Match(acc => acc.Count >= 1 && acc[0].Items.Count >= 1);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();
        var projectChanges = await projects.Should(WaitTimeout).Match(acc => acc.Count >= 2);
        projectChanges[1].ChangeType.Should().Be(QueryChangeType.Added);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Task2") with { Name = "Task 2", NodeType = "Code" }).Should().Emit();
        var taskChanges = await tasks.Should(WaitTimeout).Match(acc => acc.Count >= 2);
        taskChanges[1].ChangeType.Should().Be(QueryChangeType.Added);

        projects.Value.Should().HaveCount(2, "project subscription must not see task changes");
        tasks.Value.Should().HaveCount(2, "task subscription must not see project changes");
    }

    [Fact]
    public async Task ScopeExact_OnlyNotifiesOnExactPathChanges()
    {
        var p = P();
        SeedTopLevel(MeshNode.FromPath(p) with { Name = "ExactOrg", NodeType = "Group" }); // top-level partition root → System

        var accumulated = ObserveAccumulated($"path:{p}").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "ExactOrg Updated", NodeType = "Group" }).Should().Emit();
        var afterUpdate = await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);
        afterUpdate[1].ChangeType.Should().Be(QueryChangeType.Updated);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "ExactOrg Updated 2", NodeType = "Group" }).Should().Emit();

        var changes = await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 3);

        changes[2].ChangeType.Should().Be(QueryChangeType.Updated);
        changes.Should().HaveCount(3, "child create must not produce an emission for exact-path query");
    }

    [Fact]
    public async Task ScopeChildren_OnlyNotifiesOnDirectChildChanges()
    {
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();

        var accumulated = ObserveAccumulated($"namespace:{p}").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();
        var afterChild = await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);
        afterChild[1].ChangeType.Should().Be(QueryChangeType.Added);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1/Task") with { Name = "Task", NodeType = "Code" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project3") with { Name = "Project 3", NodeType = "Markdown" }).Should().Emit();

        var changes = await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 3);

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
        var accumulated = ObserveAccumulated($"path:{p} scope:descendants").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);
        var initial = await accumulated.Should(WaitTimeout).Emit();
        var initialCount = initial.Count;
        initialCount.Should().BeGreaterThanOrEqualTo(1, "Should have at least one initial emission");

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/Task") with { Name = "Task", NodeType = "Code" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/Task/Subtask") with { Name = "Subtask", NodeType = "Code" }).Should().Emit();

        var changes = await accumulated.Should(WaitTimeout).Match(acc => acc.Skip(initialCount)
            .Where(c => c.ChangeType == QueryChangeType.Added)
            .SelectMany(c => c.Items)
            .Select(n => n.Path)
            .Distinct()
            .Count() >= 3);

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
        SeedTopLevel(MeshNode.FromPath(p) with { Name = "AncOrg", NodeType = "Group" }); // top-level partition root → System
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/Task") with { Name = "Task", NodeType = "Code" }).Should().Emit();

        var accumulated = ObserveAccumulated($"path:{p}/Project/Task scope:ancestors").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "AncOrg Updated", NodeType = "Group" }).Should().Emit();
        var afterAncestorUpdate = await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);
        afterAncestorUpdate[1].ChangeType.Should().Be(QueryChangeType.Updated);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/Task2") with { Name = "Task 2", NodeType = "Code" }).Should().Emit();
        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "AncOrg Updated 2", NodeType = "Group" }).Should().Emit();

        var changes = await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 3);

        changes.Should().HaveCount(3, "sibling create must not emit for ancestors scope");
        changes[2].ChangeType.Should().Be(QueryChangeType.Updated);
    }

    [Fact]
    public async Task ScopeSubtree_NotifiesOnSelfAndDescendantChanges()
    {
        var p = P();
        SeedTopLevel(MeshNode.FromPath(p) with { Name = "SubOrg", NodeType = "Group" }); // top-level partition root → System

        var accumulated = ObserveAccumulated($"path:{p} scope:subtree").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "SubOrg Updated", NodeType = "Group" }).Should().Emit();
        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" }).Should().Emit();
        var changes = await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 3);

        changes.Should().HaveCount(3);
    }

    [Fact]
    public async Task ScopeHierarchy_NotifiesOnAncestorsSelfAndDescendantChanges()
    {
        var p = P();
        SeedTopLevel(MeshNode.FromPath(p) with { Name = "HRoot", NodeType = "Group" }); // top-level partition root → System
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/HCo") with { Name = "HCo", NodeType = "Code" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/HCo/Project") with { Name = "Project", NodeType = "Markdown" }).Should().Emit();

        var accumulated = ObserveAccumulated($"path:{p}/HCo scope:hierarchy").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "HRoot Updated", NodeType = "Group" }).Should().Emit();
        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);

        await NodeFactory.UpdateNode(MeshNode.FromPath($"{p}/HCo") with { Name = "HCo Updated", NodeType = "Code" }).Should().Emit();
        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 3);

        await NodeFactory.UpdateNode(MeshNode.FromPath($"{p}/HCo/Project") with { Name = "Project Updated", NodeType = "Markdown" }).Should().Emit();
        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 4);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/HCo/Project/Task") with { Name = "Task", NodeType = "Code" }).Should().Emit();
        var changes = await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 5);

        changes.Should().HaveCount(5);
    }

    [Fact]
    public async Task RecursiveDelete_EmitsRemovedForAllDeletedNodes()
    {
        var p = P();
        SeedTopLevel(MeshNode.FromPath(p) with { Name = "DelOrg", NodeType = "Group" }); // top-level partition root → System
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project/Task") with { Name = "Task", NodeType = "Code" }).Should().Emit();

        var accumulated = ObserveAccumulated($"path:{p} scope:subtree").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Should(WaitTimeout).Match(acc => acc.SelectMany(c => c.Items).Select(n => n.Path).Distinct().Count() >= 2);

        await NodeFactory.DeleteNode(p).Should().Emit();

        var changes = await accumulated.Should(WaitTimeout).Match(acc => acc
            .Where(c => c.ChangeType == QueryChangeType.Removed)
            .SelectMany(c => c.Items)
            .Select(n => n.Path)
            .Distinct()
            .Count() >= 2);

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
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();

        var accumulated = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();
        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Task1") with { Name = "Task 1", NodeType = "Code" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project3") with { Name = "Project 3", NodeType = "Markdown" }).Should().Emit();

        var changes = await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 3);

        var addedNames = changes.Skip(1)
            .Where(c => c.ChangeType == QueryChangeType.Added)
            .SelectMany(c => c.Items.Select(i => i.Name))
            .ToList();
        addedNames.Should().NotContain("Task 1", "filter-mismatched creates must not emit");
        addedNames.Should().Contain("Project 3");
    }
}
