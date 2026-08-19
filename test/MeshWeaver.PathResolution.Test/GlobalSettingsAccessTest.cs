using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PathResolution.Test;

/// <summary>
/// The global-settings page must be READABLE by an ordinary signed-in user, not only routable.
///
/// <para>#1817 had two halves. The first — every in-app link hand-writing the wrong path — is fixed
/// and guarded elsewhere (<c>GlobalSettingsNavigationRouteTest</c>,
/// <c>GlobalSettingsRouteLiteralGuard</c>). This pins the second, which was INVISIBLE until the
/// first landed: while every link 404'd with "does not match any registered address pattern",
/// nobody ever reached the node to be denied by it. Correcting the route turned that into
/// <i>"Access denied: user 'sglauser' lacks Read permission on '_Setting'"</i> on a live portal —
/// verified on a local 8443 install running the routing fix without this one.</para>
///
/// <para><c>_Setting</c> is a top-level node, so it is its own partition, and a partition with no
/// policy is private. Same class as #126, where the <c>Skill</c> partition shipped without its
/// PublicRead policy and platform skills were invisible after deployment.</para>
///
/// <para>🚨 This class deliberately does NOT seed <c>TestUsers.PublicAdminAccess()</c>. The default
/// <c>ConfigureMesh</c> grants <b>Public → Admin</b> at the root and every default partition, so in
/// the standard test mesh every identity is an administrator and an access assertion cannot fail —
/// it would be green while the live portal denies. Access is only genuinely evaluated with that
/// seed omitted, which is what <c>ConfigureMeshBase</c> gives. An earlier draft of this test used
/// the default base and passed against the unfixed code.</para>
/// </summary>
public class GlobalSettingsAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder);

    [Fact(Timeout = 10000)]
    public async Task GlobalSettingsNode_IsReadableByAnOrdinaryAuthenticatedUser()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // A plain signed-in user: authenticated, no Admin role. The test base's default identity is
        // an admin — exactly the identity that would NOT have caught this.
        access.SetCircuitContext(new AccessContext
        {
            ObjectId = "Samuel",
            Name = "Samuel",
            Email = "samuel@meshweaver.io",
            Roles = [],
        });

        try
        {
            var node = await ReadNode(GlobalSettingsNodeType.SettingsPath).Should().Emit();

            node.Should().NotBeNull(
                "the global-settings page is ungated — an ordinary signed-in user opens About and "
                + "What's New from the profile menu and the build chip; per-tab gating is what "
                + "restricts the admin tabs, not the page shell");
        }
        finally
        {
            access.SetCircuitContext(TestUsers.Admin);
        }
    }
}
