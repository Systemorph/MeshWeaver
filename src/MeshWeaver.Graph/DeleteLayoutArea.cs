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
        var permissionsObs = host.Hub.GetEffectivePermissions(nodePath)
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<Permission, Exception>(_ => Observable.Return(Permission.None));

        // Descendant count via reactive ObserveQuery — no await foreach on the thread pool.
        var descendantsObs = (meshQuery != null
            ? CountDescendants(meshQuery, nodePath)
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

    private static IObservable<int> CountDescendants(IMeshService meshQuery, string nodePath) =>
        meshQuery.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{nodePath} scope:descendants"))
            .Take(1)
            .Select(c => c.Items.Count);

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

        // Ready-made, theme-aware styling: Fluent's status-DANGER design tokens — the SAME ones
        // <FluentMessageBar Intent="Error"> (see MeshNodeErrorCardView) uses, so it reads correctly in
        // BOTH light and dark. (The old hardcoded pink #fde8e8 was a light-only box that clashed on a
        // dark page.) The var() fallbacks are cross-mode too — a translucent-red tint + mid-red text —
        // so it degrades safely if a token is ever absent. Text inherits the danger foreground.
        stack = stack.WithView(Controls.Html(
            "<div style=\"padding: 16px; border-radius: 8px; margin-bottom: 24px; " +
            "background: var(--colorStatusDangerBackground1, rgba(211,47,47,0.12)); " +
            "border: 1px solid var(--colorStatusDangerBorder1, rgba(211,47,47,0.5)); " +
            "color: var(--colorStatusDangerForeground1, #d32f2f);\">" +
            "<p style=\"margin: 0 0 8px 0; font-weight: 600;\">Warning: This action cannot be undone!</p>" +
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
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();
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

                host.Hub.Observe(delivery)
                    .Subscribe(
                        response =>
                        {
                            if (response.Message is DeleteNodeResponse { Success: true })
                            {
                                ctx.Host.UpdateData(progressId, DeleteStatus.Done);
                                // Redirect to the nearest ancestor that is an ACTUAL mesh node.
                                // The node we were looking at no longer exists, and its immediate
                                // parent PATH is frequently a virtual grouping (e.g. ".../Script")
                                // with no node of its own — redirecting straight there would just
                                // land the user on another "No node found" page. Resolve the closest
                                // existing ancestor instead; a top-level node (none) goes home. The
                                // bare node URL renders that node's default area (Mesh URL shape).
                                ResolveNearestExistingAncestor(meshQuery, nodePath)
                                    .Take(1)
                                    .Timeout(TimeSpan.FromSeconds(10))
                                    .Catch<string?, Exception>(_ => Observable.Return(GetParentPath(nodePath)))
                                    .Subscribe(ancestor =>
                                    {
                                        var target = ancestor is null ? "/" : $"/{ancestor}";
                                        ctx.Host.UpdateArea(ctx.Area, new RedirectControl(target));
                                    });
                            }
                            else
                            {
                                var err = response.Message is DeleteNodeResponse rr
                                    ? rr.Error
                                    : "Delete response not received.";
                                ctx.Host.UpdateData(progressId, DeleteStatus.Failed(err));
                            }
                        },
                        ex => ctx.Host.UpdateData(progressId, DeleteStatus.Failed(ex.Message)));
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
                "<div style=\"padding: 12px 16px; border-radius: 6px; margin-bottom: 16px; " +
                "background: var(--colorStatusSuccessBackground1, rgba(16,124,16,0.12)); " +
                "color: var(--colorStatusSuccessForeground1, #107c10);\">Node deleted. Redirecting…</div>");

        // Failed
        var message = System.Web.HttpUtility.HtmlEncode(status.ErrorMessage ?? "Unknown error");
        return Controls.Html(
            "<div style=\"padding: 12px 16px; border-radius: 6px; margin-bottom: 16px; " +
            "background: var(--colorStatusDangerBackground1, rgba(211,47,47,0.12)); " +
            $"color: var(--colorStatusDangerForeground1, #d32f2f);\"><strong>Delete failed:</strong> {message}</div>");
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

    /// <summary>
    /// Resolves the nearest ANCESTOR of <paramref name="nodePath"/> that is an actual mesh node,
    /// walking up the path nearest-first. The immediate parent PATH segment is frequently a virtual
    /// grouping (e.g. <c>AgenticPension/Script</c>) that has children but no node of its own —
    /// redirecting there after a delete would just land on another "No node found". Each candidate
    /// is an existence QUERY (the eventually-consistent index is fine: ancestor existence is stable
    /// and we never touch the just-deleted node), never a node-hub subscribe — a subscribe to a
    /// missing node hangs until timeout. Emits the nearest existing ancestor, or <c>null</c> when
    /// none exists (a top-level node → redirect home). Short-circuits: stops probing as soon as an
    /// existing ancestor is found.
    /// </summary>
    internal static IObservable<string?> ResolveNearestExistingAncestor(IMeshService? meshQuery, string nodePath)
    {
        var immediateParent = GetParentPath(nodePath);
        if (immediateParent is null)
            return Observable.Return<string?>(null);            // top-level node → home
        if (meshQuery is null)
            return Observable.Return<string?>(immediateParent); // no query service → best-effort parent

        var ancestors = new List<string>();
        for (var p = immediateParent; p is not null; p = GetParentPath(p))
            ancestors.Add(p);

        return ancestors
            .Select(ancestor => meshQuery
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{ancestor}"))
                .Take(1)
                .Select(c => c.Items.Any(n => n.Path == ancestor) ? ancestor : null))
            .Aggregate(
                Observable.Return<string?>(null),
                (acc, probe) => acc.SelectMany(found =>
                    found is not null ? Observable.Return(found) : probe));
    }
}
