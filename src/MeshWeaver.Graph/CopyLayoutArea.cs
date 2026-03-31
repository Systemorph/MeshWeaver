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
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for copying a node and its subtree to a new location.
/// </summary>
[Browsable(false)]
public static class CopyLayoutArea
{
    /// <summary>
    /// Returns the Copy menu item if the user has Create permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Create))
            return null;
        return new("Copy", MeshNodeLayoutAreas.CopyArea,
            RequiredPermission: Permission.Create, Order: 2,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.CopyArea));
    }

    /// <summary>
    /// Layout area handler for the Copy action.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Copy(LayoutAreaHost host, RenderingContext _)
    {
        var currentPath = host.Hub.Address.ToString();

        return Observable.FromAsync(async () =>
        {
            var canCreate = await PermissionHelper.CanCreateAsync(host.Hub, currentPath);
            if (!canCreate)
            {
                return (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
                    .WithView(Controls.H2("Access Denied").WithStyle("margin: 0 0 16px 0;"))
                    .WithView(Controls.Html(
                        "<p style=\"color: var(--neutral-foreground-hint);\">You do not have permission to copy nodes.</p>"));
            }

            return (UiControl?)BuildCopyForm(host, currentPath);
        });
    }

    private static UiControl BuildCopyForm(LayoutAreaHost host, string currentPath)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>();
        var formId = $"copy_form_{currentPath.Replace("/", "_")}";
        var backHref = MeshNodeLayoutAreas.BuildUrl(currentPath, MeshNodeLayoutAreas.OverviewArea);

        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["targetNamespace"] = "",
            ["force"] = false
        });
        var dataContext = LayoutAreaReference.GetDataPointer(formId);

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
            .WithView(Controls.H2("Copy Node").WithStyle("margin: 0;")));

        // Source (read-only display)
        stack = stack.WithView(Controls.Html(
            $"<p style=\"margin-bottom: 16px;\">Copy <strong>{System.Web.HttpUtility.HtmlEncode(currentPath)}</strong> and all descendants to a new location.</p>"));

        // Destination namespace picker
        stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("targetNamespace"))
        {
            Label = "Destination Namespace",
            Placeholder = "Select target namespace...",
            DataContext = dataContext
        }.WithQueries("context:create").WithMaxResults(15)
         .WithStyle("width: 100%; margin-bottom: 16px;"));

        // Force overwrite checkbox
        stack = stack.WithView(new CheckBoxControl(new JsonPointerReference("force"))
        {
            Label = "Force (overwrite existing nodes)",
            DataContext = dataContext
        }.WithStyle("margin-bottom: 16px;"));

        // Buttons
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("justify-content: flex-end;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithNavigateToHref(backHref))
            .WithView(Controls.Button("Copy")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.Copy())
                .WithClickAction(async ctx =>
                {
                    var formValues = await ctx.Host.Stream
                        .GetDataStream<Dictionary<string, object?>>(formId).FirstAsync();

                    var targetNs = formValues?.GetValueOrDefault("targetNamespace")?.ToString()?.Trim() ?? "";
                    var force = formValues?.GetValueOrDefault("force") is true or "True" or "true";

                    if (string.IsNullOrWhiteSpace(targetNs))
                    {
                        ShowDialog(ctx, "Validation Error", "Please select a destination namespace.");
                        return;
                    }

                    try
                    {
                        var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                        logger?.LogInformation("Copying node tree from {Source} to {Target}, force={Force}",
                            currentPath, targetNs, force);

                        var nodesCopied = await NodeCopyHelper.CopyNodeTreeAsync(
                            meshService, meshService, host.Hub, currentPath, targetNs, force, logger);

                        logger?.LogInformation("Copy complete: {Count} nodes copied", nodesCopied);

                        var overviewUrl = MeshNodeLayoutAreas.BuildUrl(targetNs, MeshNodeLayoutAreas.OverviewArea);

                        var successDialog = Controls.Dialog(
                            Controls.Markdown($"**Copy Complete**\n\nCopied **{nodesCopied}** node(s) to `{targetNs}`."),
                            "Copy Complete"
                        ).WithSize("M").WithClosable(true).WithCloseAction(_ =>
                        {
                            ctx.NavigateTo(overviewUrl);
                            return Task.CompletedTask;
                        });
                        ctx.Host.UpdateArea(DialogControl.DialogArea, successDialog);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "Copy failed for {Source} -> {Target}", currentPath, targetNs);
                        ShowDialog(ctx, "Copy Failed", $"Copy failed: {ex.Message}");
                    }
                })));

        return stack;
    }

    private static void ShowDialog(UiActionContext ctx, string title, string message)
    {
        var dialog = Controls.Dialog(
            Controls.Markdown($"**{title}:**\n\n{message}"),
            title
        ).WithSize("M").WithClosable(true);
        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }
}
