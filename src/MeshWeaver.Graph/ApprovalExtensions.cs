using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for adding approval support to a message hub.
/// Approvals are stored per-node as individual JSON files in _Approval/ sub-partition.
/// Follows the CommentsExtensions pattern.
/// </summary>
public static class ApprovalExtensions
{
    /// <summary>
    /// The sub-partition name where approvals are stored.
    /// </summary>
    public const string ApprovalPartition = "_Approval";

    /// <summary>
    /// Marker type used to detect if approvals are enabled in a hub configuration.
    /// </summary>
    public record ApprovalsEnabled;

    /// <summary>
    /// Adds approval support to the message hub configuration.
    /// Registers the Approval type, adds it to the data source, menu items, and layout views.
    /// </summary>
    public static MessageHubConfiguration AddApprovals(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithType<Approval>(nameof(Approval))
            .Set(new ApprovalsEnabled())
            .AddData(data => data.WithDataSource(_ =>
                new MeshDataSource(Guid.NewGuid().AsString(), data.Workspace)
                    .WithType<Approval>(ApprovalPartition, nameof(Approval))))
            .AddNodeMenuItems(ApprovalMenuProvider)
            .AddLayout(layout => layout
                .WithView("RequestApproval", ApprovalsView.RequestApproval)
                .WithView("Approvals", ApprovalsView.InlineApprovals));
    }

    /// <summary>
    /// Checks if approvals are enabled in the configuration.
    /// </summary>
    public static bool HasApprovals(this MessageHubConfiguration configuration)
        => configuration.Get<ApprovalsEnabled>() != null;

    /// <summary>
    /// Menu provider that yields "Request Approval" for users with update permission.
    /// </summary>
    private static async IAsyncEnumerable<NodeMenuItemDefinition> ApprovalMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var perms = await PermissionHelper.GetEffectivePermissionsAsync(host.Hub, hubPath);
        if (perms.HasFlag(Permission.Update))
            yield return new NodeMenuItemDefinition("Request Approval", "RequestApproval",
                Order: 30, Href: MeshNodeLayoutAreas.BuildContentUrl(hubPath, "RequestApproval"));
    }
}
