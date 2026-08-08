using System.Collections.Generic;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// The PURE path arithmetic of <see cref="NodeTypeGateEvaluator"/> — no hub, no mesh, no streams.
/// The integration behaviour rides on <c>NodeTypeGateTests</c>; this class pins the rule itself so a
/// failure names the exact path relation that broke instead of surfacing as a permission timeout.
/// </summary>
public class NodeTypeGateEvaluatorTests
{
    private const string PluginType = "Store/Plugin";

    private static readonly NodeTypeGate Gate = new(PluginType)
    {
        PublicSurfaces = [NodeTypeGate.Self, "Overview", "Subscribe"],
        RedirectOnDenied = "Subscribe",
    };

    private static readonly IReadOnlyList<NodeTypeGate> Gates = [Gate];

    private static readonly IReadOnlyDictionary<string, string> GatedNodes =
        new Dictionary<string, string> { ["Storefront"] = PluginType };

    [Theory]
    [InlineData("Storefront", true)]                     // the cover — the Self surface
    [InlineData("Storefront/Overview", true)]            // the marketing page
    [InlineData("Storefront/Overview/Detail", true)]     // …and its subtree
    [InlineData("Storefront/Subscribe", true)]           // checkout
    [InlineData("Storefront/PaidLesson", false)]         // gated: entitlement only
    [InlineData("Storefront/Overviewer", false)]         // prefix collision is NOT a surface
    [InlineData("Elsewhere/Overview", false)]            // outside every gated node
    public void PublicSurfaces_AreExactlyWhatTheTypeDeclares(string path, bool expected)
        => Assert.Equal(expected,
            NodeTypeGateEvaluator.IsAnonymouslyReadable(Gates, GatedNodes, path));

    /// <summary>
    /// 🚨 The Self surface opens the gated node and NOTHING beneath it. That asymmetry is the whole
    /// reason the gate must live on the TYPE: an <c>AccessAssignment</c> grant at the plugin root
    /// inherits strictly downward, which is precisely why the materialised shape had to write a
    /// deny for every non-public child to claw the subtree back.
    /// </summary>
    [Fact]
    public void SelfSurface_OpensTheNodeOnly_NeverItsSubtree()
    {
        var coverOnly = new NodeTypeGate(PluginType) { PublicSurfaces = [NodeTypeGate.Self] };
        Assert.True(NodeTypeGateEvaluator.IsPublicSurface(coverOnly, "Storefront", "Storefront"));
        Assert.False(NodeTypeGateEvaluator.IsPublicSurface(coverOnly, "Storefront", "Storefront/Any"));
    }

    [Fact]
    public void Redirect_ResolvesRelativeToTheGatedNode()
        => Assert.Equal("Storefront/Subscribe",
            NodeTypeGateEvaluator.ResolveRedirect(Gates, GatedNodes, "Storefront/PaidLesson"));

    [Fact]
    public void Redirect_IsNull_OutsideEveryGatedNode()
        => Assert.Null(
            NodeTypeGateEvaluator.ResolveRedirect(Gates, GatedNodes, "Elsewhere/Node"));

    [Fact]
    public void Redirect_HonoursAnAbsoluteDeclaration()
    {
        var gate = new NodeTypeGate(PluginType) { RedirectOnDenied = "/Store/Catalog" };
        Assert.Equal("Store/Catalog", NodeTypeGateEvaluator.ResolveRedirect(gate, "Storefront"));
    }

    /// <summary>A gate must never be able to open a path outside the node it is anchored on.</summary>
    [Theory]
    [InlineData("../Sibling")]
    [InlineData("Public/../../Escape")]
    public void Traversals_AreRefused(string surface)
    {
        Assert.Null(NodeTypeGateEvaluator.NormalizeSurface(surface));
        var gate = new NodeTypeGate(PluginType) { PublicSurfaces = [surface] };
        Assert.False(NodeTypeGateEvaluator.IsPublicSurface(gate, "Storefront", "Sibling"));
        Assert.False(NodeTypeGateEvaluator.IsPublicSurface(gate, "Storefront", "Escape"));
    }

    /// <summary>
    /// The NEAREST gated ancestor decides — a plugin nested inside another plugin is governed by
    /// the inner one, so a surface declared by the outer gate does not leak into the inner subtree.
    /// </summary>
    [Fact]
    public void NearestGatedAncestorWins()
    {
        var nodes = new Dictionary<string, string>
        {
            ["Storefront"] = PluginType,
            ["Storefront/Inner"] = PluginType,
        };
        // "Storefront/Inner/Subscribe" is a surface of the INNER gate (Subscribe is declared).
        Assert.True(NodeTypeGateEvaluator.IsAnonymouslyReadable(
            Gates, nodes, "Storefront/Inner/Subscribe"));
        // The inner node itself is a cover (Self) — of the inner gate.
        Assert.True(NodeTypeGateEvaluator.IsAnonymouslyReadable(Gates, nodes, "Storefront/Inner"));
        // A gated page under the inner plugin stays closed.
        Assert.False(NodeTypeGateEvaluator.IsAnonymouslyReadable(
            Gates, nodes, "Storefront/Inner/PaidLesson"));
        Assert.Equal("Storefront/Inner/Subscribe", NodeTypeGateEvaluator.ResolveRedirect(
            Gates, nodes, "Storefront/Inner/PaidLesson"));
    }

    /// <summary>
    /// A mesh that declares NO gate is completely unaffected — the condition every call site
    /// short-circuits on, so the feature costs an unconfigured deployment nothing at all.
    /// </summary>
    [Fact]
    public void NoGateDeclared_MatchesNothing()
    {
        Assert.False(NodeTypeGateEvaluator.IsAnonymouslyReadable([], GatedNodes, "Storefront"));
        Assert.Null(NodeTypeGateEvaluator.ResolveRedirect([], GatedNodes, "Storefront/PaidLesson"));
    }

    /// <summary>
    /// A gate whose type has no instances yet matches nothing — the declaration alone never opens
    /// a path, so the pre-load window of the gated-node query can only be stricter.
    /// </summary>
    [Fact]
    public void GateWithoutInstances_MatchesNothing()
        => Assert.False(NodeTypeGateEvaluator.IsAnonymouslyReadable(
            Gates, new Dictionary<string, string>(), "Storefront"));
}
