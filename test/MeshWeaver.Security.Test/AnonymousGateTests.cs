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
/// Pins <see cref="AnonymousGate.DecideAnonymousAccess"/> against the REAL
/// <see cref="PermissionEvaluator"/> (no mocks) — the decision the Blazor navigation gate maps to
/// load / paywall-navigate / login for a logged-OUT visitor:
/// <list type="bullet">
/// <item>an explicit Anonymous Viewer grant ⇒ <c>Allow</c> (the public course cover / paywall page),</item>
/// <item>denied + <see cref="PartitionAccessPolicy.RedirectOnDenied"/> ⇒ redirect to the paywall,</item>
/// <item>denied + no policy ⇒ login,</item>
/// <item>a self-referential redirect target ⇒ login (loop-guard), never a redirect loop.</item>
/// </list>
/// The Blazor-side wiring of the three outcomes is unit-tested in
/// <c>NavigationServiceTest</c> (Hosting.Blazor.Test) via the injectable gate seam.
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
                // The paywall config: denied pages under GatedCourse redirect to the Subscribe page.
                new MeshNode("_Policy", "GatedCourse")
                {
                    NodeType = SecurityCollections.PartitionAccessPolicyNodeType,
                    Content = new PartitionAccessPolicy { RedirectOnDenied = "GatedCourse/Subscribe" }
                },
                // A gated content page: the per-child Anonymous DENY overrides the inherited root
                // grant for the Anonymous subject (deny is per-subject).
                AssignmentNodeFactory.UserRole(
                    WellKnownUsers.Anonymous, "Viewer", "GatedCourse/Lesson1",
                    denied: true, accessObject: WellKnownUsers.Anonymous),
                // A partition whose RedirectOnDenied points at ITSELF — the loop-guard case.
                new MeshNode("_Policy", "LoopCourse")
                {
                    NodeType = SecurityCollections.PartitionAccessPolicyNodeType,
                    Content = new PartitionAccessPolicy { RedirectOnDenied = "LoopCourse" }
                });

    // Security tests need granular permissions — skip the PublicAdmin seed.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 20000)]
    public async Task AnonymousGrantedNode_IsAllowed()
        => await AnonymousGate.DecideAnonymousAccess(Mesh, "GatedCourse")
            .Should().Match(d => d.Allow && d.RedirectTo == null);

    [Fact(Timeout = 20000)]
    public async Task DeniedNode_WithConfiguredPaywall_RedirectsThere()
        => await AnonymousGate.DecideAnonymousAccess(Mesh, "GatedCourse/Lesson1")
            .Should().Match(d => !d.Allow && d.RedirectTo == "GatedCourse/Subscribe");

    [Fact(Timeout = 20000)]
    public async Task DeniedNode_WithoutPolicy_FallsBackToLogin()
        => await AnonymousGate.DecideAnonymousAccess(Mesh, "PrivateSpace/Node")
            .Should().Match(d => !d.Allow && d.RedirectTo == null);

    [Fact(Timeout = 20000)]
    public async Task SelfReferentialRedirect_IsLoopGuarded_ToLogin()
        => await AnonymousGate.DecideAnonymousAccess(Mesh, "LoopCourse")
            .Should().Match(d => !d.Allow && d.RedirectTo == null);

    [Fact(Timeout = 20000)]
    public async Task DeepDescendant_InheritsTheRootAnonymousGrant()
        // A deep page under GatedCourse WITHOUT its own deny inherits the root's Anonymous
        // Viewer grant (grants flow downward) — pin the inheritance so the per-child deny in
        // DeniedNode_WithConfiguredPaywall_RedirectsThere is provably the thing doing the gating.
        => await AnonymousGate.DecideAnonymousAccess(Mesh, "GatedCourse/Deep/Page")
            .Should().Match(d => d.Allow);
}
