using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for deleting a node and its descendants.
/// Shows descendant count and requires typing DELETE to confirm.
/// </summary>
public static class DeleteLayoutArea
{
    /// <summary>
    /// Returns the Delete menu item if the user has Delete permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, string? nodeName, Permission perms)
    {
        if (!perms.HasFlag(Permission.Delete))
            return null;
        var label = string.IsNullOrEmpty(nodeName) ? "Delete" : $"Delete {nodeName}";
        return new(label, MeshNodeLayoutAreas.DeleteArea,
            RequiredPermission: Permission.Delete, Order: 100,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.DeleteArea));
    }
    /// <summary>
    /// Entry point for the Delete layout area.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Delete(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.Path;
        var backHref = MeshNodeLayoutAreas.BuildUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();

        // Count descendants and check permissions asynchronously
        return Observable.FromAsync(async () =>
        {
            // Permission gate: check Delete permission
            var canDelete = await PermissionHelper.CanDeleteAsync(host.Hub, nodePath);
            if (!canDelete)
                return -1; // Sentinel value for access denied

            var descendantCount = 0;
            if (meshQuery != null)
            {
                await foreach (var _ in meshQuery.QueryAsync(
                    MeshQueryRequest.FromQuery($"path:{nodePath} scope:descendants")))
                    descendantCount++;
            }
            return descendantCount;
        }).Select(descendantCount =>
        {
            // Access denied
            if (descendantCount < 0)
            {
                return (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
                    .WithView(Controls.Stack
                        .WithOrientation(Orientation.Horizontal)
                        .WithHorizontalGap(16)
                        .WithStyle("align-items: center; margin-bottom: 24px;")
                        .WithView(Controls.Button("Back")
                            .WithAppearance(Appearance.Lightweight)
                            .WithIconStart(FluentIcons.ArrowLeft())
                            .WithNavigateToHref(backHref))
                        .WithView(Controls.H2("Access Denied").WithStyle("margin: 0; color: var(--error);")))
                    .WithView(Controls.Html(
                        "<p style=\"color: var(--neutral-foreground-hint);\">You do not have permission to delete this node.</p>"));
            }

            return BuildDeletePage(host, nodePath, backHref, descendantCount);
        });
    }

    private static UiControl BuildDeletePage(LayoutAreaHost host, string nodePath, string backHref, int descendantCount)
    {
        // Set up data binding for confirmation field
        var dataId = $"delete_nodes_{nodePath.Replace("/", "_")}";
        var formData = new Dictionary<string, object?>
        {
            ["confirmation"] = ""
        };
        host.UpdateData(dataId, formData);

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(16)
            .WithStyle("align-items: center; margin-bottom: 24px;")
            .WithView(Controls.Button("Back")
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithNavigateToHref(backHref))
            .WithView(Controls.H2("Delete Node").WithStyle("margin: 0; color: var(--error);")));

        // Warning
        var warningText = descendantCount > 0
            ? $"This will permanently delete this node and <strong>{descendantCount} descendant node(s)</strong> under <code>{nodePath}</code>."
            : $"This will permanently delete the node at <code>{nodePath}</code>.";

        stack = stack.WithView(Controls.Html(
            "<div style=\"padding: 16px; background: var(--error-container, #fde8e8); border-radius: 8px; " +
            "border: 1px solid var(--error, #d32f2f); margin-bottom: 24px;\">" +
            "<p style=\"margin: 0 0 8px 0; font-weight: 600; color: var(--error, #d32f2f);\">Warning: This action cannot be undone!</p>" +
            $"<p style=\"margin: 0;\">{warningText}</p>" +
            "</div>"));

        // Confirmation field
        stack = stack.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 24px;")
            .WithView(Controls.Body("Type DELETE to confirm:").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new TextFieldControl(new JsonPointerReference("confirmation"))
            {
                Placeholder = "DELETE",
                Immediate = true,
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            }.WithStyle("width: 300px;")));

        // Button row — uses IMeshService.DeleteNodeAsync
        // and runs validators (including RlsNodeValidator)
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("justify-content: flex-end;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithNavigateToHref(backHref))
            .WithView(Controls.Button("Delete")
                .WithAppearance(Appearance.Accent)
                .WithStyle("background: var(--error, #d32f2f); color: white;")
                .WithIconStart(FluentIcons.Delete())
                .WithClickAction(async ctx =>
                {
                    var formValues = await ctx.Host.Stream
                        .GetDataStream<Dictionary<string, object?>>(dataId).FirstAsync();

                    var confirmation = formValues.GetValueOrDefault("confirmation")?.ToString()?.Trim();
                    if (confirmation != "DELETE")
                    {
                        ShowDialog(ctx, "Confirmation Required",
                            "Please type **DELETE** in the confirmation field to proceed.");
                        return;
                    }

                    ShowDialog(ctx, "Deleting...", "Deletion in progress. Please wait...");

                    try
                    {
                        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                        await nodeFactory.DeleteNodeAsync(nodePath);

                        // Navigate to parent on success
                        var parentPath = GetParentPath(nodePath);
                        var parentHref = !string.IsNullOrEmpty(parentPath)
                            ? MeshNodeLayoutAreas.BuildUrl(parentPath, MeshNodeLayoutAreas.OverviewArea)
                            : "/";

                        ShowDialog(ctx, "Deleted",
                            $"Successfully deleted node **{nodePath}** and its descendants.\n\nRedirecting...");

                        ctx.NavigateTo(parentHref);
                    }
                    catch (Exception ex)
                    {
                        ShowDialog(ctx, "Delete Failed", ex.Message);
                    }
                })));

        return stack;
    }

    private static void ShowDialog(UiActionContext ctx, string title, string message)
    {
        var dialog = Controls.Dialog(
            Controls.Markdown(message),
            title
        ).WithSize("M").WithClosable(true);
        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }

    private static string? GetParentPath(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash > 0 ? path[..lastSlash] : null;
    }
}
