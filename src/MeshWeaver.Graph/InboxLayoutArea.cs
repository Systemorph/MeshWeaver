using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The <b>Inbox app</b> — <c>/{user}/Inbox</c>, installed for every user by
/// <c>SeedInboxAppLogonAction</c>: ONE page for everything that reaches a person — notifications,
/// mail, conversations from every connected channel, what is running for them and what just
/// finished. It is the rendered side of <see cref="InboxQueries"/>: every leg (core's own plus each
/// one a package registered) is resolved for the OWNER of the hub — anchored on their own partition,
/// projected, the invariants <c>InboxLegsAreAnchoredTest</c> pins — and its rows are merged into the
/// grid of its band.
///
/// <para><b>Select many, then act.</b> Each band is a <see cref="DataGridControl"/> with the
/// platform's own selection column (<see cref="DataGridControl.WithRowSelection"/>); the toolbar's
/// Delete acts on every ticked row of every band and Undo brings the last batch back. What Delete
/// MEANS is the item type's own business — an <see cref="IInboxItemHandler"/> the type's package
/// registered — and it is always soft: nothing is removed from the mesh. A row whose type has no
/// handler, or that is still running, renders a disabled checkbox that says why.</para>
///
/// <para>Only the owner sees it. A User node is public-read, so without the owner check anyone could
/// open another person's inbox address; the legs are RLS-scoped anyway, but the page says "not yours"
/// instead of rendering someone else's empty shell.</para>
///
/// <para>Two legs that resolve to the SAME query are shown once, under the first band that asks for
/// them — a row listed twice on one page is noise.</para>
/// </summary>
public static class InboxLayoutArea
{
    /// <summary>The layout area on the User hub — also the app record's id (<c>{user}/_App/Inbox</c>).</summary>
    public const string AreaName = "Inbox";

    /// <summary>The data id holding what the last Delete put away — what Undo brings back.</summary>
    public const string UndoId = "inboxUndo";

    /// <summary>The order bands are shown in; a provider's own band follows these, alphabetically.</summary>
    internal static readonly ImmutableArray<string> BandOrder = [InboxBand.NeedsYou, InboxBand.Running, InboxBand.Recent];

    /// <summary>The page — returned at once as a template; its rows are fed.</summary>
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var owner = UserNodeType.OwnerOf(host.Hub.Address.ToString());
        var access = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewer = access.ViewerId();
        if (string.IsNullOrEmpty(viewer) || !string.Equals(viewer, owner, StringComparison.OrdinalIgnoreCase))
            return Observable.Return<UiControl?>(Controls.Markdown(host.Localize("inbox.notYours")));

        var bands = Bands(host, owner, access.ViewerZoneId());
        if (host.TryMarkEditStateInitialized("inbox:" + UndoId))
            host.UpdateData(UndoId, ImmutableList<InboxUndoItem>.Empty);
        foreach (var band in bands)
            host.SeedRowSelection(InboxRows.SelectionId(band.Band));

