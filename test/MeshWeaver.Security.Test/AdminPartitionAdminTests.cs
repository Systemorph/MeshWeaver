using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Pins the platform-admin model: a "global / platform admin" is an admin on the ADMIN
/// PARTITION — <see cref="Permission.All"/> at scope <c>Admin</c> (an <c>AccessAssignment</c>
/// in <c>Admin/_Access</c>), reported by <c>hub.IsGlobalAdmin()</c>. This gates the platform
/// features that live in the Admin partition (Invitations, Inbox, Global Administration, Models).
///
/// <para>🚨 A platform admin is NOT a data superuser. An <c>Admin/_Access</c> grant is scoped to
/// the Admin partition; it does NOT confer access to <b>spaces</b> or <b>user partitions</b>.
/// Standing access is platform management (send invites, delete things); emergency changes to
/// space/user data are a separate, explicit <b>elevation</b> (break-glass), not standing
/// permission. See Doc/Architecture/AccessControl.md → "The Admin partition".</para>
/// </summary>
public class AdminPartitionAdminTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                // Platform admin: Admin role on the ADMIN PARTITION (scope "Admin").
                AssignmentNodeFactory.UserRole("AdminBoss", "Admin", "Admin"),
                // A space-scoped user, for the negative checks.
                AssignmentNodeFactory.UserRole("AcmeEditor", "Editor", "ACME"));

    [Fact]
    public async Task PlatformAdmin_IsGlobalAdmin()
        => await Mesh.IsGlobalAdmin("AdminBoss").Should().Match(isAdmin => isAdmin);

    [Fact]
    public async Task PlatformAdmin_HasAllOnAdminPartition_IncludingInvitations()
    {
        await Mesh.GetEffectivePermissions("Admin", "AdminBoss").Should().Match(p => p == Permission.All);
        // Invitations live in the Admin partition — a platform admin manages them.
        await Mesh.GetEffectivePermissions("Admin/Invitation", "AdminBoss").Should().Match(p => p == Permission.All);
    }

    [Fact]
    public async Task PlatformAdmin_HasNoStandingAccessToSpacesOrUsers()
    {
        // 🚨 The directive: an Admin/_Access grant gives NO standing access to spaces nor
        // user partitions. Cross-partition data changes require explicit elevation.
        await Mesh.GetEffectivePermissions("ACME", "AdminBoss").Should().Match(p => p == Permission.None);
        await Mesh.GetEffectivePermissions("ACME/Project/Task", "AdminBoss").Should().Match(p => p == Permission.None);
        await Mesh.GetEffectivePermissions("someuser/Underwriting", "AdminBoss").Should().Match(p => p == Permission.None);
    }

    [Fact]
    public async Task NonPlatformUser_IsNotGlobalAdmin()
    {
        await Mesh.IsGlobalAdmin("AcmeEditor").Should().Match(isAdmin => !isAdmin);
        await Mesh.IsGlobalAdmin("nobody").Should().Match(isAdmin => !isAdmin);
    }
}
