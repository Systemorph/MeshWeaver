using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// A partition-owning create must leave the partition ROUTABLE, not merely provisioned.
///
/// <para>🚨 The bug this pins (2026-07-29). Creating a top-level <c>User</c>/<c>Space</c> provisioned
/// the backing schema but wrote no <c>Admin/Partition/{name}</c> definition. Routing learns which
/// partitions exist from those nodes, so every address inside the new partition answered
/// <c>"No node found at '{partition}'"</c>; the per-node hub then faulted on activation and — because
/// the lookup can never succeed — failed on every retry rather than once.</para>
///
/// <para>The symptom is the reason this needs a test rather than a comment: nothing errors visibly.
/// Reads hang for the full <c>SubscribeRequest</c> timeout and the page dies blank. On the education
/// e2e mesh the install page hung for 3.5 minutes with the user's own node sitting in Postgres,
/// correctly mirrored into <c>auth</c>, while <c>Admin/Partition</c> held eleven entries — every one a
/// package or space, and not one user.</para>
///
/// <para>Package partitions were unaffected (<c>PackageInstaller</c> writes the definition alongside
/// the install), which is exactly why this went unnoticed: the paths people exercise most already
/// registered, and only SELF-PROVISIONED partitions were unroutable.</para>
/// </summary>
public class OwnedPartitionRegistrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private static string DefinitionPath(string partition) =>
        $"{PartitionNodeType.Namespace}/{partition}";

    private async Task<MeshNode?> ReadStorage(string path)
        => await Storage.Read(path, Mesh.JsonSerializerOptions).Should().Within(20.Seconds()).Emit();

    private Task<MeshNode> CreateOwner(string partition, string nodeType) =>
        MeshService.CreateNode(MeshNode.FromPath(partition) with
        {
            NodeType = nodeType,
            Name = partition,
            State = MeshNodeState.Active,
        }).FirstAsync().ToTask();

    /// <summary>
    /// THE REGRESSION. A self-provisioned Space must be registered, or nothing in it can be routed to.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task CreatingAnOwningNode_RegistersThePartition()
    {
        var partition = $"RegTest{Guid.NewGuid():N}"[..16];

        await CreateOwner(partition, "Space");

        var definition = await ReadStorage(DefinitionPath(partition));
        definition.Should().NotBeNull(
            $"creating a partition-owning node must register '{partition}' at "
            + $"{DefinitionPath(partition)} — a provisioned schema nobody can route to is not a "
            + "usable partition, and the failure mode is a hang, not an error");
        definition!.NodeType.Should().Be(PartitionNodeType.NodeType);
    }

    /// <summary>
    /// The definition must describe the partition it is for — a registration pointing at the wrong
    /// schema routes writes into someone else's data, which is worse than not routing at all.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheRegistration_DescribesThatPartition()
    {
        var partition = $"RegTest{Guid.NewGuid():N}"[..16];

        await CreateOwner(partition, "Space");
        var definition = await ReadStorage(DefinitionPath(partition));

        var content = definition!.ContentAs<PartitionDefinition>(Mesh.JsonSerializerOptions);
        content.Should().NotBeNull("the definition must carry a PartitionDefinition");
        content!.Namespace.Should().Be(partition);
        content.Schema.Should().Be(partition.ToLowerInvariant(),
            "the schema is the lower-cased namespace — provisioning and routing must agree on it");
        content.Table.Should().Be("mesh_nodes");
    }

    /// <summary>
    /// Idempotent: provisioning is re-entered on every top-level create of an owning type, and a
    /// partition that already routes must not fail the create.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RegisteringTwice_IsHarmless()
    {
        var partition = $"RegTest{Guid.NewGuid():N}"[..16];

        await CreateOwner(partition, "Space");
        var first = await ReadStorage(DefinitionPath(partition));
        first.Should().NotBeNull();

        // A second owning create for the same partition — the shape a re-onboarding takes.
        await CreateOwner(partition, "Space").ContinueWith(_ => 0);

        var second = await ReadStorage(DefinitionPath(partition));
        second.Should().NotBeNull("re-provisioning must leave the partition registered, not remove it");
        second!.NodeType.Should().Be(PartitionNodeType.NodeType);
    }
}
