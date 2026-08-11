using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// The SCALE contract for the batch bake's source discovery (issue #1216) — and the reason this
/// test lives on PostgreSQL rather than beside the other batch-bake tests.
///
/// <para><b>The defect was invisible to every in-memory test.</b> Discovery issues ONE global
/// <c>nodeType:Code</c> fetch. That query carries no <c>namespace:</c> and no <c>path:</c>, so on
/// Postgres it is the UNPINNED shape served by the cross-schema fan-out — and
/// <c>PostgreSqlCrossSchemaQueryProvider.QueryAcrossSchemasAsync</c> answers a query with no stated
/// limit by substituting a hard default of <b>50</b>. The in-memory
/// <c>StorageAdapterMeshQueryProvider</c> and the per-schema <c>PostgreSqlMeshQuery</c> both treat
/// "no limit" as UNBOUNDED, so the identical code path is correct on the adapter every unit test
/// uses. On memex-cloud 2026-08-11 the pass reported <c>resolved 50 Code node(s) … for 237 pending
/// type(s)</c> and 169 types compiled against NOTHING.</para>
///
/// <para><b>What this pins.</b> A mesh holding MORE Code nodes than that default, spread across two
/// partitions, must resolve EVERY type's full source set — and resolve exactly what the ACTIVATION
/// path resolves. The activation path is not approximated here: it issues each type's expanded
/// source queries individually (<c>MeshNodeCompilationService.DirectSourceProbe</c>), and those
/// queries ARE partition-pinned, which is precisely why it never hit the cap and the batch did. The
/// two sets are compared node-for-node.</para>
/// </summary>
[Collection("PostgreSql")]
public class BatchBakeSourceDiscoveryScaleTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    /// <summary>
    /// Code nodes per type. Two types ⇒ 60 in total, comfortably above the cross-schema fan-out's
    /// 50-row default — the whole point of the test. Anything at or below it reproduces nothing.
    /// </summary>
    private const int SourcesPerType = 30;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddGraph();
    }

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private IObservable<Unit> ProvisionPartition(string ns) =>
        Mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(p => p.EnsurePartitionProvisioned(ns))
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastOrDefaultAsync();

    /// <summary>
    /// The declared source query every type in this test uses. <c>namespace:Source</c> is relative,
    /// so <see cref="CodeQueryResolver"/> rebases it to <c>{typePath}/Source</c> and ANDs
    /// <c>nodeType:Code</c> — the ordinary shape, and one the in-memory matcher fully mirrors, so
    /// discovery serves it from the GLOBAL fetch (the query under test) rather than individually.
    /// </summary>
    private static readonly IReadOnlyList<string> DeclaredSources = ["namespace:Source scope:subtree"];

    /// <summary>
    /// Provisions a partition and writes <see cref="SourcesPerType"/> Code nodes under
    /// <c>{ns}/{typeId}/Source</c>. Returns the type's path.
    /// </summary>
    private async Task<string> SeedTypeWithSources(string ns, string typeId)
    {
        await ProvisionPartition(ns).Should().Within(60.Seconds()).Emit();

        var typePath = $"{ns}/{typeId}";
        for (var i = 0; i < SourcesPerType; i++)
            await MeshService.CreateNode(new MeshNode($"Src{i:D2}", $"{typePath}/Source")
            {
                Name = $"Src{i:D2}",
                NodeType = CodeNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new CodeConfiguration
                {
                    Code = $"public static class Src{i:D2}_{typeId} {{ public const int N = {i}; }}"
                }
            }).Should().Within(30.Seconds()).Emit();

        Output.WriteLine($"seeded {typePath} with {SourcesPerType} Code node(s)");
        return typePath;
    }

    /// <summary>
    /// The same pure fold the compiler's own probe applies
    /// (<c>MeshNodeCompilationService.ApplyQueryChange</c>, internal to MeshWeaver.Graph so it is
    /// restated rather than referenced): every change ACCUMULATES, and only a <c>Removed</c> takes
    /// anything out.
    ///
    /// <para>🚨 An <c>Initial</c> must NOT reset the accumulator. A query's Initial arrives in
    /// CHUNKS — several <c>Initial</c> emissions, each carrying part of the snapshot — so a fold
    /// that clears on Initial keeps only the LAST chunk. Written that way first, this helper
    /// reported 2 nodes for a 60-node mesh and looked exactly like the truncation bug under test.
    /// </para>
    /// </summary>
    private static ImmutableDictionary<string, MeshNode> ApplyChange(
        ImmutableDictionary<string, MeshNode> accumulated, QueryResultChange<MeshNode> change)
    {
        if (change?.Items is not { Count: > 0 } items)
            return accumulated;
        foreach (var node in items)
        {
            if (string.IsNullOrEmpty(node?.Path)) continue;
            accumulated = change.ChangeType == QueryChangeType.Removed
                ? accumulated.Remove(node.Path)
                : accumulated.SetItem(node.Path, node);
        }
        return accumulated;
    }

    /// <summary>
    /// 🚨 The SECOND half of #1216, pinned separately because it is the one that produced the
    /// EMPTY source sets (a row cap can only ever produce a PARTIAL one).
    ///
    /// <para>On the partitioned Postgres backend a node's table is chosen by PATH SEGMENT — a
    /// <c>Source/</c> or <c>Test/</c> segment routes to the per-schema <c>code</c> satellite table.
    /// Resolving a table from a <c>nodeType:</c> filter uses a different map, and <c>Code</c> is
    /// deliberately absent from it, so the discovery pass's original single <c>nodeType:Code</c>
    /// fetch fans out over <c>mesh_nodes</c> and finds NONE of them. This asserts the two facts
    /// directly against the database, so a future change to satellite routing that silently
    /// re-breaks the batch fails HERE, naming the mechanism, instead of somewhere downstream as
    /// "types are content-broken".</para>
    /// </summary>
    private async Task AssertCodeNodesLiveInTheSatelliteTable(string ns)
    {
        await using var code = _fixture.DataSource.CreateCommand($"SELECT count(*) FROM \"{ns}\".code");
        var inCode = (long)(await code.ExecuteScalarAsync())!;
        await using var primary = _fixture.DataSource.CreateCommand($"SELECT count(*) FROM \"{ns}\".mesh_nodes");
        var inPrimary = (long)(await primary.ExecuteScalarAsync())!;
        Output.WriteLine($"storage {ns}: code={inCode} mesh_nodes={inPrimary}");

        inCode.Should().Be(SourcesPerType,
            "a Source/ path segment routes a Code node to the per-schema 'code' satellite table — "
            + "this is the premise the global discovery fetch has to be built around");
        inPrimary.Should().Be(0,
            "no Code node lands in mesh_nodes here, so a discovery pass that only fans out over "
            + "mesh_nodes (which is what an unpinned nodeType:Code query does) sees nothing at all");
    }

    private IObservable<IReadOnlyList<string>> ActivationPathSources(string typePath, int expected)
    {
        var queries = CodeQueryResolver
            .ExpandAll(DeclaredSources, CodeQueryResolver.DefaultSources, typePath)
            .Concat(CodeQueryResolver.ExpandAll(null, CodeQueryResolver.DefaultTests, typePath))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // 🚨 System-scoped, exactly like the batched discovery under test
        // (NodeTypeBatchBake.ResolveSources wraps the identical Observable.Using). Source discovery
        // is framework infrastructure that reads across every partition; running the BASELINE as the
        // ambient test user instead would compare a full set against an RLS-filtered one.
        return Observable.Using(
            () => AccessContextScope.AsSystem(Mesh.ServiceProvider.GetService<AccessService>()),
            _ =>
            {
                var probe = Observable
                    .CombineLatest(queries.Select(q => Observable
                        .Defer(() => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(q)))
                        .Scan(
                            ImmutableDictionary<string, MeshNode>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
                            ApplyChange)
                        .Throttle(TimeSpan.FromSeconds(1))
                        .Take(1)))
                    // Sorted Ordinal, matching the order the batched discovery emits each set in, so
                    // the two can be compared exactly rather than as bags.
                    .Select(results => (IReadOnlyList<string>)results
                        .SelectMany(r => r.Keys)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .ToList());

                return Observable.Interval(TimeSpan.FromMilliseconds(500))
                    .StartWith(0L)
                    .SelectMany(_ => probe)
                    .Do(set => Output.WriteLine(
                        $"activation probe {typePath}: {set.Count}/{expected} via [{string.Join(" | ", queries)}]"))
                    .Where(set => set.Count >= expected)
                    .FirstAsync()
                    .Timeout(TimeSpan.FromSeconds(60));
            });
    }

    /// <summary>
    /// 🚨 THE REGRESSION TEST FOR #1216. Sixty Code nodes across two partitions — more than the
    /// cross-schema fan-out's 50-row default — and the batched discovery must resolve every one of
    /// them into the right type's source set.
    ///
    /// <para>Before the fix this fails on the very first assertion: the global <c>nodeType:Code</c>
    /// fetch comes back with 50 rows for a 60-node mesh, so at least one type's set is short and
    /// its compile would have run against a partial set — producing a genuine-looking CS0246 the
    /// bake gate reads as an image regression (#1218), or an empty set that reports as the
    /// non-gating <c>NoSources</c> (#1204's class) while the type is perfectly healthy.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task Discovery_ResolvesEveryTypesFullSourceSet_WhenTheMeshHasMoreCodeNodesThanTheDefaultQueryLimit()
    {
        var nsA = $"bbs{Guid.NewGuid():N}"[..12].ToLowerInvariant();
        var nsB = $"bbs{Guid.NewGuid():N}"[..12].ToLowerInvariant();
        var typeA = await SeedTypeWithSources(nsA, "TypeA");
        var typeB = await SeedTypeWithSources(nsB, "TypeB");

        // The activation path's answer — the baseline the batch must match. Also the barrier that
        // proves the seed is fully visible before discovery runs, so a short batch result below can
        // only mean truncation.
        var activationA = await ActivationPathSources(typeA, SourcesPerType)
            .Should().Within(70.Seconds()).Emit();
        var activationB = await ActivationPathSources(typeB, SourcesPerType)
            .Should().Within(70.Seconds()).Emit();
        Output.WriteLine($"activation path: {typeA}={activationA.Count} {typeB}={activationB.Count}");
        await AssertCodeNodesLiveInTheSatelliteTable(nsA);
        await AssertCodeNodesLiveInTheSatelliteTable(nsB);

        var definitions = new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
        {
            [typeA] = new NodeTypeDefinition { Configuration = "config => config", Sources = DeclaredSources },
            [typeB] = new NodeTypeDefinition { Configuration = "config => config", Sources = DeclaredSources },
        };

        var logger = Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("BatchBakeScale");
        var resolved = await NodeTypeBatchBake
            .ResolveSources(
                MeshService,
                Mesh.ServiceProvider.GetService<AccessService>(),
                definitions,
                [typeA, typeB],
                logger)
            .Should().Within(150.Seconds()).Emit();

        foreach (var (path, nodes) in resolved)
            Output.WriteLine($"batch discovery: {path}={nodes.Count}");

        // Every node, for every type — the headline claim. `>= SourcesPerType` would pass on a
        // truncated pass that happened to favour one partition, so the count is asserted exactly.
        resolved[typeA].Should().HaveCount(SourcesPerType,
            "the batched discovery must resolve TypeA's full source set — against the code before "
            + "#1216 this is 0, because the single global nodeType:Code fetch fans out over "
            + "mesh_nodes and never reaches the 'code' satellite table");
        resolved[typeB].Should().HaveCount(SourcesPerType,
            "the batched discovery must resolve TypeB's full source set, in a SECOND partition — "
            + "the cross-schema fan-out is where both #1216 mechanisms bite");

        // …and the same set the activation path resolves, node for node. This is the property the
        // batch driver exists to preserve: it is a faster route to the SAME compile, never a
        // different one.
        resolved[typeA].Select(n => n.Path).Should().Equal(activationA,
            "the batch must compile exactly what the activation path would have compiled");
        resolved[typeB].Select(n => n.Path).Should().Equal(activationB,
            "the batch must compile exactly what the activation path would have compiled");
    }
}
