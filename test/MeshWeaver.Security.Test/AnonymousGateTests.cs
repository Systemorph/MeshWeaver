using System.Threading.Tasks;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Pins <see cref="AnonymousGate.AllowAnonymous"/> against the REAL
/// <see cref="PermissionEvaluator"/> (no mocks) — the decision the Blazor navigation gate maps to
/// load / login for a logged-OUT visitor:
/// <list type="bullet">
/// <item>an explicit Anonymous Viewer grant ⇒ allowed (the public course cover / catalog),</item>
/// <item>EVERYTHING else ⇒ /login — even when the partition configures a
///   <see cref="PartitionAccessPolicy.RedirectOnDenied"/> paywall: sign-in is always the first
///   step, and the paywall applies to the then-authenticated visitor via the area-level
///   access-denied redirect (<c>NamedAreaView</c>).</item>
/// </list>
/// The Blazor-side wiring of the outcomes is unit-tested in <c>NavigationServiceTest</c>
/// (Hosting.Blazor.Test) via the injectable gate seam.
/// </summary>
public class AnonymousGateTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                // The public cover: the course root carries an explicit Anonymous Viewer grant.
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Anonymous, "Viewer", "GatedCourse",
                    accessObject: WellKnownUsers.Anonymous),
                // A paywall policy exists — it must NOT change the anonymous decision (login
                // first; the paywall is the authenticated visitor's redirect).
                new MeshNode("_Policy", "GatedCourse")
                {
                    NodeType = SecurityCollections.PartitionAccessPolicyNodeType,
                    Content = new PartitionAccessPolicy { RedirectOnDenied = "GatedCourse/Subscribe" }
                },
                // A gated content page: the per-child Anonymous DENY overrides the inherited root
                // grant for the Anonymous subject (deny is per-subject).
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Anonymous, "Viewer", "GatedCourse/Lesson1",
                    denied: true, accessObject: WellKnownUsers.Anonymous));

    // Security tests need granular permissions — skip the PublicAdmin seed.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 20000)]
    public async Task AnonymousGrantedNode_IsAllowed()
        => await AnonymousGate.AllowAnonymous(Mesh, "GatedCourse")
            .Should().Match(allowed => allowed);

    [Fact(Timeout = 20000)]
    public async Task DeniedNode_GoesToLogin_EvenWithAPaywallConfigured()
        // The RedirectOnDenied paywall exists at this scope — the ANONYMOUS visitor still goes to
        // /login first; only the signed-in-but-denied visitor is redirected to the paywall.
        => await AnonymousGate.AllowAnonymous(Mesh, "GatedCourse/Lesson1")
            .Should().Match(allowed => !allowed);

    [Fact(Timeout = 20000)]
    public async Task UngrantedNode_GoesToLogin()
        => await AnonymousGate.AllowAnonymous(Mesh, "PrivateSpace/Node")
            .Should().Match(allowed => !allowed);

    [Fact(Timeout = 20000)]
    public async Task DeepDescendant_InheritsTheRootAnonymousGrant()
        // A deep page under GatedCourse WITHOUT its own deny inherits the root's Anonymous
        // Viewer grant (grants flow downward) — pin the inheritance so the per-child deny in
        // DeniedNode_GoesToLogin_EvenWithAPaywallConfigured is provably the thing doing the gating.
        => await AnonymousGate.AllowAnonymous(Mesh, "GatedCourse/Deep/Page")
            .Should().Match(allowed => allowed);
}
