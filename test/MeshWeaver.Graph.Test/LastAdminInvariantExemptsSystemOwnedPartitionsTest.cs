using System.Reactive.Linq;
using System.Text.Json.Nodes;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The last-admin invariant does not apply to a SYSTEM-OWNED partition — and still applies to every
/// other one.
///
/// <para><b>The defect (#5140), which arrived wearing three faces.</b> A partition with a one-way
/// <c>_GitSync</c> is rewritten from its repo on every sync, so the only identity that may usefully
/// write it is the importer's. Two rules then met head-on:
/// <c>AccessAssignmentGuard.IsForbiddenOnSystemOwned</c> refuses GRANTING another Admin there, and
/// <see cref="SpaceAdminInvariantValidator"/> refused REMOVING the one that is already present. So
/// <c>SystemOwnedAccessRetractionHandler</c>'s sweep could only ever SPARE that last grant — which
/// its own documentation says it does, deliberately, rather than issuing a delete the pipeline would
/// refuse. The three reported symptoms (a retraction that "never fired", a removal that is blocked,
/// a direct write that is accepted) are one mechanism: <b>the documented end state was unreachable
/// by construction</b>. That, not any of the three, is the defect.</para>
///
/// <para><b>Why the exemption is sound rather than a relaxation.</b> The invariant exists so a space
/// always has somebody who can manage it. A system-owned space HAS one — the importer identity,
/// which is also the only writer whose edits survive a sync. The mirror-partition exemption already
/// in the validator is the same reasoning one step earlier. A partition that is neither mirror nor
/// system-owned always has a human admin (its creator is granted one at create), and this invariant
/// only ever fires while REMOVING an admin, so it cannot lock out a partition that has none.</para>
///
/// <para><b>SHOULD-FAIL-IF the exemption is widened into "the invariant is off".</b> There is a case
/// on each side, over the same partition shape and the same delete:
/// <see cref="OnASystemOwnedPartition_TheLastAdminMayGo"/> must pass and
/// <see cref="OnAnOrdinaryPartition_TheLastAdminIsStillRefused"/> must still refuse. The third,
/// <see cref="OnATwoWaySyncedPartition_TheLastAdminIsStillRefused"/>, pins the discriminator that a
/// coarser fix would lose: a BIJECTIVE sync is not system-owned, because there the mesh nodes are
/// the working copy and the people editing them must keep write access. A fix keyed on "has a
/// <c>_GitSync</c>" would pass the first two and fail that one.</para>
/// </summary>
public class LastAdminInvariantExemptsSystemOwnedPartitionsTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string AdminUser = "the-only-admin";

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private SpaceAdminInvariantValidator Validator => new(Mesh);

    /// <summary>
    /// The partition's single Admin grant, written to the store so the validator's remaining-admin
    /// count can see it and find nothing else.
    /// </summary>
    private async Task<MeshNode> TheOnlyAdminGrant(string partition)
    {
        var node = new MeshNode(AdminUser + "_Access", $"{partition}/_Access")
        {
            Name = "Admin grant",
            NodeType = "AccessAssignment",
            State = MeshNodeState.Active,
            Content = new AccessAssignment
            {
                AccessObject = AdminUser,
                Roles = [new RoleAssignment { Role = Role.Admin.Id }],
            },
        };
        await Storage.Write(node, Mesh.JsonSerializerOptions).Should().Emit();
        return node;
    }

    /// <summary>Wires <c>{partition}/_GitSync</c> with the given direction.</summary>
    private Task WireSync(string partition, bool twoWay) =>
        Storage.Write(
                new MeshNode(AccessAssignmentGuard.SyncConfigId, partition)
                {
                    Name = "Sync",
                    NodeType = "GitHubSyncConfig",
                    State = MeshNodeState.Active,
                    // A JsonObject is one of the three shapes IsTwoWay reads, and the one the node
                    // builders produce — so this exercises the same tolerance production does.
                    Content = new JsonObject
                    {
                        ["repository"] = "Systemorph/Memex",
                        ["twoWay"] = twoWay,
                    },
                },
                Mesh.JsonSerializerOptions)
            .Should().Emit();

    /// <summary>The verdict for deleting <paramref name="grant"/> on its own (the partition stays).</summary>
    private Task<NodeValidationResult> DeleteVerdict(MeshNode grant) =>
        Validator.Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Delete,
                Node = grant,
                // The grant alone is the delete root: the partition is NOT going away, which is the
                // only case the invariant guards (a partition-rooted cascade is already exempt).
                DeleteCascadeRootPath = grant.Path,
            })
            .Should().Emit();

    [Fact]
    public async Task OnASystemOwnedPartition_TheLastAdminMayGo()
    {
        const string Partition = "systemownedspace";
        var grant = await TheOnlyAdminGrant(Partition);
        await WireSync(Partition, twoWay: false);

        var verdict = await DeleteVerdict(grant);

        verdict.IsValid.Should().BeTrue(
            "the repo owns this partition, so its administrator is the importer identity and not a "
            + "person — holding the last human grant here is what made the retraction sweep unable "
            + "to converge, reason given: {0}", verdict.ErrorMessage ?? "(none)");
    }

    [Fact]
    public async Task OnAnOrdinaryPartition_TheLastAdminIsStillRefused()
    {
        const string Partition = "ordinaryspace";
        var grant = await TheOnlyAdminGrant(Partition);
        // No _GitSync at all — the ordinary Space this invariant exists for.

        var verdict = await DeleteVerdict(grant);

        verdict.IsValid.Should().BeFalse(
            "the control on the other side: an ordinary space left with no administrator cannot be "
            + "managed by anybody, and an exemption that also covered this one would be the "
            + "invariant switched off rather than scoped");
    }

    [Fact]
    public async Task OnATwoWaySyncedPartition_TheLastAdminIsStillRefused()
    {
        const string Partition = "bijectivespace";
        var grant = await TheOnlyAdminGrant(Partition);
        await WireSync(Partition, twoWay: true);

        var verdict = await DeleteVerdict(grant);

        verdict.IsValid.Should().BeFalse(
            "a bijective sync preserves and commits back server-side edits, so its mesh nodes are "
            + "somebody's working copy and it is NOT system-owned — the exemption keys on the "
            + "DIRECTION, exactly as the write boundary does, not on the presence of a sync");
    }
}
