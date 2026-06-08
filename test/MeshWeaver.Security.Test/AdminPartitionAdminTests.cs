using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Pins the canonical global-admin model: a "global admin" is an admin on the
/// <b>Admin partition</b> — <see cref="Permission.All"/> at scope <c>Admin</c> (an
/// <c>AccessAssignment</c> in <c>Admin/_Access</c>). The <c>PermissionEvaluator</c>
/// global-admin short-circuit then grants <see cref="Permission.All"/> on EVERY path
/// (platform superuser, like <c>System</c>), and <c>hub.IsGlobalAdmin()</c> is the one
/// predicate that reports it. See Doc/Architecture/AccessControl.md.
///
/// <para>Regression guard for the 2026-06-08 lockout: the platform-admin gates check
/// scope <c>Admin</c> while the seed wrote the grant at the root <c>_Access</c> scope —
/// writers and readers disagreed, so a configured admin saw none of the admin tabs.</para>
/// </summary>
public class AdminPartitionAdminTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                // Admin on the ADMIN PARTITION (scope "Admin" → Admin/_Access) — the
                // canonical platform admin. The ONLY grant this user has.
                AssignmentNodeFactory.UserRole("AdminBoss", "Admin", "Admin"),
                // A user scoped to ACME only — must never be mistaken for a global admin.
                AssignmentNodeFactory.UserRole("AcmeEditor", "Editor", "ACME"));

    [Fact]
    public void AdminPartitionAdmin_HasAllPermissions_OnEveryPath()
    {
        // The Admin partition itself.
        Mesh.GetEffectivePermissions("Admin", "AdminBoss").Should().Match(p => p == Permission.All);
        // Other partitions — granted purely by the global-admin short-circuit.
        Mesh.GetEffectivePermissions("ACME", "AdminBoss").Should().Match(p => p == Permission.All);
        Mesh.GetEffectivePermissions("ACME/ProductLaunch/Todo/Task1", "AdminBoss").Should().Match(p => p == Permission.All);
        Mesh.GetEffectivePermissions("rbuergi/Underwriting", "AdminBoss").Should().Match(p => p == Permission.All);
    }

    [Fact]
    public void AdminPartitionAdmin_IsGlobalAdmin()
        => Mesh.IsGlobalAdmin("AdminBoss").Should().Match(isAdmin => isAdmin);

    [Fact]
    public void ScopedUser_IsNotGlobalAdmin_AndStaysScoped()
    {
        Mesh.IsGlobalAdmin("AcmeEditor").Should().Match(isAdmin => !isAdmin);
        // No leak: an ACME-only editor has nothing on the Admin partition nor elsewhere.
        Mesh.GetEffectivePermissions("Admin", "AcmeEditor").Should().Match(p => p == Permission.None);
        Mesh.GetEffectivePermissions("MeshWeaver", "AcmeEditor").Should().Match(p => p == Permission.None);
    }

    [Fact]
    public void UnknownUser_IsNotGlobalAdmin()
        => Mesh.IsGlobalAdmin("nobody").Should().Match(isAdmin => !isAdmin);
}
