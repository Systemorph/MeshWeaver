using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Approvals;

/// <summary>
/// The Approvals module's registration surface. Production installs it via
/// <c>Modules:Assemblies</c> (<see cref="ApprovalsModuleAttribute"/>); a mesh that composes it
/// explicitly — a test fixture, a bespoke host — calls <see cref="AddApprovals"/> for the
/// identical registration. Approvals are stored per-node as individual satellites in the
/// <c>_Approval</c> sub-partition (following the Comments pattern).
/// </summary>
public static class ApprovalExtensions
{
    /// <summary>
    /// The sub-partition name where approvals are stored.
    /// </summary>
    public const string ApprovalPartition = "_Approval";

    /// <summary>
    /// Registers approval workflows on this mesh: the <c>Approval</c> node type plus — on EVERY
    /// per-node hub — the Request Approval form, the inline approvals section, the node-menu entry,
    /// and the <c>ApprovalsEnabled</c> marker the markdown overview's embedded section checks.
    /// Delisting the module (or not calling this) removes the Approvals UI mesh-wide while the
    /// <c>Approval</c> data record and its satellite-table mapping stay platform-level.
    /// </summary>
    public static MeshBuilder AddApprovals(this MeshBuilder builder)
        => builder
            .AddApprovalType()
            .ConfigureDefaultNodeHub(ConfigureHub);

    /// <summary>
    /// The per-node-hub half of the registration: type + data source + menu entry + the
    /// RequestApproval/Approvals layout areas + the <see cref="ApprovalsEnabled"/> marker.
    /// ONE code path shared by the module attribute and <see cref="AddApprovals"/>.
    /// </summary>
    internal static MessageHubConfiguration ConfigureHub(MessageHubConfiguration configuration)
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
    /// Reactive menu provider that emits "Request Approval" for users with Update permission.
    /// Re-emits when the viewer's effective permissions change (e.g. a role is granted at runtime),
    /// so the item appears/disappears without a reload. Emits an empty slice when Update is absent.
    /// </summary>
    private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> ApprovalMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        return host.Hub.GetEffectivePermissions(hubPath).Select(perms =>
            perms.HasFlag(Permission.Update)
                ? (IReadOnlyCollection<NodeMenuItemDefinition>)
                    [new NodeMenuItemDefinition(
                        "Request Approval", "RequestApproval",
                        // Emoji, like every other node-menu entry — a checkmark reads as "sign-off"
                        // in any locale. (A Fluent icon name would render as a broken <img>.)
                        Icon: "✅",
                        Order: 30, Href: MeshNodeLayoutAreas.BuildUrl(hubPath, "RequestApproval"))
                        { LabelKey = "menu.requestApproval" }]
                : []);
    }
}
