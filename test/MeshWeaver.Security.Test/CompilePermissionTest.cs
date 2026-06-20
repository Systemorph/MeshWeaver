using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Pins the <see cref="Permission.Compile"/> grant model introduced for the release-creation
/// workflow:
/// <list type="bullet">
///   <item><b>Editor (and above) hold Compile</b> — Space editors create releases by default.</item>
///   <item><b>Viewer / Commenter do NOT</b> — read/comment users can't ship releases.</item>
///   <item><b>Compile is NOT folded into <see cref="Permission.All"/></b> — keeping
///     <c>HasFlag(All)</c> (and therefore <c>IsGlobalAdmin</c>) byte-stable, so a deploy doesn't
///     lock admins out until the PG <c>user_effective_permissions</c> table re-materializes.</item>
///   <item><b>System has Compile</b> — the infra recompile that fills the cache runs as System.</item>
/// </list>
/// </summary>
public class CompilePermissionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(15.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("CompileEditor", "Editor", "Space"),
                AssignmentNodeFactory.UserRole("CompileViewer", "Viewer", "Space"),
                AssignmentNodeFactory.UserRole("CompileAdmin", "Admin", "Space"),
                AssignmentNodeFactory.UserRole("CompileCommenter", "Commenter", "Space"));

    [Fact]
    public void BuiltInRoles_GrantCompile_ToEditorAndAdmin_NotViewerOrCommenter()
    {
        // Editor / Admin / PlatformAdmin ship Compile.
        Role.Editor.Permissions.Should().HaveFlag(Permission.Compile);
        Role.Admin.Permissions.Should().HaveFlag(Permission.Compile);
        Role.PlatformAdmin.Permissions.Should().HaveFlag(Permission.Compile);

        // Read-only / comment-only roles do NOT.
        Role.Viewer.Permissions.Should().NotHaveFlag(Permission.Compile);
        Role.Commenter.Permissions.Should().NotHaveFlag(Permission.Compile);
    }

    [Fact]
    public void Compile_IsNotPartOfAll_SoHasFlagAllStaysStable()
    {
        // Compile (like Sync) is a privileged grant added explicitly to roles, never folded
        // into All — otherwise every HasFlag(All)/IsGlobalAdmin check would silently require
        // it to be re-materialized first (the 2026-06-08 admin lock-out shape).
        Permission.All.Should().NotHaveFlag(Permission.Compile);
        // But an Editor (who has both) DOES satisfy a Compile check.
        Role.Editor.Permissions.HasFlag(Permission.Compile).Should().BeTrue();
    }

    [Fact(Timeout = 20000)]
    public async Task EffectivePermissions_EditorHasCompile_ViewerDoesNot()
    {
        var editor = await Mesh.GetEffectivePermissions("Space/Project", "CompileEditor")
            .FirstAsync().ToTask(TestTimeout);
        editor.Should().HaveFlag(Permission.Compile);

        var admin = await Mesh.GetEffectivePermissions("Space/Project", "CompileAdmin")
            .FirstAsync().ToTask(TestTimeout);
        admin.Should().HaveFlag(Permission.Compile);

        var viewer = await Mesh.GetEffectivePermissions("Space/Project", "CompileViewer")
            .FirstAsync().ToTask(TestTimeout);
        viewer.Should().NotHaveFlag(Permission.Compile);

        var commenter = await Mesh.GetEffectivePermissions("Space/Project", "CompileCommenter")
            .FirstAsync().ToTask(TestTimeout);
        commenter.Should().NotHaveFlag(Permission.Compile);
    }

    [Fact(Timeout = 20000)]
    public async Task System_HasCompile()
    {
        var system = await Mesh.GetEffectivePermissions("Space/Project", WellKnownUsers.System)
            .FirstAsync().ToTask(TestTimeout);
        system.Should().HaveFlag(Permission.Compile);
        system.Should().HaveFlag(Permission.Sync);
    }
}