        var stack = Controls.Stack.WithWidth("100%").WithVerticalGap(16)
            .WithView(Controls.H2(host.Localize("inbox.title")).WithStyle("margin: 0;"), "Title")
            // Ordinary buttons: the framework keeps each busy while its bulk act runs and shows the
            // outcome line that names every item that was not put away (Doc/GUI/ButtonPendingState).
            .WithView(Controls.Toolbar
                .WithView(Controls.Button(host.Localize("inbox.delete")).WithAppearance(Appearance.Accent)
                    .WithReactiveClickAction(ctx => Delete(ctx, bands)), "Delete")
                .WithView(Controls.Button(host.Localize("inbox.undo")).WithAppearance(Appearance.Neutral)
                    .WithReactiveClickAction(Undo), "Undo"),
                "Actions");
        foreach (var band in bands)
            stack = stack
                // A provider's own band has no catalog key: its id is its title.
                .WithView(Controls.H3(BandOrder.Contains(band.Band) ? host.Localize(BandTitleKey(band.Band)) : band.Band)
                    .WithStyle("margin: 8px 0 0;"), "Band" + band.Band)
                .WithView(Grid(host, band), "Grid" + band.Band);
        return Observable.Return<UiControl?>(stack);
    }

    /// <summary>One band of the page: its id and its live rows.</summary>
    /// <param name="Band">The band.</param>
    /// <param name="Rows">Its rows, merged from every leg that feeds it.</param>
    internal sealed record BandFeed(string Band, IObservable<ImmutableList<InboxRow>> Rows);

    /// <summary>
    /// The bands of <paramref name="owner"/>'s page, each fed by its legs. A leg that cannot be read
    /// contributes no rows and is LOGGED — one provider's failure must not blank the band the
    /// others feed.
    /// </summary>
    internal static ImmutableList<BandFeed> Bands(LayoutAreaHost host, string owner, string? zone)
    {
        var hub = host.Hub;
        var handlers = hub.ServiceProvider.GetServices<IInboxItemHandler>().ToImmutableArray();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxLayoutArea));
        // Resolved ONCE, on the render turn: the texts a row may carry in its disabled checkbox.
        var running = host.Localize("inbox.blocked.running");
        var noHandler = host.Localize("inbox.blocked.noHandler");
        return Sections(InboxQueries.All(hub.ServiceProvider), owner)
            .GroupBy(section => section.Band, StringComparer.Ordinal)
            .Select(band => new BandFeed(band.Key, band
                .Select(section => hub.GetQuery(InboxRows.QueryId(section), section.Query)
                    .Select(nodes => nodes
                        .Select(node => InboxRows.Of(node, section, zone,
                            BlockedReason(section.Band, InboxItemHandlers.For(handlers, node.NodeType), running, noHandler)))
                        .ToImmutableList())
                    .Catch((Exception ex) =>
                    {
                        logger?.LogWarning(ex, "Inbox leg {Source} ({Band}) could not be read", section.Source, section.Band);
                        return Observable.Return(ImmutableList<InboxRow>.Empty);
                    }))
                .CombineLatest()
                // Deliberately NOT multicast: each leg's synced query is already shared and replays
                // its current set, so the grid's feed and a click's one-off read each subscribe
                // cheaply — and no connection is left for a teardown to miss.
                .Select(InboxRows.Merge)))
            .ToImmutableList();
    }

    /// <summary>
    /// Why a row of <paramref name="band"/> cannot be selected, or null when it can. A running item
    /// is not the viewer's to put away — it leaves the band by finishing — and a type nobody brought
    /// a handler for is listed but never written.
    /// </summary>
    internal static string? BlockedReason(string band, IInboxItemHandler? handler, string running, string noHandler)
        => string.Equals(band, InboxBand.Running, StringComparison.Ordinal) ? running
            : handler is null ? noHandler
            : null;

    /// <summary>
    /// One band's grid: the selection column, what the row is, who brought it, when — and Open,
    /// which hands off to the item's own page. The inbox lists and hands off; it is not a second
    /// mail client or chat client.
    /// </summary>
    private static DataGridControl Grid(LayoutAreaHost host, BandFeed band)
        => band.Rows.Select(rows => (IEnumerable<InboxRow>)rows)
            .BindGrid(InboxRows.GridId(band.Band), host.Localize("inbox.band.empty"),
                reason => host.Localize("inbox.listFailed", reason))
            .WithRowSelection(InboxRows.SelectionId(band.Band), InboxRows.KeyProperty, InboxRows.BlockedReasonProperty)
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(InboxRow.Title).ToCamelCase() }
                .WithTitle(host.Localize("inbox.column.item")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(InboxRow.Source).ToCamelCase() }
                .WithTitle(host.Localize("inbox.column.source")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(InboxRow.When).ToCamelCase() }
                .WithTitle(host.Localize("inbox.column.when")))
            .WithColumn(new TemplateColumnControl(
                    Controls.Button(host.Localize("inbox.open")).WithAppearance(Appearance.Lightweight)
                        .WithReactiveClickAction(ctx =>
                        {
                            if (ctx.RowPath() is { Length: > 0 } path)
                                ctx.NavigateTo("/" + path.TrimStart('/'));
                            return Observable.Return(Unit.Default);
                        }))
                .WithTitle(host.Localize("inbox.column.open")).WithSortable(false))
            .Resizable();

    /// <summary>
    /// Delete: every ticked row of every band, re-checked against the rows as they stand, put away
    /// by its own type's handler under the CLICKER's identity. What was put away becomes the Undo
    /// batch; what was not is named in the outcome line.
    /// </summary>
    private static IObservable<Unit> Delete(UiActionContext ctx, ImmutableList<BandFeed> bands)
    {
        var host = ctx.Host;
        var hub = host.Hub;
        var access = hub.ServiceProvider.GetService<AccessService>();
        // Captured HERE, on the click turn — the writes below are issued after a read, on whatever
        // thread that read answers on, where the ambient context is no longer the clicker's.
        var caller = access?.Context;
        var handlers = hub.ServiceProvider.GetServices<IInboxItemHandler>().ToImmutableArray();
        return bands
            .Select(band => ctx.SelectedRowKeys(InboxRows.SelectionId(band.Band))
                .Zip(band.Rows.Take(1), (keys, rows) => InboxRows.Targets(rows, keys)))
            .Concat()
            .ToList()
            .Select(perBand => perBand.SelectMany(rows => rows).ToImmutableList())
            .SelectMany(targets => targets.Count == 0
                ? Observable.Throw<Unit>(new InvalidOperationException(host.Localize("inbox.nothingSelected")))
                : Act(ctx, access, caller, targets.Select(row => new InboxUndoItem(row.Path, row.NodeType)).ToImmutableList(),
                        handlers, (handler, path) => handler.Dismiss(hub, path), "inbox.progress.deleting")
                    .Do(outcomes =>
                    {
                        var done = outcomes.Where(o => o.Done).Select(o => o.Path).ToImmutableHashSet(StringComparer.Ordinal);
                        host.UpdateData(UndoId, targets.Where(row => done.Contains(row.Path))
                            .Select(row => new InboxUndoItem(row.Path, row.NodeType)).ToImmutableList());
                        // The rows are gone from the grids; so are their ticks.
                        foreach (var band in bands)
                            host.UpdateData(InboxRows.SelectionId(band.Band), new DataGridSelectionState());
                        ctx.ReportSummary(Summary(host, "inbox.summary.deleted", outcomes));
                    })
                    .Select(_ => Unit.Default));
    }

    /// <summary>Undo: brings back what the last Delete put away, through the same handlers.</summary>
    private static IObservable<Unit> Undo(UiActionContext ctx)
    {
        var host = ctx.Host;
        var hub = host.Hub;
        var access = hub.ServiceProvider.GetService<AccessService>();
        var caller = access?.Context;
        var handlers = hub.ServiceProvider.GetServices<IInboxItemHandler>().ToImmutableArray();
        return host.GetDataStream<ImmutableList<InboxUndoItem>>(UndoId).Take(1)
            .SelectMany(batch => batch is not { Count: > 0 }
                ? Observable.Throw<Unit>(new InvalidOperationException(host.Localize("inbox.nothingToUndo")))
                : Act(ctx, access, caller, batch, handlers, (handler, path) => handler.Restore(hub, path), "inbox.progress.restoring")
                    .Do(outcomes =>
                    {
                        // Whatever could not be brought back stays undoable — a second click retries it.
                        var done = outcomes.Where(o => o.Done).Select(o => o.Path).ToImmutableHashSet(StringComparer.Ordinal);
                        host.UpdateData(UndoId, batch.Where(item => !done.Contains(item.Path)).ToImmutableList());
                        ctx.ReportSummary(Summary(host, "inbox.summary.restored", outcomes));
                    })
                    .Select(_ => Unit.Default));
    }

    /// <summary>
    /// Runs <paramref name="act"/> for each item in turn, each call opened under the clicker's
    /// access context. An item whose type has no handler, or whose handler faults, answers for
    /// itself — the batch never stops at the first refusal.
    /// </summary>
    private static IObservable<ImmutableList<InboxItemOutcome>> Act(
        UiActionContext ctx, AccessService? access, AccessContext? caller, ImmutableList<InboxUndoItem> items,
        ImmutableArray<IInboxItemHandler> handlers,
        Func<IInboxItemHandler, string, IObservable<InboxItemOutcome>> act, string progressKey)
        => items
            .Select((item, i) => Observable.Defer(() =>
            {
                ctx.ReportProgress(ctx.Host.Localize(progressKey, i + 1, items.Count), (double)i / items.Count);
                if (InboxItemHandlers.For(handlers, item.NodeType) is not { } handler)
                    return Observable.Return(new InboxItemOutcome(item.Path, false, ctx.Host.Localize("inbox.blocked.noHandler")));
                // 🚨 The scope is opened around the CALL: the handler issues its write inside it
                // (IInboxItemHandler). Around the subscription it would be gone by the first hop.
                using (access?.SwitchAccessContext(caller))
                    return act(handler, item.Path)
                        .Catch((Exception ex) => Observable.Return(new InboxItemOutcome(item.Path, false, ex.Message)));
            }))
            .Concat()
            .ToList()
            .Select(outcomes => outcomes.ToImmutableList());

    /// <summary>The outcome line: how many, and every item that was not done with its reason.</summary>
    internal static string Summary(LayoutAreaHost host, string key, IReadOnlyCollection<InboxItemOutcome> outcomes)
    {
        var failed = outcomes.Where(o => !o.Done).ToList();
        var line = host.Localize(key, outcomes.Count - failed.Count);
        return failed.Count == 0
            ? line
            : line + " " + host.Localize("inbox.summary.failed", failed.Count,
                string.Join("; ", failed.Select(o => $"{o.Path}: {o.Reason}")));
    }

    /// <summary>
    /// The legs of the page: every enabled leg resolved for <paramref name="owner"/>, in band
    /// order, each distinct query once. Pure — the page's shape is testable without a hub.
    /// </summary>
    internal static IReadOnlyList<InboxSection> Sections(IEnumerable<InboxQueryLeg> legs, string owner)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return InboxQueries.Resolve(legs, owner)
            .OrderBy(leg => BandOrder.IndexOf(leg.Band) is var i and >= 0 ? i : BandOrder.Length)
            .ThenBy(leg => leg.Band, StringComparer.Ordinal)
            .ThenBy(leg => leg.Order)
            .Where(leg => seen.Add(leg.Query))
            .Select(leg => new InboxSection(leg.Band, leg.Source, leg.Order, leg.Query))
            .ToArray();
    }

    /// <summary>The localization key of a band's title.</summary>
    internal static string BandTitleKey(string band) => "inbox.band." + band;
}
