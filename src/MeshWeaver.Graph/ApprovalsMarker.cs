using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Marker set on a per-node hub's configuration when the Approvals module is active
/// (MeshWeaver.Approvals, listed under <c>Modules:Assemblies</c> or added via its
/// <c>AddApprovals()</c> builder extension). It stays in Graph — not in the module — so the
/// markdown overview can keep guarding its embedded Approvals section without referencing the
/// module: when the module is delisted, the marker is never set and the section self-suppresses.
/// </summary>
public sealed record ApprovalsEnabled;

/// <summary>
/// The guard the markdown overview (and any other embedder) uses to decide whether to render an
/// approvals section. True only when the Approvals module registered on this hub.
/// </summary>
public static class ApprovalsMarkerExtensions
{
    /// <summary>
    /// Checks if approvals are enabled in the configuration.
    /// </summary>
    public static bool HasApprovals(this MessageHubConfiguration configuration)
        => configuration.Get<ApprovalsEnabled>() != null;
}
