using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// REPRO + regression guard for "access is not propagating correctly": a hub initializes and
/// syncs its OWN EntityStore under its own credential (ImpersonateAsHub → AccessContext with
/// ObjectId = the hub's mesh address, IsHub = true), and a sub-hub subscribes to its parent/owner
/// the same way (JsonSynchronizationStream.CreateExternalClient(impersonateAsHub: true)). No
/// AccessAssignment ever exists for a hub address, so the owner's RLS used to deny that Read —
/// the sub-hub never received its parent's snapshot, its layout area never rendered, and the
/// FutuRe LineOfBusiness Search timed out at 50s (a "deadlock").
///
/// The rule: a hub credential has Read on its OWN path and its ANCESTOR scopes (the sync
/// direction), and NOTHING else — never siblings, descendants, the mesh root, and never a
/// non-hub (user) identity.
/// </summary>
public class HubCredentialReadAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddRowLevelSecurity();

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("acme/space/child", "acme/space/child", true)]  // self — hub reads its own node
    [InlineData("acme/space/child", "acme/space", true)]        // parent — sub-hub reads its parent
    [InlineData("acme/space/child", "acme", true)]              // partition root (ancestor)
    [InlineData("acme/space/child", "acme/space/child/leaf", true)]  // descendant — hub reads its own subtree
    [InlineData("acme/space/child", "acme/other", false)]       // sibling subtree — denied
    [InlineData("acme/space/child", "beta", false)]             // unrelated partition — denied
    [InlineData("acme/space/child", "", false)]                 // mesh root — denied
    public async Task HubCredential_ReadsSelfAndAncestors_NotSiblings(
        string hubAddress, string scope, bool expectRead)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var saved = access.CircuitContext;
        try
        {
            // Mirror the owner-side RLS: the SubscribeRequest arrives stamped IsHub with the
            // sub-hub's address as ObjectId; the pipeline sets that as the active context.
            access.SetCircuitContext(new AccessContext
            {
                ObjectId = hubAddress,
                Name = hubAddress,
                IsHub = true
            });

            var perms = await Mesh.GetEffectivePermissions(scope, hubAddress)
                .FirstAsync().Timeout(15.Seconds()).ToTask(TestContext.Current.CancellationToken);

            perms.HasFlag(Permission.Read).Should().Be(expectRead,
                $"hub '{hubAddress}' reading scope '{scope}' should{(expectRead ? "" : " NOT")} have Read");
        }
        finally
        {
            access.SetCircuitContext(saved);
        }
    }

    [Fact]
    public async Task NonHubIdentity_PathShapedId_GetsNoImplicitAncestorRead()
    {
        // A NON-hub context whose ObjectId happens to look like a path must NOT get the
        // hub ancestor-read shortcut — only IsHub credentials do.
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var saved = access.CircuitContext;
        try
        {
            access.SetCircuitContext(new AccessContext
            {
                ObjectId = "acme/space/child",
                Name = "acme/space/child",
                IsHub = false
            });

            var perms = await Mesh.GetEffectivePermissions("acme/space", "acme/space/child")
                .FirstAsync().Timeout(15.Seconds()).ToTask(TestContext.Current.CancellationToken);

            perms.HasFlag(Permission.Read).Should().BeFalse(
                "only IsHub credentials get the ancestor-read shortcut, not a user with a path-shaped id");
        }
        finally
        {
            access.SetCircuitContext(saved);
        }
    }
}
