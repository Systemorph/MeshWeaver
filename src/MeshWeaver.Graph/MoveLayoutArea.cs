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
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for moving a node and its subtree to a new location.
/// </summary>
[Browsable(false)]
public static class MoveLayoutArea
{
    /// <summary>
    /// Returns the Move menu item if the user has Delete permission (move requires delete on source).
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Delete))
            return null;
        return new("Move", MeshNodeLayoutAreas.MoveArea,
            RequiredPermission: Permission.Delete, Order: 3,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.MoveArea));
    }

    /// <summary>
    /// Layout area handler for the Move action. Pure observable — no await, no
    /// Observable.FromAsync(async ...). Permission gating composes via
    /// <see cref="PermissionHelper.ObservePermissions"/>.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Move(LayoutAreaHost host, RenderingContext _)
    {
        var currentPath = host.Hub.Address.ToString();
        var currentId = currentPath.Contains('/') ? currentPath[(currentPath.LastIndexOf('/') + 1)..] : currentPath;

        return PermissionHelper.ObservePermissions(host.Hub, currentPath)
            .Select(perms =>
            {
                if (!perms.HasFlag(Permission.Delete))
                    return (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
                        .WithView(Controls.H2("Access Denied").WithStyle("margin: 0 0 16px 0;"))
                        .WithView(Controls.Html(
                            "<p style=\"color: var(--neutral-foreground-hint);\">You do not have permission to move this node.</p>"));

                return (UiControl?)BuildMoveForm(host, currentPath, currentId);
            });
    }

    private static UiControl BuildMoveForm(LayoutAreaHost host, string currentPath, string currentId)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>();
        var formId = $"move_form_{currentPath.Replace("/", "_")}";
        var backHref = MeshNodeLayoutAreas.BuildUrl(currentPath, MeshNodeLayoutAreas.OverviewArea);

        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["targetNamespace"] = "",
            ["newId"] = currentId
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
            .WithView(Controls.H2("Move Node").WithStyle("margin: 0;")));

        // Source (read-only display)
        stack = stack.WithView(Controls.Html(
            $"<p style=\"margin-bottom: 16px;\">Move <strong>{System.Web.HttpUtility.HtmlEncode(currentPath)}</strong> and all descendants to a new location.</p>"));

        // Target namespace picker
        stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("targetNamespace"))
        {
            Label = "Target Namespace",
            Placeholder = "Select target namespace...",
            DataContext = dataContext
        }.WithQueries("context:create").WithMaxResults(15)
         .WithStyle("width: 100%; margin-bottom: 16px;"));

        // New node Id
        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("newId"))
        {
            Label = "Node Id",
            Placeholder = "Enter the node id...",
            DataContext = dataContext
        }.WithStyle("width: 100%; margin-bottom: 16px;"));

        // Buttons — sync click action; reads form via Take(1)+Subscribe and posts
        // MoveNodeRequest, then registers a callback for the response. No await
        // anywhere on the click path: the form-data Take(1) is a one-shot read
        // that completes after the first emission, and the response handling
        // runs from the registered callback (off the click pump).
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("justify-content: flex-end;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithNavigateToHref(backHref))
            .WithView(Controls.Button("Move")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.ArrowMove())
                .WithClickAction(ctx =>
                {
                    host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                        .Take(1)
                        .Subscribe(formValues =>
                        {
                            var targetNs = formValues?.GetValueOrDefault("targetNamespace")?.ToString()?.Trim() ?? "";
                            var newId = formValues?.GetValueOrDefault("newId")?.ToString()?.Trim() ?? currentId;

                            if (string.IsNullOrWhiteSpace(newId))
                            {
                                ShowDialog(ctx, "Validation Error", "Please enter a node Id.");
                                return;
                            }

                            var targetPath = string.IsNullOrEmpty(targetNs) ? newId : $"{targetNs}/{newId}";

                            if (targetPath == currentPath)
                            {
                                ShowDialog(ctx, "Validation Error", "Target path is the same as source path.");
                                return;
                            }

                            logger?.LogInformation("Moving node from {Source} to {Target}", currentPath, targetPath);

                            var delivery = host.Hub.Post(
                                new MoveNodeRequest(currentPath, targetPath),
                                o => o.WithTarget(host.Hub.Address));

                            host.Hub.Observe((IMessageDelivery)delivery!)
                                .Subscribe(
                                    response =>
                                    {
                                        if (response.Message is MoveNodeResponse mr)
                                        {
                                            if (!mr.Success)
                                            {
                                                ShowDialog(ctx, "Move Failed", mr.Error ?? "Unknown error.");
                                            }
                                            else
                                            {
                                                logger?.LogInformation("Move complete: {Source} -> {Target}", currentPath, targetPath);

                                                var overviewUrl = MeshNodeLayoutAreas.BuildUrl(targetPath, MeshNodeLayoutAreas.OverviewArea);

                                                var successDialog = Controls.Dialog(
                                                    Controls.Markdown($"**Move Complete**\n\nMoved to `{targetPath}`."),
                                                    "Move Complete"
                                                ).WithSize("M").WithClosable(true).WithCloseAction(_ =>
                                                {
                                                    ctx.NavigateTo(overviewUrl);
                                                    return Task.CompletedTask;
                                                });
                                                ctx.Host.UpdateArea(DialogControl.DialogArea, successDialog);
                                            }
                                        }
                                        else
                                        {
                                            logger?.LogWarning("Move: unexpected response {Type} for {Source} -> {Target}",
                                                response.Message?.GetType().Name, currentPath, targetPath);
                                            ShowDialog(ctx, "Move Failed", $"Unexpected response: {response.Message?.GetType().Name}");
                                        }
                                    },
                                    ex =>
                                    {
                                        logger?.LogError(ex, "Move failed for {Source} -> {Target}",
                                            currentPath, targetPath);
                                        ShowDialog(ctx, "Move Failed", $"Move failed: {ex.Message}");
                                    });
                        });
                    return Task.CompletedTask;
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
