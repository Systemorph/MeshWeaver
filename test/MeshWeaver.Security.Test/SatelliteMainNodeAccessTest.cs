using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Pins the access rule the read gate relies on after the side-panel-spinner fix:
/// <b>access rights are defined on the main node, and whoever can Read the main
/// node can Read every satellite under it</b> — including a satellite / cell
/// sub-path that has no MeshNode (and therefore no hub) of its own.
///
/// <para>The mechanism is the <c>PermissionEvaluator</c> scope walk: evaluating
/// <c>GetEffectivePermissions({user}/_Thread/{threadId}/{messageId})</c> visits
/// every scope from the root down through the partition (the main node) to the
/// leaf, so the partition owner is granted Read on the whole subtree without a
/// per-satellite grant. <see cref="MeshWeaver.Hosting.MeshNodeStreamCache"/>'s
/// read gate now evaluates exactly this LOCALLY instead of posting a
/// <c>GetPermissionRequest</c> to the leaf path's hub — which, for a non-existent
/// sub-path, never activates and blocked the subscription for the full timeout
/// (the "thread won't open" spinner). Orleans cold-start is what made that a
/// 15s hang; the rule itself is deterministic and is what we pin here.</para>
/// </summary>
public class SatelliteMainNodeAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddRowLevelSecurity();

    /// <summary>Security tests need granular permissions — skip the blanket admin grant.</summary>
    protected override System.Threading.Tasks.Task SetupAccessRightsAsync()
        => System.Threading.Tasks.Task.CompletedTask;

    /// <summary>
    /// The partition owner has Read on a DEEP, NON-EXISTENT satellite sub-path
    /// under their partition — inherited from the main node (the partition scope)
    /// via the scope walk. This is the grant the read gate returns instead of
    /// probing the (non-existent) leaf hub.
    /// </summary>
    [Theory(Timeout = 20000)]
    [InlineData("alice/_Thread/hello-2a76")]                       // thread (3 segments)
    [InlineData("alice/_Thread/hello-2a76/278c379f")]             // message (4 segments)
    [InlineData("alice/_Thread/hello-2a76/sub-c0de/278c379f")]    // sub-thread message (5 segments)
    public async Task Owner_HasReadOnSatelliteSubPath_ViaMainNode(string satellitePath)
    {
        var perms = await Mesh.GetEffectivePermissions(satellitePath, "alice")
            .Should().Within(20.Seconds()).Emit();
        perms.HasFlag(Permission.Read).Should().BeTrue(
            "the partition owner reads every satellite under their main node " +
            "(scope walk grants via the 'alice' partition scope), even a sub-path " +
            $"that has no MeshNode of its own — '{satellitePath}'");
    }

    /// <summary>
    /// A user who is NOT the partition owner and holds no grant on the partition
    /// has NO read on the same satellite sub-path — the inheritance is from the
    /// main node's grants, not a blanket allow.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task NonOwner_HasNoReadOnSatelliteSubPath()
    {
        var perms = await Mesh.GetEffectivePermissions("alice/_Thread/hello-2a76/278c379f", "bob")
            .Should().Within(20.Seconds()).Emit();
        perms.HasFlag(Permission.Read).Should().BeFalse(
            "bob has no grant on alice's partition (the main node), so he inherits " +
            "no read on alice's thread satellites");
    }
}
