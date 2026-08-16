using MeshWeaver.Messaging;

// 🚨 Deliberately the GRAPH namespace. This file restores the EXACT public surface in-mesh
// sources compiled against before the Approvals extraction (#1654): `MeshWeaver.Graph.
// ApprovalExtensions` with `AddApprovals(this MessageHubConfiguration)`. The extraction moved
// the class to the MeshWeaver.Approvals namespace and narrowed the public entry point to
// MeshBuilder — deleting a public framework surface that the compiler cannot see the callers
// of (AGENTS.md: in-mesh source is invisible to `dotnet build`). On the first image that
// shipped the extraction, 11 NodeTypes across the SocialMedia and UWDeepfield partitions
// regressed to CompileError (CS1061 AddApprovals / CS0103 ApprovalExtensions) and the bake
// gate refused readiness mesh-wide — the same symbol that caused the #683-era outage.
// Node content lives in OTHER repos and in production databases, so the platform keeps the
// surface; content can migrate to the module registration at its own pace.
namespace MeshWeaver.Graph;

/// <summary>
/// Legacy Approvals surface for in-mesh sources written before the Approvals module extraction
/// (#1654). The real registration lives in <see cref="Approvals.ApprovalExtensions"/> and is
/// applied to every per-node hub when the module is installed — so this entry point only fills
/// the gap on hubs the module has not configured, and is a no-op everywhere else.
/// </summary>
public static class ApprovalExtensions
{
    /// <summary>The sub-partition name where approvals are stored.</summary>
    public const string ApprovalPartition = Approvals.ApprovalExtensions.ApprovalPartition;

    /// <summary>
    /// Adds approval support to this hub configuration. Idempotent: when the Approvals module
    /// (or an earlier call) already configured the hub — detected via the
    /// <see cref="ApprovalsEnabled"/> marker (<see cref="ApprovalsMarkerExtensions.HasApprovals"/>,
    /// which remains the one <c>HasApprovals</c> surface) — the configuration is returned
    /// unchanged.
    /// </summary>
    public static MessageHubConfiguration AddApprovals(this MessageHubConfiguration configuration)
        => ApprovalsMarkerExtensions.HasApprovals(configuration)
            ? configuration
            : Approvals.ApprovalExtensions.ConfigureHub(configuration);
}
