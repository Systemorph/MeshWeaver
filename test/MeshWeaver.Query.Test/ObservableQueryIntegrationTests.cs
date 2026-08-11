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

        // 🚨 SHAPE, not count. `projects.Value.Should().HaveCount(2)` read like an isolation guard but
        // was a race in both directions: it samples a BehaviorSubject at an arbitrary moment, so a leak
        // arriving 1 ms later passes, and one extra legitimate emission (a resubscribe Initial, a
        // framework satellite) reds it. The actual contract is WHAT each subscription saw, not how many
        // times it was told — and that is assertable without racing.
        projects.Value.SelectMany(c => c.Items).Should().OnlyContain(n => n.NodeType == "Markdown",
            "the Markdown query must never see a Code node — that would be a cross-query leak");
        tasks.Value.SelectMany(c => c.Items).Should().OnlyContain(n => n.NodeType == "Code",
            "the Code query must never see a Markdown node — that would be a cross-query leak");
    }

    [Fact]
    public async Task ScopeExact_OnlyNotifiesOnExactPathChanges()
    {
        var p = P();
        await SeedTopLevel(MeshNode.FromPath(p) with { Name = "ExactOrg", NodeType = "Group" }); // top-level partition root → System

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
        // 🚨 SHAPE, not count. `HaveCount(3)` here could NEVER fail: Scan grows the list by exactly one
        // per emission, so the first snapshot satisfying Match(Count >= 3) has Count == 3 by
        // construction. The assertion looked like the child-create guard and enforced nothing — the
        // leak it was written to catch would have sailed through. Assert what actually emitted.
        changes.SelectMany(c => c.Items).Select(n => n.Path).Should().OnlyContain(path => path == p,
            "an exact-path query must emit only for that node — the child create must never appear");
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

        // 🚨 Assert the SHAPE (the three paths we created each produced an Added), never a COUNT.
        // `scope:descendants` legitimately matches anything created under {p} — a satellite the
        // framework writes for the new nodes lands in the same window under load — so an
        // exactly-3 assertion fails whenever one extra descendant shows up, which is what made
        // this the top flake of 2026-08-08 (four unrelated PRs: #927, #940, #942, #947). Counting
        // emissions on a change feed that races the initial snapshot is called out in AGENTS.md
        // precisely because of this failure mode.
        var expected = new[] { $"{p}/Project", $"{p}/Project/Task", $"{p}/Project/Task/Subtask" };
        var changes = await accumulated.Should(WaitTimeout).Match(acc =>
        {
            var added = acc.Skip(initialCount)
                .Where(c => c.ChangeType == QueryChangeType.Added)
                .SelectMany(c => c.Items)
                .Select(n => n.Path)
                .ToHashSet();
            return expected.All(added.Contains);
        });

        var addedPaths = changes.Skip(initialCount)
            .Where(c => c.ChangeType == QueryChangeType.Added)
            .SelectMany(c => c.Items)
            .Select(n => n.Path)
            .ToHashSet();
        addedPaths.Should().Contain(expected,
            "each created descendant must emit an Added change — extra descendants are allowed, "
            + "the scope matches everything under the root");
    }

    [Fact]
    public async Task ScopeAncestors_NotifiesOnAncestorChanges()
    {
        var p = P();
        await SeedTopLevel(MeshNode.FromPath(p) with { Name = "AncOrg", NodeType = "Group" }); // top-level partition root → System
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

        changes[2].ChangeType.Should().Be(QueryChangeType.Updated);
        // 🚨 SHAPE, not count — HaveCount(3) after Match(Count >= 3) is a tautology (see ScopeExact).
        // The sibling-create guard is only real when it names the sibling.
        changes.SelectMany(c => c.Items).Select(n => n.Path).Should().NotContain($"{p}/Project/Task2",
            "a sibling create must not emit for an ancestors-scope query");
    }

    [Fact]
    public async Task ScopeSubtree_NotifiesOnSelfAndDescendantChanges()
    {
        var p = P();
        await SeedTopLevel(MeshNode.FromPath(p) with { Name = "SubOrg", NodeType = "Group" }); // top-level partition root → System

        var accumulated = ObserveAccumulated($"path:{p} scope:subtree").Replay();
        using var connection = accumulated.Connect();

        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        await NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "SubOrg Updated", NodeType = "Group" }).Should().Emit();
        await accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);

        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" }).Should().Emit();

        // 🚨 Wait on the SHAPE, never on a count — and note that this test's only assertion used to be
        // `changes.Should().HaveCount(3)`, which was wrong in BOTH directions at once:
        //   1. it could never fail (Scan adds one per emission, so the first snapshot satisfying
        //      Match(Count >= 3) has Count == 3 by construction), and
        //   2. the Count >= 3 gate it depended on was satisfied by an UNRELATED emission — the
        //      framework's own `{p}/_Access/…` satellite create landed third. So the test declared
        //      success on a satellite and never observed the descendant at all: scope:subtree could
        //      have stopped emitting for descendants entirely and this stayed green.
        // Gating on the emissions we actually care about fixes both — an absent emission now times out
        // (red) instead of being papered over by whatever else happened to arrive.
        var changes = await accumulated.Should(WaitTimeout).Match(acc =>
            acc.Any(c => c.ChangeType == QueryChangeType.Updated && c.Items.Any(n => n.Path == p))
            && acc.Any(c => c.ChangeType == QueryChangeType.Added
                            && c.Items.Any(n => n.Path == $"{p}/Project")));

        changes.Should().Contain(
            c => c.ChangeType == QueryChangeType.Updated && c.Items.Any(n => n.Path == p),
            "scope:subtree must emit when the node ITSELF changes");
        changes.Should().Contain(
            c => c.ChangeType == QueryChangeType.Added && c.Items.Any(n => n.Path == $"{p}/Project"),
            "scope:subtree must emit when a DESCENDANT is created");
    }

    [Fact]
    public async Task ScopeHierarchy_NotifiesOnAncestorsSelfAndDescendantChanges()
    {
        var p = P();
        await SeedTopLevel(MeshNode.FromPath(p) with { Name = "HRoot", NodeType = "Group" }); // top-level partition root → System
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

        // 🚨 Same defect as ScopeSubtree, same fix: `HaveCount(5)` was the ONLY assertion and could
        // never fail, while the `Count >= 5` gate it rode on could be satisfied by unrelated framework
        // emissions (an `_Access` satellite create). Wait on the four paths whose emission IS the
        // contract of scope:hierarchy — ancestor, self, descendant, and a newly-created deep descendant.
        var expected = new[] { p, $"{p}/HCo", $"{p}/HCo/Project", $"{p}/HCo/Project/Task" };
        var changes = await accumulated.Should(WaitTimeout).Match(acc =>
        {
            var emitted = acc.SelectMany(c => c.Items).Select(n => n.Path).ToHashSet();
            return expected.All(emitted.Contains);
        });

        changes.SelectMany(c => c.Items).Select(n => n.Path).ToHashSet()
            .Should().Contain(expected,
                "scope:hierarchy must emit for ancestors, the node itself, AND descendants");
    }

    [Fact]
    public async Task RecursiveDelete_EmitsRemovedForAllDeletedNodes()
    {
        var p = P();
        await SeedTopLevel(MeshNode.FromPath(p) with { Name = "DelOrg", NodeType = "Group" }); // top-level partition root → System
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
