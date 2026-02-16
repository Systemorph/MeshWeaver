using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
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
    /// Entry point for the Delete layout area.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Delete(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var backHref = MeshNodeLayoutAreas.BuildContentUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        // Count descendants asynchronously
        return Observable.FromAsync(async () =>
        {
            var descendantCount = 0;
            if (persistence != null)
            {
                await foreach (var _ in persistence.GetDescendantsAsync(nodePath))
                    descendantCount++;
            }
            return descendantCount;
        }).Select(descendantCount =>
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

            // Button row
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
                            var persistence2 = host.Hub.ServiceProvider.GetService<IPersistenceService>();
                            if (persistence2 == null)
                            {
                                ShowDialog(ctx, "Error", "Persistence service is not available.");
                                return;
                            }

                            await persistence2.DeleteNodeAsync(nodePath, recursive: true);

                            // Navigate to parent on success
                            var parentPath = GetParentPath(nodePath);
                            var parentHref = !string.IsNullOrEmpty(parentPath)
                                ? MeshNodeLayoutAreas.BuildContentUrl(parentPath, MeshNodeLayoutAreas.OverviewArea)
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

            return (UiControl?)stack;
        });
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
