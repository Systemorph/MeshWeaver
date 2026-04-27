using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Behavioural tests for <see cref="SyncedQueryMeshNodes"/> and the
/// hub-cached <see cref="SyncedQueryRegistry"/> retrieval surface
/// (<see cref="SyncedQueryDataSourceExtensions.GetQuery(IWorkspace, object)"/>
/// and the get-or-create
/// <see cref="SyncedQueryDataSourceExtensions.GetQuery(IWorkspace, object, string)"/>).
///
/// <para>
/// Pipeline contract: from <see cref="IMeshQueryProvider.ObserveQuery"/> →
/// scan into a running path set → <c>DistinctUntilChanged</c> with
/// element equality on the set → resolve per-path remote streams →
/// <c>CombineLatest</c>. <c>Switch</c> when the path set changes —
/// new paths add per-path streams, removed paths drop theirs. Updates to
/// existing paths flow through the per-path stream.
/// </para>
///
/// <para>Tests use the <see cref="MarkdownNodeType"/> namespace under the
/// test partition so each test creates / mutates / deletes only nodes it
/// owns.</para>
/// </summary>
public class SyncedQueryTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string SubjectsNamespace = $"{TestPartition}/SyncedQuerySubjects";

    private static MeshNode MakeSubject(string id, string name)
        => new(id, SubjectsNamespace)
        {
            Name = name,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };

    private IObservable<IEnumerable<MeshNode>> CreateQuery(object name, string filter = "")
    {
        var query = $"namespace:{SubjectsNamespace} scope:subtree nodeType:Markdown {filter}".TrimEnd();
        return Mesh.GetWorkspace().GetQuery(name, query);
    }

    /// <summary>
    /// <c>workspace.GetQuery(name, query)</c> spins up a new
    /// <see cref="SyncedQueryMeshNodes"/> after hub instantiation and caches
    /// its observable in the hub-level <see cref="SyncedQueryRegistry"/>.
    /// Subsequent <c>GetQuery(name)</c> with no query string returns the
    /// same cached instance.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void GetQuery_GetOrCreate_CachesByName()
    {
        var observableA = CreateQuery("$dyn-cache");
        var observableB = Mesh.GetWorkspace().GetQuery("$dyn-cache");
        observableB.Should().NotBeNull(
            "the dynamically-created synced query is registered under its name");
        ReferenceEquals(observableA, observableB).Should().BeTrue(
            "the registry returns the same instance on subsequent calls");
    }

    /// <summary>
    /// <c>workspace.GetQuery(name)</c> with an unknown name returns
    /// <c>null</c> — the API doesn't auto-create from a name alone.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void GetQuery_UnknownName_ReturnsNull()
    {
        var observable = Mesh.GetWorkspace().GetQuery("$does-not-exist");
        observable.Should().BeNull();
    }

    /// <summary>
    /// Sanity check: in this test environment, can <c>NodeFactory.CreateNode</c>
    /// even produce its <see cref="MeshNode"/> emission for a Markdown node?
    /// (Isolates whether the failure mode in
    /// <see cref="Add_NewMatchingNode_AppearsInCollection"/> is the synced
    /// query pipeline or the create itself.)
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Sanity_CreateNode_EmitsCreatedNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await NodeFactory
            .CreateNode(MakeSubject("sanity", "SanityCheck"))
            .FirstAsync().ToTask(ct);
        created.Should().NotBeNull();
        created.Path.Should().Be($"{SubjectsNamespace}/sanity");
    }

    /// <summary>
    /// CreateNode of a matching node grows the synced collection — the
    /// path-set tracks the Added event from the upstream mesh query and a
    /// new per-path remote stream is added to the inner CombineLatest.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Add_NewMatchingNode_AppearsInCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        var observable = CreateQuery("$add-test");

        var collection = observable.Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();

        await NodeFactory.CreateNode(MakeSubject("alpha", "Alpha"))
            .FirstAsync().ToTask(ct);

        await collection
            .Where(arr => arr.Any(n => n.Path == $"{SubjectsNamespace}/alpha"))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }

    /// <summary>
    /// Adding multiple matching nodes — the path set accumulates and the
    /// inner CombineLatest emits the union as each per-path stream arrives.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Add_MultipleNodes_CollectionAccumulates()
    {
        var ct = TestContext.Current.CancellationToken;
        var observable = CreateQuery("$add-multi");

        var collection = observable.Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();

        await NodeFactory.CreateNode(MakeSubject("one", "One")).FirstAsync().ToTask(ct);
        await NodeFactory.CreateNode(MakeSubject("two", "Two")).FirstAsync().ToTask(ct);
        await NodeFactory.CreateNode(MakeSubject("three", "Three")).FirstAsync().ToTask(ct);

        var expected = new[]
        {
            $"{SubjectsNamespace}/one",
            $"{SubjectsNamespace}/two",
            $"{SubjectsNamespace}/three",
        };
        await collection
            .Where(arr => expected.All(p => arr.Any(n => n.Path == p)))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }

    /// <summary>
    /// Updating a matching node's content (without changing query
    /// membership) re-emits the collection through its per-path remote
    /// stream — CombineLatest fires when one of its sources emits.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Update_ExistingNode_ReEmitsWithNewValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var observable = CreateQuery("$update-test");

        var collection = observable.Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();

        var seedNode = MakeSubject("beta", "Original");
        await NodeFactory.CreateNode(seedNode).FirstAsync().ToTask(ct);

        var path = seedNode.Path;
        var afterCreate = await collection
            .Where(arr => arr.Any(n => n.Path == path && n.Name == "Original"))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        var current = afterCreate.Single(n => n.Path == path);

        await NodeFactory.UpdateNode(current with { Name = "Updated" })
            .FirstAsync().ToTask(ct);

        await collection
            .Where(arr => arr.Any(n => n.Path == path && n.Name == "Updated"))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }

    /// <summary>
    /// Deleting a matching node removes it from the synced collection —
    /// the path-set drops the path and Switch tears down the per-path stream.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Delete_RemovesFromCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        var observable = CreateQuery("$delete-test");

        var collection = observable.Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();

        await NodeFactory.CreateNode(MakeSubject("keep", "Keep")).FirstAsync().ToTask(ct);
        await NodeFactory.CreateNode(MakeSubject("drop", "Drop")).FirstAsync().ToTask(ct);

        var keepPath = $"{SubjectsNamespace}/keep";
        var dropPath = $"{SubjectsNamespace}/drop";

        await collection
            .Where(arr => arr.Any(n => n.Path == keepPath) && arr.Any(n => n.Path == dropPath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);

        await NodeFactory.DeleteNode(dropPath).FirstAsync().ToTask(ct);

        var afterDelete = await collection
            .Where(arr => arr.All(n => n.Path != dropPath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        afterDelete.Should().Contain(n => n.Path == keepPath,
            "the survivor must still be in the collection");
    }

    /// <summary>
    /// A property change that flips the node out of the query result set —
    /// here, changing <see cref="MeshNode.NodeType"/> to a value the
    /// <c>nodeType:Markdown</c> filter rejects — must remove the node from
    /// the synced collection. Distinct from outright deletion.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PropertyChange_NoLongerMatchesQuery_RemovesFromCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        var observable = CreateQuery("$flip-test");

        var collection = observable.Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();

        var seedNode = MakeSubject("flip", "Flippy");
        await NodeFactory.CreateNode(seedNode).FirstAsync().ToTask(ct);

        var path = seedNode.Path;
        var afterCreate = await collection
            .Where(arr => arr.Any(n => n.Path == path))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        var current = afterCreate.Single(n => n.Path == path);

        // Flip NodeType to Code — the synced query (nodeType:Markdown)
        // should no longer match.
        await NodeFactory.UpdateNode(current with { NodeType = "Code" })
            .FirstAsync().ToTask(ct);

        await collection
            .Where(arr => arr.All(n => n.Path != path))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }

    /// <summary>
    /// Two consumers asking <c>GetQuery(name)</c> for the same name must
    /// receive the SAME observable — the cache shares the upstream
    /// subscription via Replay(1).RefCount() in the base
    /// <see cref="VirtualTypeSource{T}"/>.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void GetQuery_TwoCallers_ShareSameInstance()
    {
        var observableA = CreateQuery("$share-test");
        var observableB = Mesh.GetWorkspace().GetQuery("$share-test");
        ReferenceEquals(observableA, observableB).Should().BeTrue(
            "registry hands back the same observable for the same name");
    }

    /// <summary>
    /// AccessAssignment-specific delete behaviour: a synced collection
    /// scoped to <c>nodeType:AccessAssignment</c> must surface the Removed
    /// event when an AccessAssignment <see cref="MeshNode"/> is deleted via
    /// <see cref="IMeshService.DeleteNode"/>.
    ///
    /// <para>This is the exact pipeline that
    /// <see cref="MeshWeaver.Hosting.Security.SecurityService"/> rides for
    /// permission evaluation; the in-memory test base wires the same
    /// <see cref="MeshWeaver.Hosting.Persistence.InMemoryPersistenceService"/>
    /// + change-notifier setup that production uses for runtime CreateNode /
    /// DeleteNode of access assignments.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task DeleteAccessAssignment_RemovesFromSyncedCollection()
    {
        var ct = TestContext.Current.CancellationToken;

        // Synced query that mirrors SecurityService.ObserveAllMeshNodes:
        // global subtree scope, filtered by NodeType=AccessAssignment.
        var observable = Mesh.GetWorkspace().GetQuery(
            "$access-assignment-delete-test",
            $"nodeType:{SecurityCollections.AccessAssignmentNodeType} scope:subtree");

        var collection = observable.Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();

        // Create the assignment via the runtime CreateNode path — same path
        // the AccessAssignmentTests deny tests exercise.
        var assignment = AssignmentNodeFactory.UserRole(
            "DeleteTestUser", "Editor", "DeleteTestScope");
        await NodeFactory.CreateNode(assignment).FirstAsync().ToTask(ct);

        var assignmentPath = assignment.Path;

        // Wait for the Added event to surface.
        await collection
            .Where(arr => arr.Any(n => n.Path == assignmentPath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);

        // Delete it and assert the synced collection drops the path.
        await NodeFactory.DeleteNode(assignmentPath).FirstAsync().ToTask(ct);

        await collection
            .Where(arr => arr.All(n => n.Path != assignmentPath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }
}
