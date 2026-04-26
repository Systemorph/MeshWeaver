using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// End-to-end tests for the synced query data source
/// (<see cref="SyncedQueryDataSourceExtensions.WithMeshQuery(VirtualDataSource, string, string?)"/>)
/// wired into a NodeType the same way production NodeTypes do.
///
/// Setup pattern (also documented in <c>Doc/DataMesh/SyncedQueryDataSource.md</c>):
/// <list type="number">
///   <item>A static <see cref="MeshNode"/> "Subscriber" registered via
///   <c>AddMeshNodes</c> in <see cref="ConfigureMesh"/>. Its
///   <c>HubConfiguration</c> adds the synced data source — exactly the way a
///   production NodeType (Sources/Tests/AccessAssignments) does.</item>
///   <item>Tests start from the client (<c>GetClient()</c>); <c>Mesh</c> is a
///   virtual coordinator and is never used directly.</item>
///   <item>Reads + writes use the framework's data-layer messages
///   (<see cref="DataChangeRequest"/>, <see cref="GetDataRequest"/> via
///   <c>ReadNodeAsync</c>, <see cref="IMeshService.UpdateNode"/>); no test-only
///   request handlers, no homebrew protocol.</item>
///   <item>Verification awaits responses + subscribes to live observables
///   with <c>Where(predicate).FirstAsync()</c> — never <c>Task.Delay</c>.</item>
/// </list>
/// </summary>
public class SyncedQueryDataSourceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SubscriberPath = $"{TestPartition}/Subscriber";
    private const string SubjectsNamespace = $"{TestPartition}/Subjects";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode("Subscriber", TestPartition)
            {
                Name = "Subscriber",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                HubConfiguration = config => config
                    .AddData(data => data.WithVirtualDataSource("$mesh-subjects",
                        vs => vs.WithMeshQuery(
                            query: $"namespace:{SubjectsNamespace} scope:subtree nodeType:Markdown")))
            });

    private static MeshNode MakeSubject(string id, string name)
        => new(id, SubjectsNamespace)
        {
            Name = name,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };

    /// <summary>
    /// Source-side update propagates to every observer of the same per-node
    /// MeshNode stream — including the synced data source on the subscriber.
    /// Verified by subscribing to the live <c>ObserveQuery</c> stream and
    /// awaiting the predicate.
    /// </summary>
    [Fact]
    public async Task OwningHubUpdate_SurfacesInLiveQueryStream()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{SubjectsNamespace}/alpha";

        // Subscribe to the live query stream BEFORE any writes — accumulator
        // pattern, no Take(1), no draining; the subscription stays hot for the
        // life of the test.
        var queryStream = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{SubjectsNamespace} scope:subtree nodeType:Markdown"));

        // Await the response to the create — the response only fires after
        // the source per-node hub has applied + persisted the node.
        await NodeFactory.CreateNode(MakeSubject("alpha", "Original"))
            .FirstAsync().ToTask(ct);

        // The query stream must surface the create — wait for the predicate.
        await queryStream
            .Where(c => c.Items.Any(n => n.Path == path && n.Name == "Original"))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);

        // Update at source side; await the response.
        var current = await ReadNodeAsync(path);
        current.Should().NotBeNull();
        await NodeFactory.UpdateNode(current! with { Name = "Updated At Source" })
            .FirstAsync().ToTask(ct);

        // Live query must surface the update.
        await queryStream
            .Where(c => c.Items.Any(n => n.Path == path && n.Name == "Updated At Source"))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);

        // Source-side ground truth.
        var reread = await ReadNodeAsync(path);
        reread!.Name.Should().Be("Updated At Source");
    }

    /// <summary>
    /// The query result set tracks adds + removes — verified through the live
    /// <c>ObserveQuery</c> stream the synced data source is built on. The
    /// stream emits Initial (full set on subscribe) + per-delta Added/Removed
    /// for every subsequent change; we <c>Scan</c> them into a running path
    /// set so the predicate sees the cumulative state.
    /// </summary>
    [Fact]
    public async Task QueryStream_TracksAddsAndRemoves()
    {
        var ct = TestContext.Current.CancellationToken;

        // Hot, accumulating view of the live path set.
        var pathSet = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{SubjectsNamespace} scope:subtree nodeType:Markdown"))
            .Scan(System.Collections.Immutable.ImmutableHashSet<string>.Empty,
                (set, c) => c.ChangeType switch
                {
                    QueryChangeType.Initial or QueryChangeType.Reset =>
                        c.Items.Select(n => n.Path).ToImmutableHashSet(),
                    QueryChangeType.Added or QueryChangeType.Updated =>
                        set.Union(c.Items.Select(n => n.Path)),
                    QueryChangeType.Removed =>
                        set.Except(c.Items.Select(n => n.Path)),
                    _ => set,
                })
            .Replay(1).RefCount();

        // Keep the subscription hot for the life of the test — never Take(1).
        using var keepAlive = pathSet.Subscribe();

        await NodeFactory.CreateNode(MakeSubject("one", "One")).FirstAsync().ToTask(ct);
        await NodeFactory.CreateNode(MakeSubject("two", "Two")).FirstAsync().ToTask(ct);
        await NodeFactory.CreateNode(MakeSubject("three", "Three")).FirstAsync().ToTask(ct);

        await pathSet
            .Where(set => set.Contains($"{SubjectsNamespace}/one")
                       && set.Contains($"{SubjectsNamespace}/two")
                       && set.Contains($"{SubjectsNamespace}/three"))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);

        await NodeFactory.DeleteNode($"{SubjectsNamespace}/two").FirstAsync().ToTask(ct);

        await pathSet
            .Where(set => !set.Contains($"{SubjectsNamespace}/two"))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);
    }
}
