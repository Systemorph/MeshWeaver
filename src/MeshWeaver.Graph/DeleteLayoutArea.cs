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
    /// Query parameter (<c>q</c>) selecting the QUERY-SET mode of the Delete area. Its value is one
    /// or more mesh queries (newline-separated, URL-escaped — see <see cref="BuildQueryDeleteUrl"/>)
    /// whose combined result set is offered for deletion. This makes <c>/{path}/Delete</c> a CLEAR
    /// URL that can name a whole SET of nodes: an agent whose own delete was refused hands the user
    /// this link, the user reviews exactly what matches, and confirms under their OWN identity —
    /// the server stays the authority on every single path.
    /// </summary>
    public const string QueriesParam = "q";

    /// <summary>
    /// Builds the clear delete URL for the result set of one or more mesh queries:
    /// <c>/{anchorPath}/Delete?q={escaped queries}</c>. Multiple queries are newline-separated
    /// inside the single escaped parameter (the same multi-query convention the search surface
    /// uses). <paramref name="anchorPath"/> is any node the viewer can open — typically the space
    /// or parent the set lives under; it anchors the page, it is NOT itself deleted unless a query
    /// matches it.
    /// </summary>
    /// <param name="anchorPath">The node path the Delete page renders on.</param>
    /// <param name="queries">The mesh queries whose combined results should be offered for deletion.</param>
    /// <returns>The application-relative URL of the query-set delete page.</returns>
    public static string BuildQueryDeleteUrl(string anchorPath, IEnumerable<string> queries)
        => MeshNodeLayoutAreas.BuildUrl(anchorPath, MeshNodeLayoutAreas.DeleteArea,
            $"{QueriesParam}={Uri.EscapeDataString(string.Join("\n", queries))}");

    /// <summary>
    /// Parses the raw <see cref="QueriesParam"/> value into the query list: newline-separated,
    /// trimmed, empties dropped, duplicates removed. Pure — pinned in unit tests.
    /// </summary>
    /// <param name="raw">The raw (already URL-unescaped) parameter value.</param>
    /// <returns>The distinct, non-empty queries in declaration order.</returns>
    internal static IReadOnlyList<string> ParseQueries(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    /// <summary>
    /// Returns the Delete menu item if the user has Delete permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Delete))
            return null;
        return new("Delete", MeshNodeLayoutAreas.DeleteArea,
            RequiredPermission: Permission.Delete, Order: 100,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.DeleteArea))
            { LabelKey = "menu.delete" };
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

        // QUERY-SET mode (…/Delete?q=…): the URL names a SET of nodes via mesh queries instead of
        // the single anchor node. No up-front permission gate here — the set may span partitions
        // where the viewer's rights differ per node, so the page lists what the viewer can READ
        // and every delete is decided by the server per path on confirm (refusals are surfaced,
        // never swallowed). The UI probe on the single-node page below is convenience; the server
        // is the authority either way.
        var rawQueries = host.Reference.GetParameterValue(QueriesParam);
        if (!string.IsNullOrWhiteSpace(rawQueries))
            return DeleteQuerySet(host, ParseQueries(rawQueries), nodePath, backHref, meshQuery);

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
                : BuildAccessDenied(backHref, locale: host.ViewerLocale())))
            .StartWith(placeholder);
    }

    private static IObservable<int> CountDescendants(IMeshService meshQuery, string nodePath) =>
        meshQuery.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{nodePath} scope:descendants"))
            .Take(1)
            .Select(c => c.Items.Count);

    private static UiControl BuildAccessDenied(string backHref, string? locale = null) =>
        Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(16)
                .WithStyle("align-items: center; margin-bottom: 24px;")
                .WithView(Controls.Button(LocalizationCatalog.Get("common.back", locale))
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(backHref))
                .WithView(Controls.H2(LocalizationCatalog.Get("error.accessDenied", locale)).WithStyle("margin: 0; color: var(--error);")))
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
            .WithView(Controls.Button(host.Localize("common.back"))
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithNavigateToHref(backHref))
            .WithView(Controls.H2(host.Localize("ui.deleteNode")).WithStyle("margin: 0; color: var(--error);")));

        var safePath = System.Web.HttpUtility.HtmlEncode(nodePath);
        var warningText = descendantCount > 0
            ? $"This will permanently delete this node and <strong>{descendantCount} descendant node(s)</strong> under <code>{safePath}</code>."
            : $"This will permanently delete the node at <code>{safePath}</code>.";

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
            .WithView(Controls.Body(host.Localize("ui.typeDeleteToConfirm")).WithStyle("font-weight: 600; margin-bottom: 4px;"))
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
            .WithView(Controls.Button(host.Localize("common.cancel"))
                .WithAppearance(Appearance.Neutral)
                .WithNavigateToHref(backHref))
            .WithView(Controls.Button(host.Localize("common.delete"))
                .WithAppearance(Appearance.Accent)
                .WithStyle("background: var(--error, #d32f2f); color: white;")
                .WithIconStart(FluentIcons.Delete())
                .WithClickAction(ctx => StartDelete(ctx, host, nodePath, dataId, progressId, backHref))));

        return stack;
    }

    /// <summary>
    /// Kicks off the delete via Post + <c>hub.Observe(...)</c> — no <c>await</c>. Drives the progressId
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
                    ShowDialog(ctx, host.Localize("ui.confirmationRequired"),
                        host.Localize("ui.confirmationDialogText"));
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
                                // Redirect to the parent page. The node we were looking at no longer
                                // exists, so we must leave its page FAST — otherwise the just-deleted
                                // node's area re-renders against a gone node and errors (the "reload
                                // then error"). Two paths:
                                //  • The immediate parent is a SATELLITE grouping (_Thread, _Activity,
                                //    …) — it has no node of its own, so resolve the target by PURE PATH
                                //    (walk up past satellite segments) IMMEDIATELY, no query. This is
                                //    the common case (a thread → the user's home) and, crucially, a
                                //    distributed portal's cross-partition existence probe can't stall
                                //    the redirect behind a timeout (which is what left the dead page up
                                //    long enough to reload + error).
                                //  • A NON-satellite parent (a virtual grouping like ".../Script") can
                                //    only be told apart from a real node by asking — keep the existence
                                //    walk there, but bounded, and fall back to the pure-path ancestor
                                //    (never the maybe-virtual immediate parent) so the fallback can't
                                //    land on another "No node found" page.
                                var immediateParent = GetParentPath(nodePath);
                                var target = IsSatelliteSegment(immediateParent)
                                    ? Observable.Return(NearestNonSatelliteAncestor(nodePath))
                                    : ResolveNearestExistingAncestor(meshQuery, nodePath)
                                        .Take(1)
                                        .Timeout(TimeSpan.FromSeconds(5))
                                        .Catch<string?, Exception>(_ => Observable.Return(NearestNonSatelliteAncestor(nodePath)));
                                target.Subscribe(ancestor =>
                                    ctx.Host.UpdateArea(ctx.Area,
                                        new RedirectControl(ancestor is null ? "/" : $"/{ancestor}")));
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

    // ----- Query-set mode: …/Delete?q={mesh queries} deletes a whole result SET -----

    /// <summary>One resolved query snapshot for the query-set page: the query, the matched paths
    /// (as far as the VIEWER can read — the mesh access-filters every query), and the query's own
    /// error when it faulted. A faulted query is an ANSWER shown on the page, never folded into
    /// "no matches" — pretending empty would invite a confirm that deletes less than the user was
    /// told.</summary>
    private static IObservable<(string Query, IReadOnlyList<string> Paths, string? Error)> QuerySnapshot(
        LayoutAreaHost host, IMeshService meshQuery, string query) =>
        meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            // Gate on the INITIAL snapshot, not merely the first emission — a live change racing
            // the subscribe must not become the "review set" (the repo-wide query idiom).
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Select(c => (Query: query,
                Paths: (IReadOnlyList<string>)c.Items
                    .Select(n => n.Path)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList(),
                Error: (string?)null))
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<(string Query, IReadOnlyList<string> Paths, string? Error), Exception>(ex =>
                Observable.Return((query, (IReadOnlyList<string>)Array.Empty<string>(),
                    (string?)(ex is TimeoutException ? host.Localize("ui.queryTimeout") : ex.Message))));

    /// <summary>
    /// The query-set delete page: resolves every query to a snapshot, prunes redundant descendants
    /// (deleting a parent is recursive, so a child listed beside its parent would only fail with
    /// "not found" after the parent went), and renders the reviewable set behind the same typed
    /// DELETE confirmation as the single-node page. Deliberately NOT gated on the anchor's Delete
    /// permission: the set may span nodes with per-path rights, so each delete is decided by the
    /// server on confirm and every refusal is surfaced by path.
    /// </summary>
    private static IObservable<UiControl?> DeleteQuerySet(
        LayoutAreaHost host, IReadOnlyList<string> queries, string anchorPath, string backHref,
        IMeshService? meshQuery)
    {
        if (queries.Count == 0)
            return Observable.Return<UiControl?>(BuildQuerySetInfo(host, backHref,
                host.Localize("ui.deleteNoQueries")));
        if (meshQuery is null)
            return Observable.Return<UiControl?>(BuildQuerySetInfo(host, backHref,
                host.Localize("ui.deleteQueryServiceUnavailable")));

        var placeholder = (UiControl?)Controls.Stack.WithStyle("padding: 24px;")
            .WithView(Controls.Html(
                $"<p style=\"color: var(--neutral-foreground-hint);\">{System.Web.HttpUtility.HtmlEncode(host.Localize("ui.resolvingQueryResults"))}</p>"));

        return Observable.CombineLatest(queries.Select(q => QuerySnapshot(host, meshQuery, q)))
            .Select(snapshots => (UiControl?)BuildQuerySetPage(host, anchorPath, backHref, meshQuery, snapshots))
            .StartWith(placeholder);
    }

    /// <summary>A minimal info page (header + message + back) for the query-set states that have
    /// nothing to confirm.</summary>
    private static UiControl BuildQuerySetInfo(LayoutAreaHost host, string backHref, string message) =>
        Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(16)
                .WithStyle("align-items: center; margin-bottom: 24px;")
                .WithView(Controls.Button(host.Localize("common.back"))
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(backHref))
                .WithView(Controls.H2(host.Localize("ui.deleteQueryResults")).WithStyle("margin: 0;")))
            .WithView(Controls.Html(
                $"<p style=\"color: var(--neutral-foreground-hint);\">{System.Web.HttpUtility.HtmlEncode(message)}</p>"));

    /// <summary>How many matched paths the page lists explicitly before eliding the rest.</summary>
    private const int MaxListedPaths = 100;

    private static UiControl BuildQuerySetPage(
        LayoutAreaHost host, string anchorPath, string backHref, IMeshService meshQuery,
        IList<(string Query, IReadOnlyList<string> Paths, string? Error)> snapshots)
    {
        var paths = PruneRedundantDescendants(snapshots.SelectMany(s => s.Paths));
        // Catalog strings are trusted markup-free text; every DYNAMIC part (query, error) is
        // HTML-encoded before it is interpolated.
        var queryLines = string.Join("", snapshots.Select(s =>
            $"<li><code>{System.Web.HttpUtility.HtmlEncode(s.Query)}</code>" +
            (s.Error is null
                ? $" — {host.LocalizePlural("plural.match", s.Paths.Count)}"
                : $" — <span style=\"color: var(--colorStatusDangerForeground1, #d32f2f);\">{host.Localize("ui.queryFailed", System.Web.HttpUtility.HtmlEncode(s.Error))}</span>") +
            "</li>"));

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(16)
            .WithStyle("align-items: center; margin-bottom: 24px;")
            .WithView(Controls.Button(host.Localize("common.back"))
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithNavigateToHref(backHref))
            .WithView(Controls.H2(host.Localize("ui.deleteQueryResults")).WithStyle("margin: 0; color: var(--error);")));

        stack = stack.WithView(Controls.Html(
            $"<div style=\"margin-bottom: 16px;\"><p style=\"margin: 0 0 8px 0; font-weight: 600;\">{System.Web.HttpUtility.HtmlEncode(host.Localize("ui.queries"))}</p>" +
            $"<ul style=\"margin: 0;\">{queryLines}</ul></div>"));

        if (paths.Count == 0)
            return stack.WithView(Controls.Html(
                $"<p style=\"color: var(--neutral-foreground-hint);\">{System.Web.HttpUtility.HtmlEncode(host.Localize("ui.deleteNoMatches"))}</p>"));

        var listed = paths.Take(MaxListedPaths)
            .Select(p => $"<li><code>{System.Web.HttpUtility.HtmlEncode(p)}</code></li>");
        var elided = paths.Count > MaxListedPaths
            ? $"<li>{System.Web.HttpUtility.HtmlEncode(host.Localize("ui.andMore", paths.Count - MaxListedPaths))}</li>"
            : string.Empty;

        stack = stack.WithView(Controls.Html(
            "<div style=\"padding: 16px; border-radius: 8px; margin-bottom: 24px; " +
            "background: var(--colorStatusDangerBackground1, rgba(211,47,47,0.12)); " +
            "border: 1px solid var(--colorStatusDangerBorder1, rgba(211,47,47,0.5)); " +
            "color: var(--colorStatusDangerForeground1, #d32f2f);\">" +
            $"<p style=\"margin: 0 0 8px 0; font-weight: 600;\">{System.Web.HttpUtility.HtmlEncode(host.Localize("ui.deleteWarningTitle"))}</p>" +
            $"<p style=\"margin: 0 0 8px 0;\">{System.Web.HttpUtility.HtmlEncode(host.Localize("ui.deleteSetWarning", host.LocalizePlural("plural.node", paths.Count)))}</p>" +
            $"<ul style=\"margin: 0;\">{string.Join("", listed)}{elided}</ul>" +
            "</div>"));

        var suffix = anchorPath.Replace("/", "_");
        var dataId = $"delete_set_{suffix}";
        host.UpdateData(dataId, new Dictionary<string, object?> { ["confirmation"] = "" });
        var progressId = $"delete_set_progress_{suffix}";
        host.UpdateData(progressId, new QuerySetProgress(DeleteStatusKind.Idle));

        stack = stack.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 24px;")
            .WithView(Controls.Body(host.Localize("ui.typeDeleteToConfirm")).WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new TextFieldControl(new JsonPointerReference("confirmation"))
            {
                Placeholder = "DELETE",
                Immediate = true,
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            }.WithStyle("width: 300px;")));

        stack = stack.WithView((h, _) => h.Stream.GetDataStream<QuerySetProgress>(progressId)
            .Select(status => (UiControl?)RenderQuerySetStatus(status)));

        return stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("justify-content: flex-end;")
            .WithView(Controls.Button(host.Localize("common.cancel"))
                .WithAppearance(Appearance.Neutral)
                .WithNavigateToHref(backHref))
            .WithView(Controls.Button(host.Localize("common.delete"))
                .WithAppearance(Appearance.Accent)
                .WithStyle("background: var(--error, #d32f2f); color: white;")
                .WithIconStart(FluentIcons.Delete())
                .WithClickAction(ctx =>
                    StartQuerySetDelete(ctx, host, meshQuery, paths, anchorPath, dataId, progressId))));
    }

    /// <summary>
    /// Reads the typed confirmation and, when it says DELETE, runs the set deletion — one node at
    /// a time, strictly sequential (the platform's one sequencing rule: the next delete is only
    /// subscribed after the previous one answered), each result an ANSWER carried into the summary.
    /// A refused path never stops the rest of the set. Reactive throughout — no <c>await</c>.
    /// </summary>
    private static Task StartQuerySetDelete(
        UiActionContext ctx,
        LayoutAreaHost host,
        IMeshService meshQuery,
        IReadOnlyList<string> paths,
        string anchorPath,
        string dataId,
        string progressId)
    {
        ctx.Host.Stream
            .GetDataStream<Dictionary<string, object?>>(dataId)
            .Take(1)
            .Subscribe(formValues =>
            {
                var confirmation = formValues.GetValueOrDefault("confirmation")?.ToString()?.Trim();
                if (confirmation != "DELETE")
                {
                    ShowDialog(ctx, host.Localize("ui.confirmationRequired"),
                        host.Localize("ui.confirmationDialogText"));
                    return;
                }

                ctx.Host.UpdateData(progressId,
                    new QuerySetProgress(DeleteStatusKind.InFlight,
                        host.Localize("ui.deletingProgress", 0, paths.Count)));

                var results = new List<(string Path, string? Error)>();
                paths
                    .Select(path => Observable.Defer(() => meshQuery.DeleteNode(path)
                        .Select(_ => (Path: path, Error: (string?)null))
                        // A refused/faulted path is an ANSWER for the summary — converting it here
                        // is what lets the rest of the set proceed; it is never silently dropped.
                        .Catch<(string Path, string? Error), Exception>(ex =>
                            Observable.Return((path, (string?)ex.Message)))))
                    .Concat()
                    .Subscribe(
                        result =>
                        {
                            results.Add(result);
                            ctx.Host.UpdateData(progressId, new QuerySetProgress(
                                DeleteStatusKind.InFlight,
                                host.Localize("ui.deletingProgress", results.Count, paths.Count)));
                        },
                        ex => ctx.Host.UpdateData(progressId, new QuerySetProgress(
                            DeleteStatusKind.Failed,
                            host.Localize("ui.deleteRunFailed", System.Web.HttpUtility.HtmlEncode(ex.Message)))),
                        () => FinishQuerySetDelete(ctx, host, meshQuery, results, anchorPath, progressId));
            });

        return Task.CompletedTask;
    }

    /// <summary>Writes the terminal summary (deleted count + per-path refusals) and, when the set
    /// took the page's own anchor with it, leaves the dead page the same way the single-node flow
    /// does — redirecting to the nearest surviving ancestor.</summary>
    private static void FinishQuerySetDelete(
        UiActionContext ctx,
        LayoutAreaHost host,
        IMeshService meshQuery,
        IReadOnlyList<(string Path, string? Error)> results,
        string anchorPath,
        string progressId)
    {
        var failures = results.Where(r => r.Error is not null).ToList();
        var deleted = results.Count - failures.Count;

        if (failures.Count == 0)
            ctx.Host.UpdateData(progressId, new QuerySetProgress(
                DeleteStatusKind.Done,
                System.Web.HttpUtility.HtmlEncode(
                    host.Localize("ui.deletedCount", host.LocalizePlural("plural.node", deleted)))));
        else
        {
            var lines = string.Join("", failures.Select(f =>
                $"<li><code>{System.Web.HttpUtility.HtmlEncode(f.Path)}</code> — {System.Web.HttpUtility.HtmlEncode(f.Error!)}</li>"));
            ctx.Host.UpdateData(progressId, new QuerySetProgress(
                DeleteStatusKind.Failed,
                System.Web.HttpUtility.HtmlEncode(host.Localize("ui.deletedPartial",
                    deleted, host.LocalizePlural("plural.node", results.Count))) +
                $"<ul style=\"margin: 8px 0 0 0;\">{lines}</ul>" +
                $"<p style=\"margin: 8px 0 0 0;\">{System.Web.HttpUtility.HtmlEncode(host.Localize("ui.deleteRefusalNote"))}</p>"));
        }

        // The anchor (or an ancestor of it) may itself be in the deleted set — then this page's
        // node is gone and it must be left FAST, exactly like the single-node flow.
        var deletedPaths = results.Where(r => r.Error is null).Select(r => r.Path);
        if (!CoversPath(deletedPaths, anchorPath))
            return;
        ResolveNearestExistingAncestor(meshQuery, anchorPath)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<string?, Exception>(_ => Observable.Return(NearestNonSatelliteAncestor(anchorPath)))
            .Subscribe(ancestor =>
                ctx.Host.UpdateArea(ctx.Area, new RedirectControl(ancestor is null ? "/" : $"/{ancestor}")));
    }

    private static UiControl? RenderQuerySetStatus(QuerySetProgress status) => status.Kind switch
    {
        DeleteStatusKind.Idle => null,
        DeleteStatusKind.InFlight => Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 12px; padding: 12px 16px; background: var(--neutral-layer-2); border-radius: 6px; margin-bottom: 16px;")
            .WithView(Controls.Progress(status.Html ?? string.Empty, 0)),
        DeleteStatusKind.Done => Controls.Html(
            "<div style=\"padding: 12px 16px; border-radius: 6px; margin-bottom: 16px; " +
            "background: var(--colorStatusSuccessBackground1, rgba(16,124,16,0.12)); " +
            $"color: var(--colorStatusSuccessForeground1, #107c10);\">{status.Html}</div>"),
        _ => Controls.Html(
            "<div style=\"padding: 12px 16px; border-radius: 6px; margin-bottom: 16px; " +
            "background: var(--colorStatusDangerBackground1, rgba(211,47,47,0.12)); " +
            $"color: var(--colorStatusDangerForeground1, #d32f2f);\">{status.Html}</div>"),
    };

    /// <summary>Progress record for the query-set delete: <see cref="DeleteStatusKind"/> + the
    /// status content — plain text while in flight (rendered through text controls), pre-rendered
    /// HTML for the terminal states, whose dynamic parts are always HTML-encoded at the write
    /// site. All user-facing text is localized at the write site, where the host is available.</summary>
    private sealed record QuerySetProgress(DeleteStatusKind Kind, string? Html = null);

    /// <summary>
    /// Removes every path that another path in the set already covers: deletion is recursive, so a
    /// descendant listed beside its ancestor is redundant — and deleting it AFTER the ancestor
    /// would only fail with "not found", polluting the summary with failures that are not real.
    /// Case-insensitive on path segments (the affordance layer compares paths the same way).
    /// Pure — pinned in unit tests.
    /// </summary>
    /// <param name="paths">The combined matched paths, in any order, duplicates allowed.</param>
    /// <returns>The de-duplicated set with covered descendants removed, subtree-sorted.</returns>
    internal static IReadOnlyList<string> PruneRedundantDescendants(IEnumerable<string> paths)
    {
        // Linear after the sort — but the sort key matters for CORRECTNESS, not just speed. A plain
        // lexicographic order does NOT keep a subtree contiguous: '-' (0x2D) and '.' (0x2E) sort
        // BELOW '/' (0x2F), so between "A" and its child "A/y" a sibling "A-x" or "A.md" would
        // interleave, and a single running ancestor would forget "A" before reaching "A/y".
        // Substituting '/' with '\0' (below every path character) makes each subtree a contiguous
        // range, and then kept entries never nest — so ONE remembered ancestor decides coverage.
        string? ancestor = null;
        var result = new List<string>();
        foreach (var path in paths
                     .Where(p => !string.IsNullOrEmpty(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(p => p.Replace('/', '\0'), StringComparer.OrdinalIgnoreCase))
        {
            if (ancestor is not null
                && path.Length > ancestor.Length + 1
                && path.StartsWith(ancestor + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(path);
            ancestor = path;
        }
        return result;
    }

    /// <summary>True when any of <paramref name="deletedPaths"/> equals <paramref name="path"/> or
    /// is an ancestor of it — i.e. the recursive deletes took <paramref name="path"/> with them.
    /// Pure — pinned in unit tests.</summary>
    internal static bool CoversPath(IEnumerable<string> deletedPaths, string path)
        => deletedPaths.Any(d =>
            string.Equals(d, path, StringComparison.OrdinalIgnoreCase)
            || (path.Length > d.Length + 1 && path.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase)));

    private static string? GetParentPath(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash > 0 ? path[..lastSlash] : null;
    }

    /// <summary>Last segment of the path (the bit after the final '/'), or the whole path.</summary>
    private static string LastSegment(string path)
        => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>
    /// True when the path's last segment is a SATELLITE grouping (<c>_Thread</c>, <c>_Activity</c>,
    /// <c>_Comment</c>, …) — a '_'-prefixed segment that anchors satellites but is never a node of its
    /// own. Redirecting there after a delete always lands on "No node found".
    /// </summary>
    private static bool IsSatelliteSegment(string? path)
        => path is not null && LastSegment(path).StartsWith('_');

    /// <summary>
    /// Nearest ancestor whose last segment is NOT a satellite grouping — resolved by PURE PATH, no
    /// query (so it can never stall on a distributed portal). A thread <c>{user}/_Thread/{id}</c>
    /// resolves to <c>{user}</c>; a top-level node resolves to <c>null</c> (redirect home).
    /// </summary>
    internal static string? NearestNonSatelliteAncestor(string nodePath)
    {
        for (var p = GetParentPath(nodePath); p is not null; p = GetParentPath(p))
            if (!LastSegment(p).StartsWith('_'))
                return p;
        return null;
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
