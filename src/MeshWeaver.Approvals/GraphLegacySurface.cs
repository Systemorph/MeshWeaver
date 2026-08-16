using System;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

// 🚨 Deliberately the GRAPH namespaces. This file restores the EXACT public surface in-mesh
// sources compiled against before the Approvals extraction (#1654): the FOUR classes the
// extraction deleted from MeshWeaver.Graph — ApprovalExtensions, ApprovalsView,
// ApprovalLayoutAreas (namespace MeshWeaver.Graph) and ApprovalNodeType (namespace
// MeshWeaver.Graph.Configuration) — as thin delegates to their MeshWeaver.Approvals homes.
// The compiler cannot see the callers (AGENTS.md: in-mesh source is invisible to
// `dotnet build`): on the first image shipping the extraction, 11 NodeTypes across the
// SocialMedia and UWDeepfield partitions regressed to CompileError (CS1061 AddApprovals,
// CS0103 ApprovalExtensions / ApprovalNodeType) and the bake gate refused readiness
// mesh-wide — the same symbol family as the #683-era outage. Node content lives in OTHER
// repos and in production databases, so the platform keeps the surface; content can migrate
// to the module registration at its own pace. ApprovalsLegacySurfaceCompileTest is the
// compiler for these callers.
namespace MeshWeaver.Graph
{
    /// <summary>
    /// Legacy Approvals surface for in-mesh sources written before the Approvals module
    /// extraction (#1654). The real registration lives in
    /// <see cref="Approvals.ApprovalExtensions"/> and is applied to every per-node hub when the
    /// module is installed — so this entry point only fills the gap on hubs the module has not
    /// configured, and is a no-op everywhere else.
    /// </summary>
    public static class ApprovalExtensions
    {
        /// <summary>The sub-partition name where approvals are stored.</summary>
        public const string ApprovalPartition = Approvals.ApprovalExtensions.ApprovalPartition;

        /// <summary>
        /// Adds approval support to this hub configuration. Idempotent: when the Approvals module
        /// (or an earlier call) already configured the hub — detected via the
        /// <see cref="ApprovalsEnabled"/> marker
        /// (<see cref="ApprovalsMarkerExtensions.HasApprovals"/>, which remains the one
        /// <c>HasApprovals</c> surface) — the configuration is returned unchanged.
        /// </summary>
        public static MessageHubConfiguration AddApprovals(this MessageHubConfiguration configuration)
            => ApprovalsMarkerExtensions.HasApprovals(configuration)
                ? configuration
                : Approvals.ApprovalExtensions.ConfigureHub(configuration);
    }

    /// <summary>
    /// Legacy home of the Approvals views (pre-#1654) — delegates to
    /// <see cref="Approvals.ApprovalsView"/>.
    /// </summary>
    public static class ApprovalsView
    {
        /// <inheritdoc cref="Approvals.ApprovalsView.RequestApproval"/>
        public static IObservable<UiControl?> RequestApproval(LayoutAreaHost host, RenderingContext context)
            => Approvals.ApprovalsView.RequestApproval(host, context);

        /// <inheritdoc cref="Approvals.ApprovalsView.InlineApprovals"/>
        public static IObservable<UiControl?> InlineApprovals(LayoutAreaHost host, RenderingContext context)
            => Approvals.ApprovalsView.InlineApprovals(host, context);
    }

    /// <summary>
    /// Legacy home of the Approvals layout areas (pre-#1654) — delegates to
    /// <see cref="Approvals.ApprovalLayoutAreas"/>.
    /// </summary>
    public static class ApprovalLayoutAreas
    {
        /// <inheritdoc cref="Approvals.ApprovalLayoutAreas.OverviewArea"/>
        public const string OverviewArea = Approvals.ApprovalLayoutAreas.OverviewArea;

        /// <inheritdoc cref="Approvals.ApprovalLayoutAreas.ThumbnailArea"/>
        public const string ThumbnailArea = Approvals.ApprovalLayoutAreas.ThumbnailArea;

        /// <inheritdoc cref="Approvals.ApprovalLayoutAreas.AddApprovalViews"/>
        public static MessageHubConfiguration AddApprovalViews(this MessageHubConfiguration configuration)
            => Approvals.ApprovalLayoutAreas.AddApprovalViews(configuration);

        /// <inheritdoc cref="Approvals.ApprovalLayoutAreas.Overview"/>
        public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext context)
            => Approvals.ApprovalLayoutAreas.Overview(host, context);

        /// <inheritdoc cref="Approvals.ApprovalLayoutAreas.Thumbnail"/>
        public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext context)
            => Approvals.ApprovalLayoutAreas.Thumbnail(host, context);
    }
}

namespace MeshWeaver.Graph.Configuration
{
    /// <summary>
    /// Legacy home of the Approval node type helpers (pre-#1654) — delegates to
    /// <see cref="MeshWeaver.Approvals.ApprovalNodeType"/>.
    /// </summary>
    public static class ApprovalNodeType
    {
        /// <inheritdoc cref="MeshWeaver.Approvals.ApprovalNodeType.NodeType"/>
        public const string NodeType = MeshWeaver.Approvals.ApprovalNodeType.NodeType;

        /// <inheritdoc cref="MeshWeaver.Approvals.ApprovalNodeType.AddApprovalType{TBuilder}"/>
        public static TBuilder AddApprovalType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
            => MeshWeaver.Approvals.ApprovalNodeType.AddApprovalType(builder);

        /// <inheritdoc cref="MeshWeaver.Approvals.ApprovalNodeType.CreateMeshNode"/>
        public static MeshNode CreateMeshNode()
            => MeshWeaver.Approvals.ApprovalNodeType.CreateMeshNode();
    }
}
