using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>One partition that cannot answer costs the batched bake ITS OWN types — never all of
/// them.</b>
///
/// <para><b>Measured</b> on memex-cloud, core <c>bf5ac85526</c>, 2026-09-24: on every one of eight
/// boots between 07:12Z and 08:06Z the batched source discovery's first global fetch
/// (<c>nodeType:Code partitions:all</c>) settled at 1586 nodes and the pass was abandoned ~25 ms
/// later — <c>DynamicTypePreWarmer: batched source discovery did not establish the source sets —
/// abandoning the batch and falling back to the activation-driven sweep for ALL 53 pending
/// type(s)</c>. Each global fetch is ONE UNION over every partition schema, so a single partition
/// whose read fails fails every fetch, and <c>ResolveSources</c> surfaced that as a fault of the
/// whole batch: 38–80 pending types per boot went to the activation-driven sweep, 5–14 minutes a
/// boot, and on memex (with #5643's wait deadlock) hours.</para>
///
/// <para><b>What this pins.</b> The provider below is a test PROVIDER (the extension point every
/// backend plugs into — the same seam <c>ColdEmptyInitialIsNotCachedTest</c> drives), not a mock of
/// a core service: it fails exactly the reads a partitioned backend fails when one partition cannot
/// answer — every mesh-wide <c>nodeType:Code</c> fetch, plus the anchored reads of the one broken
/// type. Everything else is the real mesh, the real query fan-in and the sweep's own discovery. The
/// healthy type must come back with its real source set; the broken one must be ABSENT (so the sweep
/// warms it by activation), never present with an empty set (that would be #1216's fabricated
/// verdict). On the pre-fix code the whole call faults, so the first assertion fails — the negative
/// control is the defect itself.</para>
/// </summary>
public class OnePartitionCostsOnlyItsOwnTypesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string GoodType => $"{TestPartition}/Healthy{suffix}";
    private string BrokenType => $"{TestPartition}/Broken{suffix}";

    private readonly UnanswerablePartitionProvider provider = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider>(provider));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static MeshNode TypeNode(string id) => new(id, TestPartition)
    {
        NodeType = MeshNode.NodeTypePath,
        Name = id,
        State = MeshNodeState.Active,
        Content = new NodeTypeDefinition { Configuration = "config => config" },
    };

    private static MeshNode Code(string ns) => new("Main", ns)
    {
        NodeType = "Code",
        State = MeshNodeState.Active,
        Content = new CodeConfiguration
        {
            Language = "csharp",
            Code = "public static class Answers { public static int Answer() => 42; }",
        },
    };

    [Fact(Timeout = 240_000)]
    public async Task AGlobalFetchThatCannotBeAnswered_WithholdsOnlyTheTypesWhoseOwnQueriesFail()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var node in new[]
                 {
                     TypeNode($"Healthy{suffix}"), Code($"{GoodType}/Source"),
                     TypeNode($"Broken{suffix}"), Code($"{BrokenType}/Source"),
                 })
            await MeshService.CreateNode(node).Take(1)
                .Should().Within(TestTimeouts.Convergence)
                .Emit($"{node.Path} must exist before discovery reads it", ct);
        await UntilListed($"{GoodType}/Source/Main", ct);

        // Armed only now, so the seeding above reads the mesh as it is.
        provider.Arm(BrokenType);

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var definitions = ImmutableDictionary<string, NodeTypeDefinition?>.Empty
            .Add(GoodType, new NodeTypeDefinition { Configuration = "config => config" })
            .Add(BrokenType, new NodeTypeDefinition { Configuration = "config => config" });
        var sets = await NodeTypeBatchBake
            .ResolveSources(MeshService, access, definitions, [GoodType, BrokenType], null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a partition that cannot answer must not fault the whole discovery pass", ct);

        Output.WriteLine("established: {0}", string.Join(", ", sets.Keys));
        Assert.True(provider.GlobalFetchesRefused > 0,
            "the mesh-wide fetch must actually have been refused, or this test measures nothing");
        Assert.True(sets.TryGetValue(GoodType, out var good),
            "the healthy type's own anchored query answers, so it stays in the batch");
        Assert.Equal([$"{GoodType}/Source/Main"], good!.Select(n => n.Path).ToArray());
        Assert.False(sets.ContainsKey(BrokenType),
            "the type whose own query fails is withheld (warmed by activation), never handed an empty set");
    }

    /// <summary>
    /// The per-type rule on its own, without a mesh: a type is in the map only when EVERY one of its
    /// queries answered and the answer is established; a failed query withholds the types that read
    /// it and nobody else, and a contradicted empty answer withholds its type as before (#3663).
    /// </summary>
    [Fact]
    public void AssemblePerQuery_WithholdsExactlyTheTypesWhoseOwnQueriesFailed()
    {
        const string qA = "namespace:P/A/Source scope:subtree nodeType:Code";
        const string qB = "namespace:Q/B/Source scope:subtree nodeType:Code";
        const string qC = "namespace:P/C/Source scope:subtree nodeType:Code";
        var node = new MeshNode("Main", "P/A/Source") { NodeType = "Code" };
        var answers = ImmutableDictionary<string, NodeTypeBatchBake.QueryAnswer>.Empty
            .Add(qA, new(ImmutableDictionary<string, MeshNode>.Empty.Add(node.Path, node), null))
            .Add(qB, new(null, new InvalidOperationException("42703: column n.created_by does not exist")))
            .Add(qC, new(ImmutableDictionary<string, MeshNode>.Empty, null));
        ImmutableList<NodeTypeBatchBake.PendingType> perType =
        [
            new("P/A", [qA], [], DeclaresSources: false, KnownSourceCount: 1),
            new("Q/B", [qB], [], DeclaresSources: false, KnownSourceCount: 1),
            // Empty, and the record says it HAS two files: unestablished, withheld.
            new("P/C", [qC], [], DeclaresSources: false, KnownSourceCount: 2),
        ];

        var sets = NodeTypeBatchBake.AssemblePerQuery(perType, answers, null);

        Assert.Equal(["P/A"], sets.Keys.ToArray());
        Assert.Equal([node.Path], sets["P/A"].Select(n => n.Path).ToArray());
    }

    private async Task UntilListed(string path, CancellationToken ct)
        => await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}").AsSystem())
                .Take(1))
            .Where(change => change.Items.Any(n => n.Path == path))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"discovery must be able to see {path}", ct);

    /// <summary>
    /// Answers every query with an empty Initial — except, once armed, the reads a partitioned
    /// backend fails when one partition cannot answer: every MESH-WIDE <c>nodeType:Code</c> fetch
    /// (one UNION over every partition), and the anchored reads of the one broken type.
    /// </summary>
    private sealed class UnanswerablePartitionProvider : IMeshQueryProvider
    {
        private string? broken;
        private int refused;

        public string Name => nameof(UnanswerablePartitionProvider);

        public int GlobalFetchesRefused => Volatile.Read(ref refused);

        public void Arm(string brokenType) => Volatile.Write(ref broken, brokenType);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(
            MeshQueryRequest request, JsonSerializerOptions options) =>
            Observable.Defer(() =>
            {
                var brokenType = Volatile.Read(ref broken);
                var queries = request.EffectiveQueries.ToList();
                var code = queries.Any(q => q.Contains("nodeType:Code", StringComparison.OrdinalIgnoreCase));
                if (brokenType is not null && code)
                {
                    var meshWide = queries.Any(q =>
                        q.Contains(ParsedQuery.CrossPartitionQualifier, StringComparison.OrdinalIgnoreCase)
                        || q.Contains("namespace:*", StringComparison.OrdinalIgnoreCase));
                    if (meshWide)
                        Interlocked.Increment(ref refused);
                    if (meshWide)
                        return Observable.Throw<QueryResultChange<T>>(new InvalidOperationException(
                            "42703: column n.created_by does not exist (one partition cannot answer)"));
                    // The broken type's OWN read answers the way a partitioned provider answers a
                    // read it could not complete: an Initial over the rows that survived, marked
                    // incomplete — the shape that must NOT be folded as a source set.
                    if (queries.Any(q => q.Contains(brokenType, StringComparison.OrdinalIgnoreCase)))
                        return Observable.Return(new QueryResultChange<T>
                        {
                            ChangeType = QueryChangeType.Initial,
                            Items = Array.Empty<T>(),
                            Timestamp = DateTimeOffset.UtcNow,
                            SnapshotIncomplete = true,
                        });
                }
                return Observable.Return(new QueryResultChange<T>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = Array.Empty<T>(),
                    Timestamp = DateTimeOffset.UtcNow,
                });
            });

        public IObservable<IReadOnlyCollection<QueryResult>> Query(
            MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }
}
