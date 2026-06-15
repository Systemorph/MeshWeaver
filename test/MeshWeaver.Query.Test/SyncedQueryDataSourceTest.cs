using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
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
///   <c>ReadNode</c>, <see cref="IMeshService.UpdateNode"/>); no test-only
///   request handlers, no homebrew protocol.</item>
///   <item>Verification subscribes to live observables and asserts on the
///   matching emission via <c>.Should().Match(...)</c> — never <c>Task.Delay</c>.</item>
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
    /// Verified by subscribing to the live <c>Query</c> stream and
    /// asserting on the matching emission.
    /// </summary>
    [Fact]
    public async Task OwningHubUpdate_SurfacesInLiveQueryStream()
    {
        var path = $"{SubjectsNamespace}/alpha";

        // Subscribe to the live query stream — the assertion subscribes the cold
        // observable and waits for the matching emission.
        var queryStream = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{SubjectsNamespace} scope:subtree nodeType:Markdown"));

        // The create's emission only fires after the source per-node hub has
        // applied + persisted the node.
        await NodeFactory.CreateNode(MakeSubject("alpha", "Original")).Should().Emit();

        // The query stream must surface the create — wait for the predicate.
        await queryStream
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(c => c.Items.Any(n => n.Path == path && n.Name == "Original"));

        // Update at source side; the update's emission confirms the write.
        var current = await ReadNode(path).Should().Emit();
        current.Should().NotBeNull();
        await NodeFactory.UpdateNode(current! with { Name = "Updated At Source" }).Should().Emit();

        // Live query must surface the update.
        await queryStream
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(c => c.Items.Any(n => n.Path == path && n.Name == "Updated At Source"));

        // Source-side ground truth — wait for the authoritative read to reflect
        // the update too. ReadNode rounds-trips the owner hub each call, so retry
        // via an interval until the read sees the updated name.
        var reread = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode(path))
            .Should().Within(TimeSpan.FromSeconds(10))
            .Match(n => n?.Name == "Updated At Source");
        reread!.Name.Should().Be("Updated At Source");
    }

    /// <summary>
    /// The query result set tracks adds + removes — verified through the live
    /// <c>Query</c> stream the synced data source is built on. The
    /// stream emits Initial (full set on subscribe) + per-delta Added/Removed
    /// for every subsequent change; we <c>Scan</c> them into a running path
    /// set so the predicate sees the cumulative state.
    /// </summary>
    [Fact]
    public async Task QueryStream_TracksAddsAndRemoves()
    {
        // Hot, accumulating view of the live path set.
        var pathSet = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
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

        // Keep the subscription hot for the life of the test.
        using var keepAlive = pathSet.Subscribe();

        await NodeFactory.CreateNode(MakeSubject("one", "One")).Should().Emit();
        await NodeFactory.CreateNode(MakeSubject("two", "Two")).Should().Emit();
        await NodeFactory.CreateNode(MakeSubject("three", "Three")).Should().Emit();

        await pathSet
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(set => set.Contains($"{SubjectsNamespace}/one")
                       && set.Contains($"{SubjectsNamespace}/two")
                       && set.Contains($"{SubjectsNamespace}/three"));

        await NodeFactory.DeleteNode($"{SubjectsNamespace}/two").Should().Emit();

        await pathSet
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(set => !set.Contains($"{SubjectsNamespace}/two"));
    }
}
