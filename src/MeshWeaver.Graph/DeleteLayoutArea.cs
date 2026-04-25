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
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Delete))
            return null;
        return new("Delete", MeshNodeLayoutAreas.DeleteArea,
            RequiredPermission: Permission.Delete, Order: 100,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.DeleteArea));
    }
    /// <summary>
    /// Entry point for the Delete layout area.
    /// Fully reactive composition — no <c>await</c> on the rendering path.
    /// Permission and descendant-count streams are combined via <c>CombineLatest</c>;
    /// a blocked hub cannot produce an emission so the render stays empty instead of deadlocking.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Delete(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.Path;
        var backHref = MeshNodeLayoutAreas.BuildUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();

        // Both source streams must emit at least once for the page to render. Add Timeout
        // + Catch so a stuck permission lookup or a hanging descendant count can never
        // leave the user with an eternal spinner. We render conservatively on failure
        // (deny, zero descendants) rather than blocking.
        var permissionsObs = PermissionHelper.ObservePermissions(host.Hub, nodePath)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<Permission, Exception>(_ => Observable.Return(Permission.None));

        var descendantsObs = (meshQuery != null
            ? Observable.FromAsync(token => CountDescendantsAsync(meshQuery, nodePath, token))
            : Observable.Return(0))
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<int, Exception>(_ => Observable.Return(0));

        var placeholder = (UiControl?)Controls.Stack.WithStyle("padding: 24px;")
            .WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint);\">Loading delete confirmation…</p>"));

        return permissionsObs.CombineLatest(descendantsObs,
            (perms, count) => (canDelete: perms.HasFlag(Permission.Delete), count))
            .Select(tuple => (UiControl?)(tuple.canDelete
                ? BuildDeletePage(host, nodePath, backHref, tuple.count)
                : BuildAccessDenied(backHref)))
            .StartWith(placeholder);
    }

    private static async Task<int> CountDescendantsAsync(IMeshService meshQuery, string nodePath, CancellationToken ct)
    {
        var count = 0;
        await foreach (var _ in meshQuery.QueryAsync(
                           MeshQueryRequest.FromQuery($"path:{nodePath} scope:descendants"), ct))
            count++;
        return count;
    }

    private static UiControl BuildAccessDenied(string backHref) =>
        Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
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

    private static UiControl BuildDeletePage(LayoutAreaHost host, string nodePath, string backHref, int descendantCount)
    {
        // Form + progress state.
        var dataId = $"delete_nodes_{nodePath.Replace("/", "_")}";
        host.UpdateData(dataId, new Dictionary<string, object?>
        {
            ["confirmation"] = ""
        });
        var progressId = $"delete_progress_{nodePath.Replace("/", "_")}";
        host.UpdateData(progressId, DeleteStatus.Idle);

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

        var warningText = descendantCount > 0
            ? $"This will permanently delete this node and <strong>{descendantCount} descendant node(s)</strong> under <code>{nodePath}</code>."
            : $"This will permanently delete the node at <code>{nodePath}</code>.";

        stack = stack.WithView(Controls.Html(
            "<div style=\"padding: 16px; background: var(--error-container, #fde8e8); border-radius: 8px; " +
            "border: 1px solid var(--error, #d32f2f); margin-bottom: 24px;\">" +
            "<p style=\"margin: 0 0 8px 0; font-weight: 600; color: var(--error, #d32f2f);\">Warning: This action cannot be undone!</p>" +
            $"<p style=\"margin: 0;\">{warningText}</p>" +
            "</div>"));

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

        // Progress / status banner — driven by the progressId data stream.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<DeleteStatus>(progressId)
            .Select(status => (UiControl?)RenderStatus(status, nodePath)));

        // Button row: Cancel + Delete. Delete is gated by an in-flight status so the user
        // can't double-submit; during the request we render a progress indicator above.
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
                .WithClickAction(ctx => StartDelete(ctx, host, nodePath, dataId, progressId, backHref))));

        return stack;
    }

    /// <summary>
    /// Kicks off the delete via Post + RegisterCallback — no <c>await</c>. Drives the progressId
    /// data stream so the user sees "Deleting…" while the callback is pending, and "Deleted" /
    /// "Failed" once the response arrives. See Doc/Architecture/AsynchronousCalls.
    /// </summary>
    private static Task StartDelete(
        UiActionContext ctx,
        LayoutAreaHost host,
        string nodePath,
        string dataId,
        string progressId,
        string backHref)
    {
        ctx.Host.Stream
            .GetDataStream<Dictionary<string, object?>>(dataId)
            .Take(1)
            .Subscribe(formValues =>
            {
                var confirmation = formValues.GetValueOrDefault("confirmation")?.ToString()?.Trim();
                if (confirmation != "DELETE")
                {
                    ShowDialog(ctx, "Confirmation Required",
                        "Please type **DELETE** in the confirmation field to proceed.");
                    return;
                }

                ctx.Host.UpdateData(progressId, DeleteStatus.InFlight);

                // Post the DeleteNodeRequest to the node's own hub. We register a non-awaiting
                // callback that flips the progress stream to Done / Failed when the response
                // arrives — no blocking on the hub scheduler anywhere.
                var delivery = host.Hub.Post(
                    new DeleteNodeRequest(nodePath) { Recursive = true },
                    o => o.WithTarget(new Address(nodePath)))!;

                host.Hub.RegisterCallback(delivery, response =>
                {
                    if (response is IMessageDelivery<DeleteNodeResponse> r && r.Message.Success)
                    {
                        ctx.Host.UpdateData(progressId, DeleteStatus.Done);
                        // Navigate back — the node we were looking at no longer exists.
                        ctx.Host.UpdateArea(ctx.Area, new RedirectControl(backHref));
                    }
                    else
                    {
                        var err = response is IMessageDelivery<DeleteNodeResponse> rr
                            ? rr.Message.Error
                            : "Delete response not received.";
                        ctx.Host.UpdateData(progressId, DeleteStatus.Failed(err));
                    }
                    return response;
                });
            });

        return Task.CompletedTask;
    }

    private static UiControl? RenderStatus(DeleteStatus status, string nodePath)
    {
        if (status.Kind == DeleteStatusKind.Idle)
            return null;

        if (status.Kind == DeleteStatusKind.InFlight)
            return Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 12px; padding: 12px 16px; background: var(--neutral-layer-2); border-radius: 6px; margin-bottom: 16px;")
                .WithView(Controls.Progress("Deleting…", 0))
                .WithView(Controls.Body($"Deleting {nodePath}. Waiting for confirmation…"));

        if (status.Kind == DeleteStatusKind.Done)
            return Controls.Html(
                "<div style=\"padding: 12px 16px; background: var(--success-container, #e6f7e6); color: var(--success, #107c10); border-radius: 6px; margin-bottom: 16px;\">Node deleted. Redirecting…</div>");

        // Failed
        var message = System.Web.HttpUtility.HtmlEncode(status.ErrorMessage ?? "Unknown error");
        return Controls.Html(
            $"<div style=\"padding: 12px 16px; background: var(--error-container, #fde8e8); color: var(--error, #d32f2f); border-radius: 6px; margin-bottom: 16px;\"><strong>Delete failed:</strong> {message}</div>");
    }

    private enum DeleteStatusKind { Idle, InFlight, Done, Failed }

    private record DeleteStatus(DeleteStatusKind Kind, string? ErrorMessage = null)
    {
        public static DeleteStatus Idle { get; } = new(DeleteStatusKind.Idle);
        public static DeleteStatus InFlight { get; } = new(DeleteStatusKind.InFlight);
        public static DeleteStatus Done { get; } = new(DeleteStatusKind.Done);
        public static DeleteStatus Failed(string? msg) => new(DeleteStatusKind.Failed, msg);
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
