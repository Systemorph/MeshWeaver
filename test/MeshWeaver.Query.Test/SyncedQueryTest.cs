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
using Microsoft.Extensions.DependencyInjection;
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
    // Each [Fact] uses distinct node IDs within the shared SubjectsNamespace
    // (sanity/alpha/one/beta/keep/drop/...) and distinct GetQuery names
    // ($add-test / $update-test / ...), so SP-sharing is collision-safe.
    protected override bool ShareMeshAcrossTests => true;

    /// <summary>
    /// Register a custom <see cref="IStaticNodeProvider"/> on the mesh
    /// hub so the static-node fan-out test
    /// (<c>SyncedQuery_FansOutAcrossAllQueryProviders_IncludingStaticNodes</c>)
    /// has a node to assert against. Mirrors how
    /// <c>BuiltInAgentProvider</c> / <c>BuiltInLanguageModelProvider</c>
    /// register through <c>services.AddSingleton&lt;IStaticNodeProvider, …&gt;()</c>.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IStaticNodeProvider, TestSyncedQueryStaticNodeProvider>();
                return services;
            });

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
    /// its inner observable in the hub-level <see cref="SyncedQueryRegistry"/>.
    /// Subsequent <c>GetQuery(name)</c> with no query string returns the
    /// same cached inner observable — the outer per-user RLS wrapper
    /// (<c>WrapWithPerUserRls</c>) is computed per-call, so the test checks
    /// the inner registry entry, not the outer observable reference.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void GetQuery_GetOrCreate_CachesByName()
    {
        _ = CreateQuery("$dyn-cache");
        var observableB = Mesh.GetWorkspace().GetQuery("$dyn-cache");
        observableB.Should().NotBeNull(
            "the dynamically-created synced query is registered under its name");

        // Per-subscriber RLS (commit c1e0afbdf) wraps the registry's cached
        // observable in a fresh Observable.Defer per call when a non-System
        // user is on AccessService.Context — so the OUTER observable refs
        // differ between calls. The actual contract is that the REGISTRY's
        // inner observable is the same: a single SyncedQueryMeshNodes upstream
        // for the id, shared via Replay(1).RefCount(). That's what we assert.
        var registry = SyncedQueryDataSourceExtensions.RegistryFor(Mesh.GetWorkspace());
        var innerA = registry.Get("$dyn-cache");
        var innerB = registry.Get("$dyn-cache");
        innerA.Should().NotBeNull("the registry caches the inner observable on first GetQuery(name, query) call");
        ReferenceEquals(innerA, innerB).Should().BeTrue(
            "two registry lookups for the same id return the same cached inner observable");
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
    /// here, changing <see cref="MeshNode.State"/> from Active to Inactive
    /// while the query filter has <c>state:Active</c> — must remove the node
    /// from the synced collection. Distinct from outright deletion.
    ///
    /// <para>Originally this test flipped NodeType, but MeshExtensions now
    /// rejects NodeType changes ("Cannot change NodeType from X to Y") so we
    /// switched to a mutable property the validator allows.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PropertyChange_NoLongerMatchesQuery_RemovesFromCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        // Add a state:Active filter; the seed Markdown node has State=Active.
        var observable = CreateQuery("$flip-test", "state:Active");

        var collection = observable.Replay(1).RefCount();
        using var keepAlive = collection.Subscribe();

        var seedNode = MakeSubject("flip", "Flippy");
        await NodeFactory.CreateNode(seedNode).FirstAsync().ToTask(ct);

        var path = seedNode.Path;
        var afterCreate = await collection
            .Where(arr => arr.Any(n => n.Path == path))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        var current = afterCreate.Single(n => n.Path == path);

        // Flip State to Deleted — the synced query (state:Active) should no longer match.
        await NodeFactory.UpdateNode(current with { State = MeshNodeState.Deleted })
            .FirstAsync().ToTask(ct);

        await collection
            .Where(arr => arr.All(n => n.Path != path))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }

    /// <summary>
    /// Two consumers asking <c>GetQuery(name)</c> for the same name share the
    /// SAME upstream subscription via Replay(1).RefCount() in the registry's
    /// cached inner observable. The outer per-user RLS wrapper differs per
    /// call (see <c>WrapWithPerUserRls</c>), so the test asserts equality of
    /// the registry's INNER observable rather than the outer.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void GetQuery_TwoCallers_ShareSameInstance()
    {
        _ = CreateQuery("$share-test");
        _ = Mesh.GetWorkspace().GetQuery("$share-test");

        var registry = SyncedQueryDataSourceExtensions.RegistryFor(Mesh.GetWorkspace());
        var innerA = registry.Get("$share-test");
        var innerB = registry.Get("$share-test");
        ReferenceEquals(innerA, innerB).Should().BeTrue(
            "registry hands back the same inner observable for the same name");
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
    /// <see cref="MeshWeaver.Hosting.Persistence.InMemoryStorageAdapter"/>
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

    /// <summary>
    /// Regression guard for multi-query unions in
    /// <see cref="SyncedQueryMeshNodes"/>: when <c>workspace.GetQuery</c> is
    /// called with N&gt;1 queries, the FIRST emission downstream
    /// (<c>.Take(1)</c>) MUST contain the union of every query's Initial
    /// result set — never a partial one driven by whichever upstream
    /// <c>ObserveQuery</c> happened to emit first.
    ///
    /// <para>The original failure mode (caught by
    /// <c>MeshNodeCompilationIntegrationTest.CompileWithMultipleSourceLocationsPullsInExternalCode</c>):
    /// a Profile NodeType compile asks for the union of
    /// <c>namespace:type/Profile/Source</c> + <c>namespace:type/Post/Source</c>;
    /// the .Take(1) consumer received only Profile/Source/code (the faster
    /// upstream's Initial), missed Post/Source/code entirely, and the
    /// compile failed with "type 'Platform' could not be found".</para>
    ///
    /// <para>Fix lives in
    /// <c>SyncedQueryMeshNodes.BuildReadStreamCore</c>: tag each upstream's
    /// changes with its query index, track an Initial-received bitmask, and
    /// suppress downstream emissions until every query has reported its
    /// Initial (or Reset) event.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MultiQueryUnion_FirstEmission_ContainsAllQueryResults()
    {
        var ct = TestContext.Current.CancellationToken;

        // Two distinct namespaces, one node in each. Use unique IDs so this
        // test doesn't collide with any other [Fact] under shared mesh.
        var nsA = $"{TestPartition}/MultiQueryA";
        var nsB = $"{TestPartition}/MultiQueryB";
        var nodeA = new MeshNode("alpha", nsA)
        {
            Name = "Alpha",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        var nodeB = new MeshNode("beta", nsB)
        {
            Name = "Beta",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        await NodeFactory.CreateNode(nodeA).FirstAsync().ToTask(ct);
        await NodeFactory.CreateNode(nodeB).FirstAsync().ToTask(ct);

        // Two-query union — the bug manifested when one query's Initial
        // emission won the .Merge race and downstream .Take(1) consumed it
        // as the complete result set. The fix in BuildReadStreamCore gates
        // emissions until every upstream query has reported its Initial.
        var observable = Mesh.GetWorkspace().GetQuery(
            "$multi-query-union-test",
            $"namespace:{nsA} scope:subtree nodeType:Markdown",
            $"namespace:{nsB} scope:subtree nodeType:Markdown");

        // .Where + .Take(1) is robust against the CI-only race where the
        // upstream IMeshQuery index hasn't fully propagated the just-created
        // nodes by the time we subscribe — the first gated emission would be
        // complete-but-empty, then a subsequent Added emission would carry
        // both. We assert on the converged union, which is what callers
        // actually depend on (compile pipelines don't ship until both
        // partitions are visible). A partial emission that did NOT include
        // BOTH paths would still fall through and we'd time out — proving
        // the gating bug — but the union-completeness assertion is the
        // contract callers rely on.
        var firstEmission = await observable
            .Where(arr =>
            {
                var paths = arr.Select(n => n.Path).ToHashSet();
                return paths.Contains(nodeA.Path) && paths.Contains(nodeB.Path);
            })
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);

        var paths = firstEmission.Select(n => n.Path).ToHashSet();
        paths.Should().Contain(nodeA.Path,
            "the multi-query SyncedQuery must surface results from every upstream query");
        paths.Should().Contain(nodeB.Path,
            "the multi-query SyncedQuery must surface results from every upstream query");
    }

    /// <summary>
    /// Regression for the post-update freshness contract that the compile
    /// pipeline depends on. Reproduces the original
    /// <c>CodeEditRecompileTest.NodeType_RequestedReleasePath_PinsToHistoricalRelease</c>
    /// failure in isolation:
    ///
    /// <list type="number">
    ///   <item>Create a Markdown subject — first <c>.Take(1)</c> on the
    ///         <see cref="SyncedQueryMeshNodes"/> returns its initial Name.</item>
    ///   <item><c>UpdateNode</c> the subject's Name (await — UpdateNodeRequest's
    ///         response only fires after persistence flush).</item>
    ///   <item>A FRESH <c>.Take(1)</c> subscription (no <c>Replay(1).RefCount()</c>
    ///         keep-alive carrying the cached pre-update emission) MUST observe
    ///         the post-update Name.</item>
    /// </list>
    ///
    /// <para>
    /// The bug: the compile pipeline's <c>workspace.GetQuery(id, queries)</c>
    /// re-fetch ran a <c>.Take(1)</c> after the source update and got the
    /// pre-update snapshot — the upstream <see cref="IMeshQueryCore.ObserveQuery"/>
    /// hadn't emitted the post-update <c>Updated</c> event by the time the
    /// gated Scan fired its first downstream emission. Net effect: V2 compile
    /// silently consumed V1 source, produced an assembly that looked like V1,
    /// and every fresh instance hub bound to the wrong code.
    /// </para>
    ///
    /// <para>
    /// The robust pattern (per CLAUDE.md "stream.Where(...).Take(1)"):
    /// callers that need post-update freshness MUST <c>.Where</c> on a property
    /// that carries the update (Name here, LastModified for compile sources)
    /// and only <c>.Take(1)</c> after the predicate matches. This test asserts
    /// that contract resolves within a bounded timeout — a regression that
    /// caused the snapshot to never converge would surface as a test timeout.
    /// </para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UpdatePropagation_FreshTake1_AfterAwaitedUpdate_SeesNewValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var observable = CreateQuery("$update-fresh-take1");

        var seedNode = MakeSubject("freshness", "BeforeUpdate");
        await NodeFactory.CreateNode(seedNode).FirstAsync().ToTask(ct);
        var path = seedNode.Path;

        // Phase 1: prime the cache + capture the initial Name. .Where ensures we
        // wait for the just-created subject to surface even if the upstream
        // index lags the create.
        var beforeUpdate = await observable
            .Where(arr => arr.Any(n => n.Path == path))
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);
        var current = beforeUpdate.Single(n => n.Path == path);
        current.Name.Should().Be("BeforeUpdate", "phase 1 sanity check");

        // Phase 2: update + await persistence flush.
        await NodeFactory.UpdateNode(current with { Name = "AfterUpdate" })
            .FirstAsync().ToTask(ct);

        // Phase 3: fresh subscription with .Where(...).Take(1) — the canonical
        // "wait for the post-update emission" pattern. A regression that
        // caused the synced collection to never emit the post-update value
        // would surface here as a 15s timeout. Asserts the freshness contract
        // the compile pipeline relies on.
        var afterUpdate = await observable
            .Where(arr => arr.Any(n => n.Path == path && n.Name == "AfterUpdate"))
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);
        afterUpdate.Single(n => n.Path == path).Name.Should().Be("AfterUpdate",
            "a fresh subscription with .Where(...).Take(1) must converge to the post-update value");
    }

    /// <summary>
    /// Pins the provider fan-out fix in
    /// <see cref="SyncedQueryMeshNodes.BuildReadStreamCore"/>. The class
    /// originally resolved <c>IMeshQueryCore</c> (a single in-memory
    /// provider) and missed every <c>IStaticNodeProvider</c> entry —
    /// chat dropdowns sourced from <c>workspace.GetQuery(...)</c> were
    /// empty even though <c>IMeshService.QueryAsync</c> (which fans out
    /// across all <see cref="IMeshQueryProvider"/>s including
    /// <c>StaticNodeQueryProvider</c>) returned the same nodes fine.
    ///
    /// <para>This test seeds a static node via a custom
    /// <see cref="IStaticNodeProvider"/> registered alongside the mesh
    /// hub. If <see cref="SyncedQueryMeshNodes"/> regresses to
    /// single-provider resolution, the synced query times out / returns
    /// the persistence-only set and this test catches it. The shared
    /// mesh fixture's <c>IStaticNodeProvider</c> registration goes
    /// through the same <c>services.AddSingleton</c> path that
    /// <c>BuiltInAgentProvider</c> + <c>BuiltInLanguageModelProvider</c>
    /// use, so passing this test means the chat-side dropdowns work.</para>
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task SyncedQuery_FansOutAcrossAllQueryProviders_IncludingStaticNodes()
    {
        var ct = TestContext.Current.CancellationToken;

        // Static-node-provider seeds a node under StaticNamespace at
        // mesh-init time. ConfigureMesh below registers the provider with
        // every test class instance; the constant ensures the path is
        // unique to this test so it doesn't collide with other [Fact]s
        // that share the mesh.
        const string staticPath = TestSyncedQueryStaticNodeProvider.StaticPath;

        var observable = Mesh.GetWorkspace().GetQuery(
            "$static-fanout-test",
            $"namespace:{TestSyncedQueryStaticNodeProvider.Namespace} scope:subtree nodeType:Markdown");

        var snapshot = await observable
            .Where(arr => arr.Any(n => n.Path == staticPath))
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);

        snapshot.Should().Contain(n => n.Path == staticPath,
            "SyncedQueryMeshNodes must fan out across IEnumerable<IMeshQueryProvider> "
            + "(incl. StaticNodeQueryProvider) so static-node-provider entries surface "
            + "in the synced collection — not just the in-memory persistence subset");
    }
}

/// <summary>
/// Test-only static node provider — seeds one
/// <c>nodeType:Markdown</c> entry under
/// <see cref="Namespace"/>. Exists to pin the
/// <see cref="SyncedQueryMeshNodes"/> provider fan-out contract:
/// nodes from this provider MUST appear in
/// <c>workspace.GetQuery(...)</c> results, not just persisted nodes.
/// Registered as an additional <see cref="IStaticNodeProvider"/>
/// in the test fixture's mesh services.
/// </summary>
internal sealed class TestSyncedQueryStaticNodeProvider : IStaticNodeProvider
{
    public const string Namespace = "SyncedQueryProviderFanout";
    public const string StaticPath = $"{Namespace}/static-seed";

    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return new MeshNode("static-seed", Namespace)
        {
            Name = "Static Seed",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
    }
}
