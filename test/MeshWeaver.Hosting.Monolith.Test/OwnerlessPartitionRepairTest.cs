using System.Reactive.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// OWNERLESS-PARTITION REPAIR (#638). A partition whose root exists while <c>{partition}/_Access</c>
/// is completely EMPTY is the residue of a create that wrote the row and never recorded ownership.
/// It denies everyone — and the first person it denies is the user whose grant went missing, which
/// is why gating the self-heal on that user's own <c>Create</c> permission made the residue
/// repairable by a platform admin ONLY.
///
/// <para>🚨 This class deliberately configures the mesh WITHOUT
/// <c>TestUsers.PublicAdminAccess()</c> (i.e. <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>
/// rather than the default): that fixture grants <c>Public</c> root Admin, so every identity would
/// be an admin and the whole scenario — a user who holds nothing — could not exist. The
/// DevLogin identity keeps its own root grant, which is what the test's SETUP runs as.</para>
/// </summary>
public class OwnerlessPartitionRepairTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The partition's owner: a plain user holding NOTHING — no role, no grant, anywhere.</summary>
    private const string Owner = "alice";

    /// <summary>Someone else entirely — also holding nothing.</summary>
    private const string Stranger = "mallory";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static string GrantPath(string partition, string subject) => $"{partition}/_Access/{subject}_Access";

    private async Task<MeshNode?> ReadStorage(string path)
        => await Storage.Read(path, Mesh.JsonSerializerOptions).Should().Within(15.Seconds()).Emit();

    private Task<MeshNode> CreateChild(string path)
        => MeshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = path.Split('/')[^1],
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();

    /// <summary>
    /// The repair restores the ORIGINAL creator's grant, so the owner of a partition broken by a
    /// half-completed create gets it back — WITHOUT holding any permission that could authorize
    /// the repair, because there is none to hold.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OwnerlessPartition_RestoresTheOriginalCreatorsGrant_ForANonAdminOwner()
    {
        const string partition = "OwnerlessOwner";
        await PlantOwnerlessPartition(partition, Owner);

        // Alice writes into her own broken partition. That first write is still refused (at the
        // moment it is validated she has no grant) — what this pins is that the bootstrap repaired
        // the ownership the failed create lost.
        await AsUser(Owner, () => TryCreateChild($"{partition}/page1"));

        var grant = await ReadStorage(GrantPath(partition, Owner));
        grant.Should().NotBeNull("a partition with no grants at all must get its creator's grant back");
        grant!.NodeType.Should().Be("AccessAssignment");

        // …and that it is real access, not just a row: once the permission fold carries the
        // restored grant, Alice's next write into her partition lands.
        await Mesh.GetEffectivePermissions(partition, Owner)
            .Should().Within(30.Seconds()).Match(p => p.HasFlag(Permission.Create));
        await AsUser(Owner, () => CreateChild($"{partition}/page2"));
    }

    /// <summary>
    /// The repair must never become "grant the caller what they asked for": a stranger writing
    /// into an ownerless partition gets NOTHING — only the root's original creator is restored,
    /// and the stranger's own write stays refused.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OwnerlessPartition_AStrangerGetsNothing_OnlyTheOriginalCreatorIsRestored()
    {
        const string partition = "OwnerlessStranger";
        await PlantOwnerlessPartition(partition, Owner);

        var refused = await AsUser(Stranger, () => TryCreateChild($"{partition}/page1"));
        refused.Should().NotBeNull("a stranger must still be refused on a partition they were never granted");

        (await ReadStorage(GrantPath(partition, Stranger))).Should().BeNull(
            "the repair restores the partition's OWNER — never the caller");
        (await ReadStorage(GrantPath(partition, Owner))).Should().NotBeNull(
            "whoever touches it, the grant that comes back is the original creator's");
    }

    /// <summary>
    /// Plants the exact #638 residue: a usable partition root created by <paramref name="owner"/>
    /// with NO access grant anywhere under it (the row landed, the grant never did). Written
    /// straight to storage, because the ordinary create path would grant the creator Admin — the
    /// very thing that is missing here.
    /// </summary>
    private async Task PlantOwnerlessPartition(string partition, string owner)
    {
        await Storage.Write(
                new MeshNode(partition)
                {
                    NodeType = "Space",
                    Name = partition,
                    State = MeshNodeState.Active,
                    CreatedBy = owner,
                    CreatedDate = DateTimeOffset.UtcNow,
                    Version = 1,
                },
                Mesh.JsonSerializerOptions)
            .Should().Within(15.Seconds()).Emit();

        (await ReadStorage(GrantPath(partition, owner))).Should().BeNull(
            "pre-condition: the partition carries no grant at all — the #638 residue");
    }

    /// <summary>Creates a child and returns the refusal, or <c>null</c> when it succeeded.</summary>
    private async Task<Exception?> TryCreateChild(string path)
    {
        try
        {
            await CreateChild(path);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> under <paramref name="userId"/>'s identity and restores the
    /// DevLogin admin afterwards — the standard per-user shape (see PartitionWriteGuardTest).
    /// </summary>
    private async Task<T> AsUser<T>(string userId, Func<Task<T>> action)
    {
        var ctx = new AccessContext { ObjectId = userId, Name = userId };
        Access.SetContext(ctx);
        Access.SetHostIdentity(ctx);
        try
        {
            return await action();
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }
}
