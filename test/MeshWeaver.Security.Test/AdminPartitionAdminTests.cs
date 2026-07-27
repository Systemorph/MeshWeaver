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
        await Mesh.GetEffectivePermissions("Admin", "AdminBoss").Should().Match(p => p == (Permission.All | Permission.Compile));
        // Invitations live in the Admin partition — a platform admin manages them.
        await Mesh.GetEffectivePermissions("Admin/Invitation", "AdminBoss").Should().Match(p => p == (Permission.All | Permission.Compile));
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

    /// <summary>
    /// 🚨 The invariant the STORE depends on, named in the terms the business states it:
    /// being a global admin must not grant READ on any node. Gated/purchased content is gated
    /// for admins too — entitlement is a record, never a side effect of administering the
    /// platform. Asserted on Read specifically (not just "no permissions") so the guarantee is
    /// greppable and cannot be weakened to "well, read-only is harmless".
    /// </summary>
    [Fact]
    public async Task PlatformAdmin_GrantsNoReadOnAnyOtherNode()
    {
        await Mesh.GetEffectivePermissions("ACME", "AdminBoss")
            .Should().Match(p => !p.HasFlag(Permission.Read));
        // A purchasable plugin and its gated child — the store's paywall must hold for an admin
        // who has not bought it, exactly as it does for any other signed-in visitor.
        await Mesh.GetEffectivePermissions("AgenticEngineering", "AdminBoss")
            .Should().Match(p => !p.HasFlag(Permission.Read));
        await Mesh.GetEffectivePermissions("AgenticEngineering/Introduction", "AdminBoss")
            .Should().Match(p => !p.HasFlag(Permission.Read));
        // Another user's home stays private from the platform admin as well.
        await Mesh.GetEffectivePermissions("someuser/Notes", "AdminBoss")
            .Should().Match(p => !p.HasFlag(Permission.Read));
    }

    [Fact]
    public async Task NonPlatformUser_IsNotGlobalAdmin()
    {
        await Mesh.IsGlobalAdmin("AcmeEditor").Should().Match(isAdmin => !isAdmin);
        await Mesh.IsGlobalAdmin("nobody").Should().Match(isAdmin => !isAdmin);
    }
}
