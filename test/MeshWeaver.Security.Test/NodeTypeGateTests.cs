using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// The TYPE-DECLARED plugin gate (issue #701) against the REAL <see cref="PermissionEvaluator"/>.
///
/// <para><b>What the whole class is worth reading for: the mesh below writes exactly ONE node for
/// the gate</b> — the plugin itself — plus one entitlement grant for the buyer. There is no
/// <c>_Policy</c> node, no root Public/Anonymous grant, and not a single per-child deny. Everything
/// the paywall needs is read off the node's TYPE:</para>
/// <list type="bullet">
///   <item>the cover, the marketing page and the checkout surface are anonymously readable,</item>
///   <item>every other page is closed to an anonymous visitor,</item>
///   <item>one root Viewer grant (what a purchase or a coupon writes) opens the whole subtree,</item>
///   <item>a denied reader's redirect target comes from the type, not from a written policy.</item>
/// </list>
///
/// <para>This inverts the materialised shape pinned by <c>PaywallRealGateShapeTests</c> (root
/// Public+Anonymous Viewer grants, then a Public+Anonymous DENY on every non-public child —
/// allow-then-deny, O(children) rows re-derived on every sync). Both shapes are honoured: the gate
/// only ever GRANTS on its declared surfaces, so it cannot weaken the deny-based gate that existing
/// deployments still carry.</para>
///
/// <para>🚨 There is nothing here that a gating pass can fail to run for a given plugin, and no
/// second condition (the production <c>_Policy</c> nodes carried <c>redirectOnDenied</c> while the
/// reader additionally demanded <c>publicRead: false</c>) for writer and reader to drift apart on.
/// A plugin that declares no price is gated for the same reason every other one is — its TYPE.</para>
/// </summary>
public class NodeTypeGateTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PluginType = "Store/Plugin";
    private const string Plugin = "Storefront";
    private const string Buyer = "gate_buyer";
    private const string Visitor = "gate_visitor";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .ConfigureNodeTypeAccess(access => access.WithGate(new NodeTypeGate(PluginType)
            {
                // The cover, the marketing page and the checkout surface — nothing else.
                PublicSurfaces = [NodeTypeGate.Self, "Overview", "Subscribe"],
                RedirectOnDenied = "Subscribe",
            }))
            .AddMeshNodes(
                // The ONE node the gate needs: the plugin. Its TYPE carries the policy.
                MeshNode.FromPath(Plugin) with { NodeType = PluginType, Name = "Storefront" },
                // The entitlement: a single Viewer grant at the plugin root — what a purchase or a
                // coupon writes. Nothing else is needed to open the subtree.
                AssignmentNodeFactory.UserRole(Buyer, "Viewer", Plugin));

    // Security tests need granular permissions — skip the PublicAdmin seed.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 30000)]
    public async Task Cover_IsAnonymouslyReadable_FromTheTypeAlone()
        => await Mesh.GetEffectivePermissions(Plugin, WellKnownUsers.Anonymous)
            .Should().Within(20.Seconds()).Match(p => p.HasFlag(Permission.Read),
                "the plugin's own node is a declared public surface of its type — no grant, no " +
                "policy and no row of any kind is written for it");

    [Theory(Timeout = 30000)]
    [InlineData("Overview")]
    [InlineData("Subscribe")]
    public async Task MarketingAndCheckout_AreAnonymouslyReadable(string surface)
        => await Mesh.GetEffectivePermissions($"{Plugin}/{surface}", WellKnownUsers.Anonymous)
            .Should().Within(20.Seconds()).Match(p => p.HasFlag(Permission.Read),
                $"'{surface}' is declared anonymous on the type — a visitor must be able to see " +
                "the offer and start a purchase without signing in");

    /// <summary>
    /// The gated page is closed to an anonymous visitor — with NO deny row anywhere in the mesh.
    ///
    /// <para>🚨 The positive wait comes FIRST and is load-bearing. A deny-shaped assertion
    /// (<c>!HasFlag(Read)</c>) is satisfied by the very first emission of a cold fold —
    /// <see cref="Permission.None"/> before anything has loaded — so on its own it certifies
    /// nothing and passes vacuously. Establishing that the cover IS readable proves the gate has
    /// folded on THIS mesh, which is what makes the denial below evidence rather than a race.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task GatedChild_IsClosedToAnonymous_WithNoDenyRows()
    {
        await Mesh.GetEffectivePermissions(Plugin, WellKnownUsers.Anonymous)
            .Should().Within(20.Seconds()).Match(p => p.HasFlag(Permission.Read),
                "barrier: the gate must be folded before a denial means anything");

        await Mesh.GetEffectivePermissions($"{Plugin}/PaidLesson", WellKnownUsers.Anonymous)
            .Should().Within(20.Seconds()).Match(p => !p.HasFlag(Permission.Read),
                "a page that is not a declared surface stays closed by the framework's " +
                "deny-by-default — the gate never had to write a deny to achieve it");
    }

    /// <summary>The signed-in visitor who bought nothing is in exactly the same position.</summary>
    [Fact(Timeout = 30000)]
    public async Task AuthenticatedButNotEntitled_IsDenied()
    {
        await Mesh.GetEffectivePermissions(Plugin, Visitor)
            .Should().Within(20.Seconds()).Match(p => p.HasFlag(Permission.Read),
                "barrier: the public cover proves the gate folded for this identity too");

        await Mesh.GetEffectivePermissions($"{Plugin}/PaidLesson", Visitor)
            .Should().Within(20.Seconds()).Match(p => !p.HasFlag(Permission.Read),
                "signing in is not an entitlement");
    }

    /// <summary>
    /// The entitlement — ONE Viewer grant at the plugin root — opens the ENTIRE subtree, with no
    /// second grant and nothing per-child. This is the case that catches an over-strict gate: a
    /// declaration that denied instead of merely granting would strip the buyer here.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData("PaidLesson")]
    [InlineData("Module/Deep/Lesson")]
    public async Task Entitlement_OpensTheWholeSubtree(string page)
        => await Mesh.GetEffectivePermissions($"{Plugin}/{page}", Buyer)
            .Should().Within(20.Seconds()).Match(p => p.HasFlag(Permission.Read),
                "the entitlement record is the single switch that opens the subtree");

    /// <summary>
    /// The redirect target comes from the TYPE. No <c>_Policy</c> node exists in this mesh — the
    /// churn source measured on memex (version counters in the hundreds of thousands, every write
    /// by <c>system-security</c>) simply has nothing to rewrite.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData(Plugin)]
    [InlineData(Plugin + "/PaidLesson")]
    [InlineData(Plugin + "/Module/Deep/Lesson")]
    public async Task RedirectOnDenied_IsTypeDeclared_WithNoPolicyNode(string path)
        => await Mesh.GetRedirectOnDenied(path)
            .Should().Within(20.Seconds()).Match(r => r == $"{Plugin}/Subscribe");

    /// <summary>
    /// Nothing outside a gated node's subtree is touched — the gate is anchored on its instances,
    /// so a mesh's other partitions evaluate exactly as they did before.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task OutsideTheGatedNode_NothingChanges()
    {
        await Mesh.GetEffectivePermissions("SomeOtherPartition/Node", WellKnownUsers.Anonymous)
            .Should().Within(20.Seconds()).Match(p => !p.HasFlag(Permission.Read));
        await Mesh.GetRedirectOnDenied("SomeOtherPartition/Node")
            .Should().Within(20.Seconds()).Match(r => r == null);
    }

    /// <summary>
    /// The signed-OUT navigation decision — the surface a real visitor hits first. The cover and
    /// checkout load; the gated page goes to /login (from where the authenticated-but-denied
    /// visitor is redirected to the type-declared paywall by <c>NamedAreaView</c>).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AnonymousGate_LoadsThePublicSurfaces_AndStopsAtTheGatedOne()
    {
        await AnonymousGate.AllowAnonymous(Mesh, Plugin)
            .Should().Within(20.Seconds()).Match(allowed => allowed);
        await AnonymousGate.AllowAnonymous(Mesh, $"{Plugin}/Subscribe")
            .Should().Within(20.Seconds()).Match(allowed => allowed);
        await AnonymousGate.AllowAnonymous(Mesh, $"{Plugin}/PaidLesson")
            .Should().Within(20.Seconds()).Match(allowed => !allowed);
    }
}
