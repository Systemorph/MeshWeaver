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
using MeshWeaver.Hosting.Persistence.Query;
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

    private IObservable<IReadOnlyList<string>> ActivationPathSources(
        string typePath, int expected, IReadOnlyList<string>? declaredSources = null)
    {
        var queries = CodeQueryResolver
            .ExpandAll(declaredSources ?? DeclaredSources, CodeQueryResolver.DefaultSources, typePath)
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

    /// <summary>
    /// 🚨 THE REGRESSION TEST FOR THE RESIDUAL HALF OF #1216 — the one that stalled the
    /// memex-cloud rollout on ci.2969 after the first two mechanisms were fixed.
    ///
    /// <para><b>The production shape, reproduced exactly.</b> A NodeType in partition A declares a
    /// cross-partition shared source in partition B — <c>shared=@{B}/SampleData/Source/Fixtures</c>
    /// — and the Code node it needs lives one level BELOW that partition's <c>Source</c> folder, at
    /// <c>{B}/SampleData/Source/Fixtures/MtplClaimFixtures</c>. Four types failed exactly this way
    /// (<c>ReinsuranceDemo/SampleData</c>, <c>Claims/Claim</c>, <c>Claims/Enquiry</c>,
    /// <c>Claims/Register</c>), all on the same missing symbol:
    /// <c>CS0103: The name 'MtplClaimFixtures' does not exist in the current context</c>.</para>
    ///
    /// <para><b>Why the earlier invariant could not catch it.</b> Those types resolve PLENTY of
    /// other sources — their own <c>Source</c> subtree and the shared folder's direct children — so
    /// the set is PARTIAL, never empty, and #1220's emptiness invariant sees a healthy type. The
    /// partial set reaches Roslyn and produces a compile error indistinguishable from real content
    /// breakage, which the bake gate reads as an image regression and refuses readiness on.</para>
    ///
    /// <para><b>The mechanism</b> is <c>QueryParser</c>: a WILDCARD <c>namespace:*/Source</c> cannot
    /// become a Path, so it is emitted as a <c>namespace LIKE '%/Source'</c> filter — and
    /// <c>scope:subtree</c>, which only ever drives a PATH walk, was silently dropped. The batch's
    /// global fetch (<c>NodeTypeBatchBake.GlobalCodeQueries</c>) is that query, and it is the only
    /// shape that reaches the per-schema <c>code</c> satellite table across partitions, so a Code
    /// node whose namespace does not END in <c>/Source</c> was invisible to the whole pass.</para>
    ///
    /// <para>The assertion is on CONTAINMENT of the nested node rather than a count, because that
    /// is the production symptom: one missing file out of many present ones.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task Discovery_ResolvesACrossPartitionSharedSource_NestedBelowTheSourceFolder()
    {
        var nsConsumer = $"bbn{Guid.NewGuid():N}"[..12].ToLowerInvariant();
        var nsShared = $"bbn{Guid.NewGuid():N}"[..12].ToLowerInvariant();
        await ProvisionPartition(nsConsumer).Should().Within(60.Seconds()).Emit();
        await ProvisionPartition(nsShared).Should().Within(60.Seconds()).Emit();

        // The consumer type's OWN source — the reason its resolved set is never empty.
        var typePath = $"{nsConsumer}/SampleData";
        await MeshService.CreateNode(new MeshNode("Spine", $"{typePath}/Source")
        {
            Name = "Spine",
            NodeType = CodeNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Code = "public static class Spine { }" }
        }).Should().Within(30.Seconds()).Emit();

        // The shared partition: one Code node DIRECTLY under Source (namespace ends in `/Source`,
        // so the unfixed global fetch DOES see it — this is what keeps the set non-empty and hides
        // the defect from the emptiness invariant) …
        var sharedRoot = $"{nsShared}/SampleData/Source";
        await MeshService.CreateNode(new MeshNode("Shared", sharedRoot)
        {
            Name = "Shared",
            NodeType = CodeNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Code = "public static class Shared { }" }
        }).Should().Within(30.Seconds()).Emit();

        // … and the one that was missing in production: nested ONE level deeper, so its namespace
        // is `{shared}/SampleData/Source/Fixtures` — which `namespace LIKE '%/Source'` never matches.
        var nestedPath = $"{sharedRoot}/Fixtures/MtplClaimFixtures";
        await MeshService.CreateNode(new MeshNode("MtplClaimFixtures", $"{sharedRoot}/Fixtures")
        {
            Name = "MtplClaimFixtures",
            NodeType = CodeNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Code = "public static class MtplClaimFixtures { public const int N = 1; }"
            }
        }).Should().Within(30.Seconds()).Emit();

        // The declared sources of the production types, with the partition names substituted:
        // own subtree + the cross-partition shared folder(s). `@` expands to `path:X` + a
        // `namespace:X scope:subtree` folder match, both of which the in-memory matcher mirrors —
        // so BOTH are served from the global fetch, which is exactly why the fetch has to be
        // complete.
        var declaredSources = new[]
        {
            "namespace:Source scope:subtree",
            $"shared=@{sharedRoot}",           // the Claims/Claim shape — resolves the direct child
            $"shared=@{sharedRoot}/Fixtures",  // the ReinsuranceDemo/SampleData shape — the nested one
        };

        // Barrier: the activation path (each source query issued individually, partition-pinned)
        // is the baseline, and it also proves every seeded node is visible before discovery runs —
        // so a node missing from the batch below can only be a discovery defect, never a race.
        var activation = await ActivationPathSources(typePath, 3, declaredSources)
            .Should().Within(70.Seconds()).Emit();
        activation.Should().Contain(nestedPath,
            "the activation path issues `namespace:{shared}/SampleData/Source scope:subtree` PINNED "
            + "to the shared partition, where scope:subtree drives a real path walk — this is the "
            + "answer the batched pass has to match");

        var definitions = new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
        {
            [typePath] = new NodeTypeDefinition { Configuration = "config => config", Sources = declaredSources },
        };

        var logger = Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("BatchBakeNested");
        var resolved = await NodeTypeBatchBake
            .ResolveSources(
                MeshService,
                Mesh.ServiceProvider.GetService<AccessService>(),
                definitions,
                [typePath],
                logger)
            .Should().Within(150.Seconds()).Emit();

        var paths = resolved[typePath].Select(n => n.Path).ToList();
        Output.WriteLine($"batch discovery: {typePath}={paths.Count} [{string.Join(", ", paths)}]");

        paths.Should().Contain(nestedPath,
            "the batch's global fetch is `namespace:*/Source scope:subtree nodeType:Code`, and the "
            + "wildcard form emits a `namespace LIKE '%/Source'` FILTER — scope:subtree drives a "
            + "PATH walk and there is no path here, so before the fix it was silently dropped and "
            + "this node (namespace `.../Source/Fixtures`) was invisible to the entire pass");
        paths.Should().Equal(activation,
            "the batch must resolve exactly what the activation path resolves — a PARTIAL set is "
            + "the failure mode this test exists for: it compiles, and the CS0103 it produces reads "
            + "as broken content rather than as broken discovery");
    }

    /// <summary>
    /// 🚨 THE CROSS-PATH CONTRACT FOR #1235: ONE query string, TWO evaluation paths, IDENTICAL
    /// answers. The same <c>namespace:*/Source scope:subtree nodeType:Code</c> is run through
    /// parser → POSTGRES and through parser → the IN-MEMORY <see cref="QueryEvaluator"/>, and the
    /// two result sets are compared node-for-node.
    ///
    /// <para><b>Why this shape, and not a unit test of either evaluator.</b> The defect was never
    /// inside one evaluator — it was a DISAGREEMENT between the producer and one of its consumers.
    /// <c>QueryParser</c> emitted a wildcard namespace as SQL's <c>%</c> (the only <c>Like</c>
    /// filter in the language that did), which the SQL generators re-derived anyway from <c>*</c>,
    /// while <c>QueryEvaluator.CompareWildcard</c> understood only <c>*</c> and therefore fell
    /// through to an equality test against the literal <c>"%/Source"</c>. Postgres answered
    /// correctly; memory answered EMPTY. A unit test of either side alone passes with the bug
    /// present — Postgres because it is right, the evaluator because a hand-written test would
    /// naturally have used the <c>*</c> spelling the parser does not produce. Only running the
    /// same string down both paths can see the fork.</para>
    ///
    /// <para><b>What depends on the in-memory answer.</b> The Postgres fan-out's live relevance
    /// gate (<c>PostgreSqlPartitionedMeshQuery</c> evaluates a change notification's payload
    /// in-memory to decide whether a NEW row could enter the query), the storage-adapter and
    /// static-node providers, and the SQLite vector query. All of them served EMPTY for a wildcard
    /// namespace.</para>
    ///
    /// <para>The equality is asserted against an explicit expected set as well, so a future
    /// regression that breaks BOTH paths the same way cannot pass as "they agree".</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task WildcardNamespaceSubtree_AnswersIdentically_ThroughPostgresAndInMemory()
    {
        const string Query = "namespace:*/Source scope:subtree nodeType:Code";

        var ns = $"bbw{Guid.NewGuid():N}"[..12].ToLowerInvariant();
        await ProvisionPartition(ns).Should().Within(60.Seconds()).Emit();

        // (namespace, id) — chosen so every arm of the widened filter is exercised, plus decoys
        // that both paths must reject. `*/Source` covers the first; only the two-wildcard
        // `*/Source/*` covers the nested pair; the decoys have no `Source` SEGMENT at all.
        var seed = new (string Namespace, string Id, bool Expected)[]
        {
            ($"{ns}/Model/Source", "Direct", true),
            ($"{ns}/Model/Source/Fixtures", "Nested", true),
            ($"{ns}/Model/Source/Fixtures/Deep", "Deeper", true),
            ($"{ns}/Model/Other", "Decoy", false),
            // 'Sources' is a different segment — the literal after the wildcard matches verbatim.
            ($"{ns}/Model/Sources", "SourcesDecoy", false),
        };

        var nodes = seed
            .Select(s => new MeshNode(s.Id, s.Namespace)
            {
                Name = s.Id,
                NodeType = CodeNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new CodeConfiguration { Code = $"public static class {s.Id} {{ }}" }
            })
            .ToList();
        foreach (var node in nodes)
            await MeshService.CreateNode(node).Should().Within(30.Seconds()).Emit();

        var expected = seed.Where(s => s.Expected)
            .Select(s => $"{s.Namespace}/{s.Id}")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        // ── Path 1: parser → Postgres. Retried until the seeded rows are visible; that is a
        // propagation barrier for the WRITES, not for the defect (Postgres always answered this
        // query correctly — it is the in-memory side that returned nothing).
        var fromPostgres = await Observable.Using(
                () => AccessContextScope.AsSystem(Mesh.ServiceProvider.GetService<AccessService>()),
                _ => Observable.Interval(TimeSpan.FromMilliseconds(500))
                    .StartWith(0L)
                    .SelectMany(_ => Observable
                        .Defer(() => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(Query)))
                        .Scan(
                            ImmutableDictionary<string, MeshNode>.Empty
                                .WithComparers(StringComparer.OrdinalIgnoreCase),
                            ApplyChange)
                        .Throttle(TimeSpan.FromSeconds(1))
                        .Take(1))
                    .Select(map => (IReadOnlyList<string>)map.Keys
                        .Where(p => p.StartsWith($"{ns}/", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .ToList())
                    .Do(set => Output.WriteLine($"postgres: {set.Count} [{string.Join(", ", set)}]"))
                    .Where(set => set.Count >= expected.Count)
                    .FirstAsync())
            .Should().Within(90.Seconds()).Emit();

        // ── Path 2: parser → in-memory. The SAME string, no Postgres involved. Deterministic and
        // synchronous — a wildcard namespace produces no ParsedQuery.Path, so the whole query IS
        // the filter and QueryEvaluator.Matches is the complete in-memory semantics for it.
        var parsed = new QueryParser().Parse(Query);
        var evaluator = new QueryEvaluator();
        var fromMemory = nodes
            .Where(n => evaluator.Matches(n, parsed))
            .Select(n => n.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Output.WriteLine($"in-memory: {fromMemory.Count} [{string.Join(", ", fromMemory)}]");

        fromMemory.Should().Equal(expected,
            "the in-memory evaluator must answer the wildcard-namespace subtree query itself — "
            + "before the fix it returned NOTHING, because the parser handed it a `%`-spelled "
            + "pattern it could only compare for literal equality (#1235)");
        fromPostgres.Should().Equal(expected,
            "Postgres was always the correct side; if this drifts, the widening (#1232) regressed");
        fromMemory.Should().Equal(fromPostgres,
            "ONE query string must mean ONE thing. The parser, the SQL generators and the in-memory "
            + "evaluators share a single wildcard vocabulary (`*`); the moment any of them spells it "
            + "differently, a query silently answers two different questions depending on which "
            + "backend happens to serve it");
    }

    /// <summary>
    /// The batch bake's THIRD in-memory matcher is <see cref="CodeQueryResolver.Matches"/>, and it
    /// is not a query evaluator at all — it compares locations with a literal
    /// <c>path.StartsWith(ns + "/")</c> and has no glob whatsoever. So a per-type source query
    /// carrying a WILDCARD location must never be classified as servable from the global map:
    /// the matcher would answer "no match" for every node and the type would compile against a
    /// silently partial (or empty) source set — #1216's failure shape once more.
    ///
    /// <para>This is the invariant that keeps #1238 (batch bake default-on) safe. Today no declared
    /// source query contains a <c>*</c> — <c>CodeQueryResolver.Expand</c> only ever produces
    /// concrete locations, from <c>$self</c> rebasing or a <c>shared=@Concrete/Path</c> — so the
    /// hazard is latent rather than live. <c>IsInMemoryMatchable</c> is the gate whose whole purpose
    /// is to admit only "shapes it provably mirrors", so the wildcard belongs on the exotic side of
    /// it, where the query runs against the mesh and the wildcard is genuinely understood.</para>
    /// </summary>
    [Theory]
    // Concrete locations — the shapes Expand actually produces — stay matchable.
    [InlineData("namespace:Acme/Model/Source scope:subtree nodeType:Code", true)]
    [InlineData("path:Acme/Model/Source/Spine nodeType:Code", true)]
    // Wildcards must fall through to an individual mesh query.
    [InlineData("namespace:*/Source scope:subtree nodeType:Code", false)]
    [InlineData("path:Acme/*/Source nodeType:Code", false)]
    public void IsInMemoryMatchable_RejectsWildcardLocations_BecauseTheMatcherHasNoGlob(
        string query, bool expected)
    {
        NodeTypeBatchBake.IsInMemoryMatchable(query).Should().Be(expected);
    }
}
