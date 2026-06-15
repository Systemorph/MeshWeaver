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
/// Covers the regression where chat <c>/commands</c> and the harness catalog were invisible on the
/// distributed/PG path: the built-in commands + harnesses were registered ONLY as in-memory
/// <see cref="IStaticNodeProvider"/>s (which Orleans routing never consults), with NO
/// <see cref="IStaticRepoSource"/> to materialize them into the DB partition on boot — unlike
/// Agent/Model/Doc. So <c>namespace:Command</c> / <c>namespace:Harness</c> queries returned nothing
/// → the chat found no commands and the harness picker spun forever.
///
/// <para>This verifies the import SOURCES exist and emit the right nodes; the importer→PG
/// round-trip itself is covered generically by <c>StaticRepoImporterTests</c> (any IStaticRepoSource
/// materializes + becomes queryable).</para>
/// </summary>
public class CommandHarnessImportSourceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Default (un-synced) AI registration — the monolith path keeps the in-memory providers, so the
    // BuiltInCommand/HarnessProvider singletons resolve and FindStaticNode serves the catalog.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddAI();

    [Fact(Timeout = 60000)]
    public void CommandStaticRepoSource_Emits_BuiltInCommands_UnderCommandPartition()
    {
        var source = new CommandStaticRepoSource(
            Mesh.ServiceProvider.GetRequiredService<BuiltInCommandProvider>());

        source.Partition.Should().Be("Command");

        var commands = source.EnumerateSourceNodes();
        commands.Select(n => n.Id).OrderBy(x => x).Should().Equal("agent", "harness", "model");
        commands.Should().AllSatisfy(n =>
        {
            n.NodeType.Should().Be(CommandNodeType.NodeType);
            n.Namespace.Should().Be(CommandNodeType.RootNamespace);
            // The pick-spec content must arrive typed — ProjectCommands drops a command whose
            // Content isn't a CommandDefinition, so the chat would silently lose it.
            n.Content.Should().BeOfType<CommandDefinition>();
            // Ordering lives in the QUERY (data), not the GUI picker: `sort:order` makes the
            // picker's default-to-first land on the catalog head (Assistant's order:-1 for agents).
            ((CommandDefinition)n.Content!).Query.Should().Contain("sort:order");
        });
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
        Mesh.ServiceProvider.FindStaticNode("Command/agent").Should().NotBeNull();
        Mesh.ServiceProvider.FindStaticNode($"Harness/{Harnesses.MeshWeaver}").Should().NotBeNull();
    }
}

/// <summary>
/// The sync gate for the Command + Harness partitions, mirroring
/// <see cref="AgentPartitionSyncGateTest"/>: when a partition is DB-synced (static-repo import), its
/// in-memory <see cref="IStaticNodeProvider"/> MUST be skipped — otherwise it shadows Postgres and
/// the importer's inner <c>CreateNode</c> collides ("Node already exists"), so the partition never
/// materializes into the DB and the chat finds no commands / the harness picker stays empty.
/// </summary>
public class CommandHarnessPartitionSyncGateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        // Both partitions DB-synced — the gate must drop their in-memory static surfaces.
        base.ConfigureMesh(builder)
            .AddAI(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Command", "Harness" });

    [Fact(Timeout = 60000)]
    public void SyncedCommandAndHarnessPartitions_AreNotShadowedByInMemoryStaticNodes()
    {
        // RED before the gate: AddCommandType/AddHarnessType registered the IStaticNodeProvider
        // unconditionally, so the importer couldn't materialize them and PG served nothing.
        Mesh.ServiceProvider.FindStaticNode("Command/agent").Should().BeNull(
            "a DB-synced Command partition must not also register its commands as in-memory static nodes");
        Mesh.ServiceProvider.FindStaticNode($"Harness/{Harnesses.MeshWeaver}").Should().BeNull(
            "a DB-synced Harness partition must not also register its harnesses as in-memory static nodes");
    }
}
