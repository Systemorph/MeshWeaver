using System;
using System.Collections.Generic;
using MeshWeaver.AI;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Regression for the atioz 2026-06-11 secondary finding (surfaced by the import-hub fix's per-file
/// activity logging): a partition that is BOTH sync-enabled (<c>Features:StaticRepoSync</c>) AND
/// served by an in-memory <see cref="IStaticNodeProvider"/> could never MATERIALIZE into the DB.
///
/// <para>The built-in agents are registered as an <c>IStaticNodeProvider</c>
/// (<see cref="BuiltInAgentProvider"/>) which feeds <c>serviceProvider.FindStaticNode(path)</c>. The
/// importer upserts each source node via <c>CreateOrUpdateNodeRequest</c>; persistence is empty →
/// inner <c>CreateNodeRequest</c> → its <c>FindStaticNode</c> fallback finds the built-in agent →
/// fails <c>"Node already exists at path: Agent/X"</c>. On atioz <c>Agent</c> imported 4 / FAILED 8
/// (<c>ImportedWithErrors</c>) while <c>Doc</c> — which has no <c>IStaticNodeProvider</c> — imported
/// 161/0.</para>
///
/// <para>The fix gates the <c>IStaticNodeProvider</c> registration on <c>!dbSynced</c> in
/// <c>AddAgentType</c>/<c>AddLanguageModelType</c> (the prior state gated only the
/// <c>IPartitionStorageProvider</c>). This test exercises the REAL prod entry
/// <c>AddAI(serveFromPartition)</c> — called ONCE with the sync set in production — and asserts the
/// gate directly: when the partition is DB-synced, its agents are NOT FindStaticNode-discoverable,
/// so the importer materializes them (served from PG) instead of colliding. (End-to-end
/// materialization needs a PG backend — covered by <c>OrleansStaticRepoImportTest</c> + atioz; the
/// monolith has no PG to back a synced partition, so this verifies the gate, not the write.)</para>
/// </summary>
public class AgentPartitionSyncGateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SampleAgentPath = "Agent/ThreadNamer";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        // The "Agent" partition is DB-synced — the gate must drop the in-memory IStaticNodeProvider.
        base.ConfigureMesh(builder)
            .AddAI(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Agent" });

    [Fact(Timeout = 60000)]
    public void SyncedAgentPartition_IsNotShadowedByInMemoryStaticNodes()
    {
        // 🚨 The precise gate: with the "Agent" partition DB-synced, its built-in agents must NOT be
        // FindStaticNode-discoverable — otherwise the importer's inner CreateNode refuses to
        // materialize them ("Node already exists at path: Agent/X"). RED before the gate fix, which
        // registered the IStaticNodeProvider regardless of dbSynced (only the IPartitionStorageProvider
        // was gated). The BuiltInAgentProvider singleton + the "Agent" NodeType def still resolve.
        Mesh.ServiceProvider.FindStaticNode(SampleAgentPath).Should().BeNull(
            "a DB-synced Agent partition must not also register its agents as in-memory static nodes");
    }
}
