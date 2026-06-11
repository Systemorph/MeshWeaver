using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// DISTRIBUTED repro for the atioz 2026-06-11 <b>secondary</b> finding — the one the import-hub
/// fix's per-file activity logging surfaced once the wedge was gone: a source node that is ALSO
/// registered as an in-memory <b>static</b> node (<c>AddMeshNodes</c> / the built-in
/// <c>AddAgentType</c> / <c>AddModelProviderType</c> seeds) cannot be <b>materialized into the
/// DB</b> by the static-repo import.
///
/// <para>Flow of the bug: the importer upserts via <c>CreateOrUpdateNodeRequest</c>; its
/// <c>persistence.Read</c> returns null (the node is not in the DB yet) → <c>DispatchInnerCreate</c>
/// → <c>CreateNodeRequest</c>, whose <c>FindStaticNode</c> fallback finds the in-memory static node
/// and the handler fails <c>"Node already exists at path"</c>. So <c>Agent</c>/<c>Model</c> (both
/// static-registered) report <c>ImportedWithErrors</c> while <c>Doc</c> (no static-registered nodes)
/// imports clean — exactly what the atioz activity logs showed (8 Agent + 2 Model failures).</para>
///
/// <para>This pins the cause: a static registration must NOT make the canonical upsert fail
/// "already exists" — the import must materialize the SOURCE content into the DB. RED until the
/// CreateNode/static-node interaction is fixed.</para>
/// </summary>
public class OrleansStaticRepoImportStaticBackedTest(ITestOutputHelper output)
    : OrleansTestBase<OrleansStaticRepoImportStaticBackedTest.StaticBackedConfigurator>(output)
{
    // Fresh per test-run partition so reruns don't collide on the shared in-memory store.
    internal static readonly string Partition = "StaticBackedRepo" + Guid.NewGuid().ToString("N")[..8];
    internal const string StaticBody = "STATIC PLACEHOLDER (in-memory only)";
    internal const string SourceBody = "MATERIALIZED FROM SOURCE";
    internal static readonly FakeStaticBackedSource Source = new(Partition);

    private IServiceProvider SiloServices => ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
    private IMessageHub Mesh => SiloServices.GetRequiredService<IMessageHub>();
    private CancellationToken Ct => new CancellationTokenSource(55.Seconds()).Token;

    private static string? MarkdownOf(MeshNode? n) => (n?.Content as MarkdownContent)?.Content;

    private async Task<MeshNode?> ReadWhen(string path, Func<MeshNode, bool> predicate, CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
            return await Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Where(n => n is not null && predicate(n))
                .Take(1)
                .Timeout(30.Seconds())
                .Catch((Exception _) => Observable.Return<MeshNode?>(null))
                .FirstAsync()
                .ToTask(ct);
    }

    // RED — documents an OPEN, separately-tracked bug (atioz 2026-06-11 secondary finding). Skipped
    // so it doesn't break shared CI while the proper fix is scoped. DO NOT "fix" by unblocking
    // CreateNode's "already exists" guard — that guard is load-bearing: materializing a node that
    // ALSO exists as a static node creates a static-vs-DB reconciliation conflict that HANGS (proven
    // — a one-line handler change ran this test for 2h42m before failing). The correct fix is on the
    // REGISTRATION side: a sync-enabled partition (Features:StaticRepoSync:Partitions) must NOT also
    // register its nodes as in-memory static nodes (the built-in Agent/Model seeds), so the import
    // creates them cleanly in the DB exactly as Doc does (Doc has no static-registered nodes → 161
    // imported, 0 failed; Agent/Model are static-registered → ImportedWithErrors). Remove the Skip
    // once that registration gate lands.
    [Fact(Timeout = 90000, Skip = "Open bug: sync-enabled partitions still register static nodes; fix is registration-side gating, not the CreateNode guard. See class remarks.")]
    public async Task Import_MaterializesNode_EvenWhenAlsoRegisteredAsStaticNode()
    {
        var ct = Ct;

        var results = await StaticRepoImporter.ImportAll(Mesh).ToList().FirstAsync().ToTask(ct);
        foreach (var r in results)
            Output.WriteLine($"import: partition={r.Partition} outcome={r.Outcome} count={r.Count}");

        var result = results.Single(r => r.Partition == Partition);

        // 🚨 The source node {Partition}/StaticBacked is ALSO registered as an in-memory static
        // node. The import MUST materialize it into the DB through the canonical upsert — a static
        // registration must not make CreateNode fail "Node already exists". RED today: the upsert
        // faults and the partition reports ImportedWithErrors.
        result.Outcome.Should().Be("Imported",
            "a source node that is also a static node must materialize, not fail 'already exists'");

        // …and the persisted node must carry the SOURCE content, not the static placeholder.
        var materialized = await ReadWhen($"{Partition}/StaticBacked",
            n => MarkdownOf(n)?.Contains("MATERIALIZED") == true, ct);
        materialized.Should().NotBeNull(
            "the import must persist the SOURCE content over the in-memory static placeholder");
        MarkdownOf(materialized).Should().Contain(SourceBody);
    }

    /// <summary>Source whose single child shares its path with an in-memory static node (below).</summary>
    public sealed class FakeStaticBackedSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        [
            new MeshNode("StaticBacked", partition)
            {
                NodeType = "Markdown", Name = "Static Backed", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = $"# Static Backed\n\n{SourceBody}" }
            }
        ];

        public MeshNode? PartitionRoot => new(partition)
        {
            Name = partition, NodeType = "Space", State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# {partition}\n\nwelcome" }
        };
    }

    /// <summary>Silo fixture that registers {Partition}/StaticBacked as an in-memory STATIC node —
    /// the collision that makes CreateNode's FindStaticNode fallback refuse the materialization.</summary>
    public class StaticBackedConfigurator : TestSiloConfigurator
    {
        protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
            builder
                .AddSpaceType()
                // Same path as the source node, registered as an in-memory static MeshNode. This is
                // what makes FindStaticNode(path) non-null on the create path while persistence is
                // still empty — the exact shape of the built-in Agent/Model seeds on atioz.
                .AddMeshNodes(new MeshNode("StaticBacked", Partition)
                {
                    NodeType = "Markdown", Name = "Static Backed", State = MeshNodeState.Active,
                    Content = new MarkdownContent { Content = $"# Static Backed\n\n{StaticBody}" }
                })
                .ConfigureServices(s => s.AddSingleton<IStaticRepoSource>(Source));
    }
}
