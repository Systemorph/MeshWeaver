using System;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Documentation;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// The documentation partition is a synced, public read-only Space: an ordinary user (and, because
/// a <c>PartitionAccessPolicy</c> caps the maximum permission at a scope, even an admin) may READ it
/// but never Create / Update / Delete — and this holds for BOTH the pages under the partition AND the
/// partition <c>Space</c> root itself (path <c>Doc</c>, namespace <c>""</c>, which sits in the scope
/// hierarchy under <c>Doc</c>). Enforced by the <c>Doc/_Policy</c> seeded by
/// <see cref="DocumentationExtensions.AddDocumentation{TBuilder}"/> (CUD = false) plus the Public→Viewer
/// grant for readability. Agent/Model synced partitions use the same shape (<c>PublicRead = true</c>,
/// CUD = false) via their built-in static providers. Regression guard for task #12.
/// </summary>
public class SyncedPartitionReadOnlyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan StepTimeout = 30.Seconds();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddDocumentation();

    [Theory]
    [InlineData("Doc")]                      // the partition Space root (namespace="")
    [InlineData("Doc/DataMesh/UnifiedPath")] // a page under the root
    public async Task Doc_IsPublicReadOnly(string path)
    {
        // A plain user with no explicit grant. Even though the test mesh seeds Public→Admin at root
        // (so this user resolves to Admin = Permission.All), the Doc/_Policy cap strips C/U/D at the
        // Doc scope and below — proving the synced space cannot be written, not even by an admin.
        const string user = "ordinary.user@example.com";

        await Mesh.GetEffectivePermissions(path, user)
            .Should().Within(StepTimeout)
            .Match(p => p.HasFlag(Permission.Read)
                        && !p.HasFlag(Permission.Create)
                        && !p.HasFlag(Permission.Update)
                        && !p.HasFlag(Permission.Delete),
                $"'{path}' must be publicly readable but never user-writable (Doc/_Policy CUD=false)");
    }
}
