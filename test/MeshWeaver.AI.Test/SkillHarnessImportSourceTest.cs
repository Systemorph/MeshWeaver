using System.Collections.Generic;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Covers the regression where chat <c>/skills</c> and the harness catalog were invisible on the
/// distributed/PG path: the built-in skills + harnesses were registered ONLY as in-memory
/// <see cref="IStaticNodeProvider"/>s (which Orleans routing never consults), with NO
/// <see cref="IStaticRepoSource"/> to materialize them into the DB partition on boot. So
/// <c>namespace:Skill</c> / <c>namespace:Harness</c> queries returned nothing → the chat found no
/// skills and the harness picker spun forever.
/// </summary>
public class SkillHarnessImportSourceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Default (un-synced) AI registration — the monolith path keeps the in-memory providers, so the
    // BuiltInSkill/HarnessProvider singletons resolve and FindStaticNode serves the catalog.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddAI();

    [Fact(Timeout = 60000)]
    public void SkillStaticRepoSource_Emits_BuiltInSkills_UnderSkillPartition()
    {
        var source = new SkillStaticRepoSource(
            Mesh.ServiceProvider.GetRequiredService<BuiltInSkillProvider>());

        source.Partition.Should().Be("Skill");

        var nodes = source.EnumerateSourceNodes();

        // The partition's PublicRead _Policy MUST travel to the synced partition — without it the
        // partition has no read policy → its skills are unreadable → the chat finds no skills (the
        // Harness wedge, atioz 2026-06-15).
        var policy = nodes.SingleOrDefault(n => n.NodeType == "PartitionAccessPolicy");
        policy.Should().NotBeNull("the Skill partition PublicRead _Policy must be imported");
        ((MeshWeaver.Mesh.Security.PartitionAccessPolicy)policy!.Content!).PublicRead.Should().BeTrue();

        var skills = nodes.Where(n => n.NodeType == SkillNodeType.NodeType).ToList();
        skills.Select(n => n.Id).OrderBy(x => x).Should().Equal("agent", "code", "create-space", "harness", "layout-area", "maui", "model", "provider-keys");
        skills.Should().AllSatisfy(n =>
        {
            n.Namespace.Should().Be(SkillNodeType.RootNamespace);
            // The pick-spec content must arrive typed — ProjectSkills drops a skill whose Content
            // isn't a SkillDefinition, so the chat would silently lose it.
            n.Content.Should().BeOfType<SkillDefinition>();
        });
        // Ordering lives in the QUERY (data), not the GUI picker: `sort:order` makes the picker's
        // default-to-first land on the catalog head. This applies ONLY to PICK skills that carry a
        // query (agent / model / harness) — command/prompt skills (code, create-space) have no pick
        // Action and are exempt.
        skills.Select(n => (SkillDefinition)n.Content!)
            .Where(d => d.Action?.Query is not null)
            .Should().AllSatisfy(d => d.Action!.Query.Should().Contain("sort:order"));
    }

    [Fact(Timeout = 60000)]
    public void HarnessStaticRepoSource_Emits_Harnesses_AndImportsPublicReadPolicy()
    {
        var source = new HarnessStaticRepoSource(
            Mesh.ServiceProvider.GetRequiredService<BuiltInHarnessProvider>());

        source.Partition.Should().Be("Harness");

        var nodes = source.EnumerateSourceNodes();

        // The native MeshWeaver harness content node is always present.
        nodes.Should().Contain(n => n.Id == Harnesses.MeshWeaver);

        // The partition's PublicRead "_Policy" MUST be imported onto the synced DB partition. Without
        // it the partition has no read policy → Harness/MeshWeaver is unreadable → its hub init fails →
        // the composer's harness picker can't load (atioz 2026-06-15; OrleansHarnessPartitionPublicReadTest).
        var policy = nodes.SingleOrDefault(n => n.NodeType == "PartitionAccessPolicy");
        policy.Should().NotBeNull("the partition PublicRead _Policy must travel to the synced partition");
        ((MeshWeaver.Mesh.Security.PartitionAccessPolicy)policy!.Content!).PublicRead.Should().BeTrue();

        // Only OTHER "_"-governance (per-user _Access grants) is dropped — the partition policy travels.
        nodes.Should().NotContain(n => n.Id.StartsWith('_') && n.NodeType != "PartitionAccessPolicy");

        // Every NON-policy node is a typed harness content node.
        nodes.Where(n => n.NodeType != "PartitionAccessPolicy").Should().AllSatisfy(n =>
        {
            n.NodeType.Should().Be(HarnessNodeType.NodeType);
            n.Content.Should().BeOfType<Harness>();
        });
    }

    [Fact(Timeout = 60000)]
    public void UnsyncedPartitions_AreServedInMemory()
    {
        // Monolith / un-synced deploys keep the in-memory provider — the catalog is FindStaticNode-
        // discoverable so the chat works WITHOUT a DB import. (The synced path is the gate test below.)
        Mesh.ServiceProvider.FindStaticNode("Skill/agent").Should().NotBeNull();
        Mesh.ServiceProvider.FindStaticNode($"Harness/{Harnesses.MeshWeaver}").Should().NotBeNull();
    }
}

/// <summary>
/// The sync gate for the Skill + Harness partitions, mirroring <see cref="AgentPartitionSyncGateTest"/>:
/// when a partition is DB-synced (static-repo import), its in-memory <see cref="IStaticNodeProvider"/>
/// MUST be skipped — otherwise it shadows Postgres and the importer's inner <c>CreateNode</c> collides
/// ("Node already exists"), so the partition never materializes and the chat finds no skills / the
/// harness picker stays empty.
/// </summary>
public class SkillHarnessPartitionSyncGateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        // Both partitions DB-synced — the gate must drop their in-memory static surfaces.
        base.ConfigureMesh(builder)
            .AddAI(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Skill", "Harness" });

    [Fact(Timeout = 60000)]
    public void SyncedSkillAndHarnessPartitions_AreNotShadowedByInMemoryStaticNodes()
    {
        // RED before the gate: AddSkillType/AddHarnessType registered the IStaticNodeProvider
        // unconditionally, so the importer couldn't materialize them and PG served nothing.
        Mesh.ServiceProvider.FindStaticNode("Skill/agent").Should().BeNull(
            "a DB-synced Skill partition must not also register its skills as in-memory static nodes");
        Mesh.ServiceProvider.FindStaticNode($"Harness/{Harnesses.MeshWeaver}").Should().BeNull(
            "a DB-synced Harness partition must not also register its harnesses as in-memory static nodes");
    }
}
