using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Query.Test;

public class ObservableQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private IMeshService Query => MeshQuery;

    /// <summary>
    /// Subscribes to the query and accumulates emissions into an immutable list.
    /// Returns an observable of the running accumulator (one emission per source emission).
    /// Tests can then <c>.Where(predicate).FirstAsync().Timeout(...).ToTask(ct)</c> to
    /// wait on the actual condition rather than a fixed <c>Task.Delay</c>.
    /// </summary>
    private IObservable<ImmutableList<QueryResultChange<MeshNode>>> ObserveAccumulated(string queryText)
        => Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(queryText))
            .Scan(ImmutableList<QueryResultChange<MeshNode>>.Empty, (acc, c) => acc.Add(c));

    [Fact]
    public async Task ObserveQuery_EmitsInitialResults()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;

        // Act — wait for the initial emission to carry both items.
        var changes = await ObserveAccumulated("path:ACME nodeType:Markdown scope:descendants")
            .Where(acc => acc.Count >= 1 && acc[0].Items.Count >= 2)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        // Assert
        changes.Should().HaveCount(1);
        changes[0].ChangeType.Should().Be(QueryChangeType.Initial);
        changes[0].Items.Should().HaveCount(2);
        changes[0].Items.Select(n => n.Name).Should().Contain(["Project 1", "Project 2"]);
    }

    [Fact]
    public async Task ObserveQuery_EmitsAddedOnNewNode()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:ACME nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        await accumulated.Where(acc => acc.Count >= 1).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Add a new matching node.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });

        // Wait for the Added emission.
        var changes = await accumulated
            .Where(acc => acc.Count >= 2)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        // Assert
        changes.Should().HaveCount(2);
        changes[1].ChangeType.Should().Be(QueryChangeType.Added);
        changes[1].Items.Should().HaveCount(1);
        changes[1].Items[0].Name.Should().Be("Project 2");
    }

    [Fact]
    public async Task ObserveQuery_EmitsRemovedOnDeletedNode()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:ACME nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission with both items.
        await accumulated
            .Where(acc => acc.Count >= 1 && acc[0].Items.Count >= 2)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        // Act - Delete a node
        await NodeFactory.DeleteNode("ACME/Project1");

        // Wait for the Removed emission.
        var changes = await accumulated
            .Where(acc => acc.Count >= 2)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        // Assert
        changes.Should().HaveCount(2);
        changes[1].ChangeType.Should().Be(QueryChangeType.Removed);
        changes[1].Items.Should().HaveCount(1);
        changes[1].Items[0].Name.Should().Be("Project 1");
    }

    [Fact]
    public async Task ObserveQuery_IgnoresChangesOutsideScope()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:ACME nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        await accumulated.Where(acc => acc.Count >= 1).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Add a node outside the scope (different path).
        await NodeFactory.CreateNode(MeshNode.FromPath("Other/Project2") with { Name = "Project 2", NodeType = "Markdown" });

        // Trigger an in-scope change after the out-of-scope create so we have a positive
        // signal that the system processed the second event without a fixed sleep.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project3") with { Name = "Project 3", NodeType = "Markdown" });

        // Wait until the in-scope emission shows up; the out-of-scope create must
        // not have produced its own emission.
        var changes = await accumulated
            .Where(acc => acc.Count >= 2 && acc.Skip(1).Any(c =>
                c.ChangeType == QueryChangeType.Added &&
                c.Items.Any(i => i.Name == "Project 3")))
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        // Assert - only Initial + the in-scope Added; nothing for the out-of-scope create.
        changes[0].ChangeType.Should().Be(QueryChangeType.Initial);
        changes.Skip(1).SelectMany(c => c.Items).Select(i => i.Name)
            .Should().NotContain("Project 2", "out-of-scope creates must not emit");
    }

    [Fact]
    public async Task ObserveQuery_IgnoresChangesNotMatchingFilter()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:ACME nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        await accumulated.Where(acc => acc.Count >= 1).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Add a node within scope but not matching filter (different nodeType).
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Task1") with { Name = "Task 1", NodeType = "Code" });

        // Trigger a matching change to get a positive signal.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });

        var changes = await accumulated
            .Where(acc => acc.Count >= 2 && acc.Skip(1).Any(c =>
                c.ChangeType == QueryChangeType.Added &&
                c.Items.Any(i => i.Name == "Project 2")))
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        // Assert - the non-matching Task1 must not have produced any emission.
        changes[0].ChangeType.Should().Be(QueryChangeType.Initial);
        changes.Skip(1).SelectMany(c => c.Items).Select(i => i.Name)
            .Should().NotContain("Task 1", "filter-mismatched creates must not emit");
    }

    [Fact]
    public async Task ObserveQuery_BatchesRapidChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:ACME nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial empty emission.
        await accumulated.Where(acc => acc.Count >= 1).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Add multiple nodes rapidly (within debounce window).
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project3") with { Name = "Project 3", NodeType = "Markdown" });

        // Wait until the post-initial Added emissions cover all three items.
        var changes = await accumulated
            .Where(acc =>
            {
                if (acc.Count < 2) return false;
                var added = acc.Skip(1)
                    .Where(c => c.ChangeType == QueryChangeType.Added)
                    .SelectMany(c => c.Items)
                    .Select(n => n.Name)
                    .Distinct()
                    .Count();
                return added >= 3;
            })
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

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
    public async Task ObserveQuery_VersionIncrementsWithEachChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:ACME nodeType:Markdown scope:descendants").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        await accumulated.Where(acc => acc.Count >= 1).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Add nodes one at a time, waiting for each to flow through.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await accumulated.Where(acc => acc.Count >= 2).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        var changes = await accumulated.Where(acc => acc.Count >= 3).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Assert - Versions should be incrementing.
        changes.Should().HaveCountGreaterThanOrEqualTo(2);
        for (int i = 1; i < changes.Count; i++)
        {
            changes[i].Version.Should().BeGreaterThan(changes[i - 1].Version);
        }
    }

    [Fact]
    public async Task ObserveQuery_DisposalStopsNotifications()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission via a separate observation rather than a sleep.
        await ObserveAccumulated("path:ACME nodeType:Markdown scope:descendants")
            .Where(acc => acc.Count >= 1)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        // Act - Dispose subscription.
        subscription.Dispose();

        var snapshot = receivedChanges.ToImmutableList();

        // Add more nodes after disposal and wait via a fresh subscription that DOES emit;
        // if the disposed subscription got the Added change, the snapshot would have grown.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await ObserveAccumulated("path:ACME nodeType:Markdown scope:descendants")
            .Where(acc => acc.Count >= 1 && acc[0].Items.Count >= 2)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        // Assert - the disposed subscription should not have received the Added change.
        receivedChanges.Should().HaveCount(snapshot.Count, "disposed subscription must not emit further");
    }

    [Fact]
    public async Task ObserveQuery_ScopeExact_OnlyNotifiesOnExactPath()
    {
        // Arrange — use a unique path to avoid collision with base-class setup.
        await NodeFactory.CreateNode(MeshNode.FromPath("TestOrg") with { Name = "TestOrg", NodeType = "Group" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("path:TestOrg").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        await accumulated.Where(acc => acc.Count >= 1).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Modify the exact path; expect Updated.
        await NodeFactory.UpdateNode(MeshNode.FromPath("TestOrg") with { Name = "TestOrg Updated", NodeType = "Group" });
        var afterUpdate = await accumulated
            .Where(acc => acc.Count >= 2)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);
        afterUpdate[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Add a child (should NOT trigger). Then update self again to get a positive signal.
        await NodeFactory.CreateNode(MeshNode.FromPath("TestOrg/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.UpdateNode(MeshNode.FromPath("TestOrg") with { Name = "TestOrg Updated 2", NodeType = "Group" });

        var afterChild = await accumulated
            .Where(acc => acc.Count >= 3)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        // The third emission should be the second update — never any emission for the child.
        afterChild[2].ChangeType.Should().Be(QueryChangeType.Updated);
        afterChild.Should().HaveCount(3, "child create must not produce an emission for path:exact");
    }

    [Fact]
    public async Task ObserveQuery_ScopeChildren_OnlyNotifiesOnDirectChildren()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var ct = TestContext.Current.CancellationToken;
        var accumulated = ObserveAccumulated("namespace:ACME").Replay();
        using var connection = accumulated.Connect();

        // Wait for initial emission.
        await accumulated.Where(acc => acc.Count >= 1).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Add a direct child; expect a second emission.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await accumulated.Where(acc => acc.Count >= 2).FirstAsync().Timeout(WaitTimeout).ToTask(ct);

        // Act - Add a grandchild (should NOT trigger). Add another direct child as positive signal.
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project1/Task") with { Name = "Task", NodeType = "Code" });
        await NodeFactory.CreateNode(MeshNode.FromPath("ACME/Project3") with { Name = "Project 3", NodeType = "Markdown" });

        var changes = await accumulated
            .Where(acc => acc.Count >= 3)
            .FirstAsync()
            .Timeout(WaitTimeout)
            .ToTask(ct);

        // Assert - third emission is for the direct child Project3, not the grandchild.
        var addedNames = changes.Skip(1)
            .Where(c => c.ChangeType == QueryChangeType.Added)
            .SelectMany(c => c.Items.Select(i => i.Name))
            .ToList();
        addedNames.Should().NotContain("Task", "grandchild must not emit for namespace: query");
        addedNames.Should().Contain("Project 3");
    }
}
