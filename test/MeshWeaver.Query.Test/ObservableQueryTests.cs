using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reactive.Linq;
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Query.Test;

public class ObservableQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Shared mesh: each [Fact] uses a per-method partition prefix derived from the
    // caller's name, so node creates/deletes never collide across tests.
    protected override bool ShareMeshAcrossTests => true;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private IMeshService Query => MeshQuery;

    /// <summary>Returns the calling test method's name — used as a partition prefix.</summary>
    private static string P([CallerMemberName] string name = "") => name;

    /// <summary>
    /// Subscribes to the query and accumulates emissions into an immutable list.
    /// Returns an observable of the running accumulator (one emission per source emission).
    /// Tests then assert via <c>.Should(WaitTimeout).Match(predicate)</c> to wait on the
    /// actual condition rather than a fixed <c>Task.Delay</c>.
    /// </summary>
    private IObservable<ImmutableList<QueryResultChange<MeshNode>>> ObserveAccumulated(string queryText)
        => Query
            .Query<MeshNode>(MeshQueryRequest.FromQuery(queryText))
            .Scan(ImmutableList<QueryResultChange<MeshNode>>.Empty, (acc, c) => acc.Add(c));

    [Fact]
    public void ObserveQuery_EmitsInitialResults()
    {
        // 🟢 Role-model reactive test: NO `await`, no `Task`. The creates are driven by the
        // assertion's subscribe, and we wait for the stream's matching emission via
        // `.Should().Match(...)` — see Doc/Architecture/ReactiveTestAssertions.md.
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();

        // Act — wait for the initial emission to carry both items.
        var changes = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants")
            .Should(WaitTimeout)
            .Match(acc => acc.Count >= 1 && acc[0].Items.Count >= 2);

        // Assert
        changes.Should().HaveCount(1);
        changes[0].ChangeType.Should().Be(QueryChangeType.Initial);
        changes[0].Items.Should().HaveCount(2);
        changes[0].Items.Select(n => n.Name).Should().Contain(["Project 1", "Project 2"]);
    }

    [Fact]
    public void ObserveQuery_EmitsAddedOnNewNode()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();

        var accumulated = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        // Act - Add a new matching node.
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();

        // Wait for the Added emission.
        var changes = accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);

        // Assert
        changes.Should().HaveCount(2);
        changes[1].ChangeType.Should().Be(QueryChangeType.Added);
        changes[1].Items.Should().HaveCount(1);
        changes[1].Items[0].Name.Should().Be("Project 2");
    }

    [Fact]
    public void ObserveQuery_EmitsRemovedOnDeletedNode()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();

        var accumulated = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission with both items.
        accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1 && acc[0].Items.Count >= 2);

        // Act - Delete a node
        NodeFactory.DeleteNode($"{p}/Project1").Should().Emit();

        // Wait for the Removed emission.
        var changes = accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);

        // Assert
        changes.Should().HaveCount(2);
        changes[1].ChangeType.Should().Be(QueryChangeType.Removed);
        changes[1].Items.Should().HaveCount(1);
        changes[1].Items[0].Name.Should().Be("Project 1");
    }

    [Fact]
    public void ObserveQuery_IgnoresChangesOutsideScope()
    {
        var p = P();
        var other = $"Other_{p}";
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();

        var accumulated = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        // Act - Add a node outside the scope (different path).
        NodeFactory.CreateNode(MeshNode.FromPath($"{other}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();

        // Trigger an in-scope change after the out-of-scope create so we have a positive
        // signal that the system processed the second event without a fixed sleep.
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project3") with { Name = "Project 3", NodeType = "Markdown" }).Should().Emit();

        // Wait until the in-scope emission shows up; the out-of-scope create must
        // not have produced its own emission.
        var changes = accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2 && acc.Skip(1).Any(c =>
            c.ChangeType == QueryChangeType.Added &&
            c.Items.Any(i => i.Name == "Project 3")));

        // Assert - only Initial + the in-scope Added; nothing for the out-of-scope create.
        changes[0].ChangeType.Should().Be(QueryChangeType.Initial);
        changes.Skip(1).SelectMany(c => c.Items).Select(i => i.Name)
            .Should().NotContain("Project 2", "out-of-scope creates must not emit");
    }

    [Fact]
    public void ObserveQuery_IgnoresChangesNotMatchingFilter()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();

        var accumulated = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        // Act - Add a node within scope but not matching filter (different nodeType).
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Task1") with { Name = "Task 1", NodeType = "Code" }).Should().Emit();

        // Trigger a matching change to get a positive signal.
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();

        var changes = accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2 && acc.Skip(1).Any(c =>
            c.ChangeType == QueryChangeType.Added &&
            c.Items.Any(i => i.Name == "Project 2")));

        // Assert - the non-matching Task1 must not have produced any emission.
        changes[0].ChangeType.Should().Be(QueryChangeType.Initial);
        changes.Skip(1).SelectMany(c => c.Items).Select(i => i.Name)
            .Should().NotContain("Task 1", "filter-mismatched creates must not emit");
    }

    [Fact]
    public void ObserveQuery_BatchesRapidChanges()
    {
        var p = P();
        var accumulated = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial empty emission.
        accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        // Act - Add multiple nodes rapidly (within debounce window).
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project3") with { Name = "Project 3", NodeType = "Markdown" }).Should().Emit();

        // Wait until the post-initial Added emissions cover all three items.
        var changes = accumulated.Should(WaitTimeout).Match(acc =>
        {
            if (acc.Count < 2) return false;
            var added = acc.Skip(1)
                .Where(c => c.ChangeType == QueryChangeType.Added)
                .SelectMany(c => c.Items)
                .Select(n => n.Name)
                .Distinct()
                .Count();
            return added >= 3;
        });

        // Assert - first emission is Initial; total Added items equal 3.
        changes.Should().HaveCountGreaterThanOrEqualTo(2);
        changes[0].ChangeType.Should().Be(QueryChangeType.Initial);

        var addedItemTotal = changes
            .Skip(1)
            .Where(c => c.ChangeType == QueryChangeType.Added)
            .Sum(c => c.Items.Count);
        addedItemTotal.Should().Be(3);
    }

    [Fact]
    public void ObserveQuery_VersionIncrementsWithEachChange()
    {
        var p = P();
        var accumulated = ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        // Act - Add nodes one at a time, waiting for each to flow through.
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();
        accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);

        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();
        var changes = accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 3);

        // Assert - Versions should be incrementing.
        changes.Should().HaveCountGreaterThanOrEqualTo(2);
        for (int i = 1; i < changes.Count; i++)
        {
            changes[i].Version.Should().BeGreaterThan(changes[i - 1].Version);
        }
    }

    [Fact]
    public void ObserveQuery_DisposalStopsNotifications()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();

        // Thread-safe: the OnNext callback fires on the change-feed thread while the test thread
        // reads the count. A plain List races here (the original flake).
        var receivedChanges = new System.Collections.Concurrent.ConcurrentQueue<QueryResultChange<MeshNode>>();

        var subscription = Query
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{p} nodeType:Markdown scope:descendants"))
            .Subscribe(receivedChanges.Enqueue);

        // Wait until THIS subscription (not a separate one) has recorded its initial emission, so
        // the pre-dispose baseline is deterministic. The original waited on a *different*
        // subscription's initial, so the test subscription's own initial could still be in flight
        // when Dispose ran — and land afterwards, growing the count and failing the assert.
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => receivedChanges.Count)
            .Should(WaitTimeout)
            .Match(c => c >= 1);

        // Act - Dispose subscription, then snapshot the deterministic baseline.
        subscription.Dispose();
        var countAtDisposal = receivedChanges.Count;

        // Add a node after disposal and wait — via a FRESH subscription that DOES emit — until the
        // system has processed the Added (its Initial snapshot shows both nodes). If the disposed
        // subscription were still live, the change feed would have grown its queue by now.
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();
        ObserveAccumulated($"path:{p} nodeType:Markdown scope:descendants")
            .Should(WaitTimeout)
            .Match(acc => acc.Count >= 1 && acc[0].Items.Count >= 2);

        // Assert - the disposed subscription should not have received the Added change.
        receivedChanges.Count.Should().Be(countAtDisposal, "disposed subscription must not emit further");
    }

    [Fact]
    public void ObserveQuery_ScopeExact_OnlyNotifiesOnExactPath()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath(p) with { Name = "TestOrg", NodeType = "Group" }).Should().Emit();

        var accumulated = ObserveAccumulated($"path:{p}").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        // Act - Modify the exact path; expect Updated.
        NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "TestOrg Updated", NodeType = "Group" }).Should().Emit();
        var afterUpdate = accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);
        afterUpdate[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Add a child (should NOT trigger). Then update self again to get a positive signal.
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project") with { Name = "Project", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.UpdateNode(MeshNode.FromPath(p) with { Name = "TestOrg Updated 2", NodeType = "Group" }).Should().Emit();

        var afterChild = accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 3);

        // The third emission should be the second update — never any emission for the child.
        afterChild[2].ChangeType.Should().Be(QueryChangeType.Updated);
        afterChild.Should().HaveCount(3, "child create must not produce an emission for path:exact");
    }

    [Fact]
    public void ObserveQuery_ScopeChildren_OnlyNotifiesOnDirectChildren()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();

        var accumulated = ObserveAccumulated($"namespace:{p}").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 1);

        // Act - Add a direct child; expect a second emission.
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project2") with { Name = "Project 2", NodeType = "Markdown" }).Should().Emit();
        accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 2);

        // Act - Add a grandchild (should NOT trigger). Add another direct child as positive signal.
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project1/Task") with { Name = "Task", NodeType = "Code" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Project3") with { Name = "Project 3", NodeType = "Markdown" }).Should().Emit();

        var changes = accumulated.Should(WaitTimeout).Match(acc => acc.Count >= 3);

        // Assert - third emission is for the direct child Project3, not the grandchild.
        var addedNames = changes.Skip(1)
            .Where(c => c.ChangeType == QueryChangeType.Added)
            .SelectMany(c => c.Items.Select(i => i.Name))
            .ToList();
        addedNames.Should().NotContain("Task", "grandchild must not emit for namespace: query");
        addedNames.Should().Contain("Project 3");
    }
}
