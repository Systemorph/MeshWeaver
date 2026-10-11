using System.Collections.Immutable;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The <b>Inbox app</b> — <c>/{user}/Inbox</c>, installed for every user by
/// <c>SeedInboxAppLogonAction</c>: ONE grid for everything that reaches a person — notifications,
/// mail, conversations from every connected channel, what is running for them and what just
/// finished. It is the rendered side of <see cref="InboxQueries"/>: every leg (core's own plus each
/// one a package registered) is resolved for the OWNER of the hub — anchored on their own partition,
/// projected, the invariants <c>InboxLegsAreAnchoredTest</c> pins — and its rows are merged into
/// the grid.
///
/// <para><b>Everything above the grid is data-bound.</b> The page holds ONE piece of view state, an
/// <see cref="InboxFilter"/> in its data section, and each control is bound to a field of it: the
/// search box, the order select, and one switch per source. The grid's rows are a function of that
/// state and the legs — nothing is copied, and no control has a click handler that re-renders the
/// page.</para>
///
/// <para><b>The search is the mesh's own.</b> A typed term is appended to every leg and handed to
/// the query engine (free text, field filters, the semantic index) under the OWNER's identity; it is
/// not a filter over the rows that happened to be loaded.</para>
///
/// <para><b>Select many, then act.</b> The grid carries the platform's own selection column
/// (<see cref="DataGridControl.WithRowSelection"/>); Delete acts on every ticked row and Undo brings
/// the last batch back. What Delete MEANS is the item type's own business — an
/// <see cref="IInboxItemHandler"/> the type's package registered — and it is always soft: nothing is
/// removed from the mesh. A row whose type has no handler, or that is still running, renders a
/// disabled checkbox that says why.</para>
///
/// <para>Only the owner sees it. A User node is public-read, so without the owner check anyone could
/// open another person's inbox address; the legs are RLS-scoped anyway, but the page says "not yours"
/// instead of rendering someone else's empty shell.</para>
/// </summary>
public static class InboxLayoutArea
{
    /// <summary>The layout area on the User hub — also the app record's id (<c>{user}/_App/Inbox</c>).</summary>
    public const string AreaName = "Inbox";

    /// <summary>The data id holding what the last Delete put away — what Undo brings back.</summary>
    public const string UndoId = "inboxUndo";

    /// <summary>How long the page waits after the last keystroke before it asks the query engine.</summary>
    internal static readonly TimeSpan SearchQuiet = TimeSpan.FromMilliseconds(300);

    /// <summary>The order bands are ranked in; a provider's own band follows these, alphabetically.</summary>
    internal static readonly ImmutableArray<string> BandOrder = [InboxBand.NeedsYou, InboxBand.Running, InboxBand.Recent];

    /// <summary>The page — returned at once as a template; its rows are fed.</summary>
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var owner = UserNodeType.OwnerOf(host.Hub.Address.ToString());
        var access = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewer = access.ViewerId();
        if (string.IsNullOrEmpty(viewer) || !string.Equals(viewer, owner, StringComparison.OrdinalIgnoreCase))
            return Observable.Return<UiControl?>(Controls.Markdown(host.Localize("inbox.notYours")));

        var sections = Sections(InboxQueries.All(host.Hub.ServiceProvider), owner);
        var sources = sections.GroupBy(section => section.Source, StringComparer.Ordinal)
            .Select(source => (Id: source.Key, Label: SourceLabel(host, source.First())))
            .ToImmutableList();
        // Seeded ONCE per session: a re-render must not reset what the viewer typed, switched or ticked.
        if (host.TryMarkEditStateInitialized("inbox:" + InboxRows.FilterId))
        {
            host.UpdateData(InboxRows.FilterId, new InboxFilter
            {
                Sources = sources.ToImmutableDictionary(source => InboxRows.SourceKey(source.Id), _ => true),
            });
            host.UpdateData(UndoId, ImmutableList<InboxUndoItem>.Empty);
        }
        host.SeedRowSelection(InboxRows.SelectionId);

        var filterContext = LayoutAreaReference.GetDataPointer(InboxRows.FilterId);
        var switches = Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap: 16px; flex-wrap: wrap;");
        foreach (var source in sources)
            switches = switches.WithView(
                Controls.Switch(new JsonPointerReference($"sources/{InboxRows.SourceKey(source.Id)}")) with
                {
                    DataContext = filterContext, Label = source.Label,
                }, "Show" + InboxRows.SourceKey(source.Id));

        return Observable.Return<UiControl?>(Controls.Stack.WithWidth("100%").WithVerticalGap(12)
            .WithView(Controls.H2(host.Localize("inbox.title")).WithStyle("margin: 0;"), "Title")
            .WithView(Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap: 12px; flex-wrap: wrap; align-items: end;")
                .WithView(Controls.Text(new JsonPointerReference("search")) with
                {
                    DataContext = filterContext, Label = host.Localize("inbox.search"),
                    Placeholder = host.Localize("inbox.search.placeholder"),
                }, "Search")
                .WithView(Controls.Select(new JsonPointerReference("order"),
                    InboxOrder.All.Select(order => (Option)new Option<string>(order, host.Localize("inbox.order." + order))).ToArray()) with
                {
                    DataContext = filterContext, Label = host.Localize("inbox.order"),
                }, "Order"), "Find")
            .WithView(switches, "Sources")
            // Ordinary buttons: the framework keeps each busy while its bulk act runs and shows the
            // outcome line that names every item that was not put away (Doc/GUI/ButtonPendingState).
            .WithView(Controls.Toolbar
                .WithView(Controls.Button(host.Localize("inbox.delete")).WithAppearance(Appearance.Accent)
                    .WithReactiveClickAction(ctx => Delete(ctx, owner)), "Delete")
                .WithView(Controls.Button(host.Localize("inbox.undo")).WithAppearance(Appearance.Neutral)
                    .WithReactiveClickAction(Undo), "Undo"),
                "Actions")
            .WithView(Grid(host, Rows(host, sections, owner, access.ViewerZoneId())), "Grid"));
    }

    /// <summary>
    /// The grid's rows: every leg's result for the term the viewer typed, merged, then narrowed to
    /// the sources switched on and put in the chosen order. A leg that cannot be read contributes no
    /// rows and is LOGGED — one provider's failure must not blank the list the others feed.
    /// </summary>
    internal static IObservable<ImmutableList<InboxRow>> Rows(
        LayoutAreaHost host, IReadOnlyList<InboxSection> sections, string owner, string? zone)
    {
        var hub = host.Hub;
        var handlers = hub.ServiceProvider.GetServices<IInboxItemHandler>().ToImmutableArray();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxLayoutArea));
        // Resolved ONCE, on the render turn: every text a row may carry.
        var running = host.Localize("inbox.blocked.running");
        var noHandler = host.Localize("inbox.blocked.noHandler");
        var culture = ViewerCulture(host.ViewerLocale());
        var labels = sections.GroupBy(s => s.Source, StringComparer.Ordinal)
            .ToImmutableDictionary(s => s.Key, s => SourceLabel(host, s.First()), StringComparer.Ordinal);
        var bands = sections.Select(s => s.Band).Distinct(StringComparer.Ordinal)
            .Select((band, rank) => (band, rank, Title: BandOrder.Contains(band) ? host.Localize(BandTitleKey(band)) : band))
            .ToImmutableDictionary(b => b.band, b => (b.rank, b.Title), StringComparer.Ordinal);
        // 🚨 Distinct by VALUE. The data section re-emits this id whenever ANY id in it changes —
        // the grid's own rows included — and each emission is a freshly deserialized record whose
        // dictionary is a new instance. Without the fingerprint the page would feed on its own
        // writes: rows written → filter "changes" → rows written again, without end.
        var filter = host.GetDataStream<InboxFilter>(InboxRows.FilterId).Select(f => f ?? new InboxFilter())
            .DistinctUntilChanged(InboxRows.Fingerprint, StringComparer.Ordinal);

        IObservable<ImmutableList<InboxRow>> Merged(string term) => sections
            .Select(section => Nodes(hub, section, term, owner)
                .Select(nodes => nodes
                    .Select(node => InboxRows.Of(node, section, zone,
                        BlockedReason(section.Band, InboxItemHandlers.For(handlers, node.NodeType), running, noHandler),
                        bands[section.Band].Title, bands[section.Band].rank, labels[section.Source], culture))
                    .ToImmutableList())
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "Inbox leg {Source} ({Band}) could not be read", section.Source, section.Band);
                    return Observable.Return(ImmutableList<InboxRow>.Empty);
                }))
            .CombineLatest()
            .Select(InboxRows.Merge);

        return filter.Select(f => f.Search.Trim())
            .DistinctUntilChanged(StringComparer.Ordinal)
            // The first read and a cleared box answer at once; a term waits for the typing to pause,
            // and Switch drops the read a newer keystroke superseded.
            .Select(term => term.Length == 0 ? Observable.Return(term) : Observable.Return(term).Delay(SearchQuiet))
            .Switch()
            .Select(Merged)
            .Switch()
            .CombineLatest(filter, InboxRows.Apply);
    }

    /// <summary>
    /// A source's display name: its catalog text in the viewer's language when the provider gave a
    /// key, else its id (right for a name that is the same in every language).
    /// </summary>
    internal static string SourceLabel(LayoutAreaHost host, InboxSection section)
        => string.IsNullOrWhiteSpace(section.SourceLabelKey) ? section.Source : host.Localize(section.SourceLabelKey);

    /// <summary>
    /// The culture a row's date is written in. A language tag the runtime does not know falls back
    /// to invariant — an unreadable preference must never fault a render.
    /// </summary>
    internal static CultureInfo ViewerCulture(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return CultureInfo.InvariantCulture;
        try
        {
            return CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>
    /// One leg's nodes. With no search term it is the leg's SHARED synced query (one subscription
    /// per leg and viewer, replayed). With a term it is a live read of the leg plus the term — its
    /// own subscription, ended when the term changes — because a synced query is kept for the
    /// process's life per distinct query text, and a typed term is unbounded.
    /// </summary>
    internal static IObservable<IEnumerable<MeshNode>> Nodes(IMessageHub hub, InboxSection section, string term, string owner)
        => term.Length == 0
            ? hub.GetQuery(InboxRows.QueryId(section), section.Query)
            // 🚨 The viewer is an ARGUMENT, never the ambient identity: this is subscribed on a data
            // emission, where the ambient context is no longer the page's — and the term is the
            // viewer's own text, so it must be evaluated with exactly the viewer's rights.
            : hub.ServiceProvider.GetRequiredService<IMeshService>()
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"{section.Query} {term}", owner))
                .Scan(ImmutableDictionary<string, MeshNode>.Empty.WithComparers(StringComparer.Ordinal), Fold)
                .Select(nodes => (IEnumerable<MeshNode>)nodes.Values);

    /// <summary>
    /// A live read answers its first set whole and every later change as a delta; this folds them
    /// into "what the leg selects now".
    /// </summary>
    internal static ImmutableDictionary<string, MeshNode> Fold(ImmutableDictionary<string, MeshNode> current, QueryResultChange<MeshNode> change)
        => change.ChangeType switch
        {
            QueryChangeType.Removed => current.RemoveRange(change.Items.Select(n => n.Path)),
            QueryChangeType.Added or QueryChangeType.Updated => current.SetItems(change.Items.Select(n => KeyValuePair.Create(n.Path, n))),
            // Initial — and a reset: the set itself.
            _ => ImmutableDictionary<string, MeshNode>.Empty.WithComparers(StringComparer.Ordinal)
                .SetItems(change.Items.Select(n => KeyValuePair.Create(n.Path, n))),
        };

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
    /// The grid: the selection column, what the row is, who brought it, which band it is in, when —
    /// and Open, which hands off to the item's own page. The inbox lists and hands off; it is not a
    /// second mail client or chat client.
    /// </summary>
    private static DataGridControl Grid(LayoutAreaHost host, IObservable<ImmutableList<InboxRow>> rows)
    {
        var grid = rows.Select(set => (IEnumerable<InboxRow>)set)
            .BindGrid(InboxRows.GridId, host.Localize("inbox.band.empty"), reason => host.Localize("inbox.listFailed", reason))
            .WithRowSelection(InboxRows.SelectionId, InboxRows.KeyProperty, InboxRows.BlockedReasonProperty);
        foreach (var (property, key) in new[]
                 {
                     (nameof(InboxRow.Title), "inbox.column.item"),
                     (nameof(InboxRow.Source), "inbox.column.source"),
                     (nameof(InboxRow.Band), "inbox.column.status"),
                     (nameof(InboxRow.When), "inbox.column.when"),
                 })
            // The order is the select's: a header sort would be a second, unbound ordering.
            grid = grid.WithColumn(new PropertyColumnControl<string> { Property = property.ToCamelCase() }
                .WithTitle(host.Localize(key)).WithSortable(false));
        return grid
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
    }

    /// <summary>
    /// Delete: every ticked row, re-checked against the rows the grid shows and confined to the
    /// owner's partition, put away by its own type's handler under the CLICKER's identity. What was
    /// put away becomes the Undo batch; what was not is named in the outcome line.
    /// </summary>
    private static IObservable<Unit> Delete(UiActionContext ctx, string owner)
    {
        var host = ctx.Host;
        var hub = host.Hub;
        var access = hub.ServiceProvider.GetService<AccessService>();
        // Captured HERE, on the click turn — the writes below are issued after a read, on whatever
        // thread that read answers on, where the ambient context is no longer the clicker's.
        var caller = access?.Context;
        var handlers = hub.ServiceProvider.GetServices<IInboxItemHandler>().ToImmutableArray();
        return ctx.SelectedRowKeys(InboxRows.SelectionId)
            // The rows the grid is SHOWING — what the viewer ticked against — read once.
            .Zip(host.GetDataStream<ImmutableList<InboxRow>>(InboxRows.GridId).Take(1),
                (keys, rows) => InboxRows.Targets(rows ?? [], keys, owner))
            .SelectMany(targets => targets.Count == 0
                ? Observable.Throw<Unit>(new InvalidOperationException(host.Localize("inbox.nothingSelected")))
                : Act(ctx, access, caller, targets.Select(row => new InboxUndoItem(row.Path, row.NodeType)).ToImmutableList(),
                        handlers, (handler, path) => handler.Dismiss(hub, path), "inbox.progress.deleting")
                    .Do(outcomes =>
                    {
                        var done = outcomes.Where(o => o.Done).Select(o => o.Path).ToImmutableHashSet(StringComparer.Ordinal);
                        host.UpdateData(UndoId, targets.Where(row => done.Contains(row.Path))
                            .Select(row => new InboxUndoItem(row.Path, row.NodeType)).ToImmutableList());
                        // The rows are gone from the grid; so are their ticks.
                        host.UpdateData(InboxRows.SelectionId, new DataGridSelectionState());
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
    {
        var logger = ctx.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxLayoutArea));
        return items
            .Select((item, i) => Observable.Defer(() =>
            {
                ctx.ReportProgress(ctx.Host.Localize(progressKey, i + 1, items.Count), (double)i / items.Count);
                if (InboxItemHandlers.For(handlers, item.NodeType) is not { } handler)
                    return Observable.Return(new InboxItemOutcome(item.Path, false, ctx.Host.Localize("inbox.blocked.noHandler")));
                // 🚨 The scope is opened around the CALL: the handler issues its write inside it
                // (IInboxItemHandler). Around the subscription it would be gone by the first hop.
                using (access?.SwitchAccessContext(caller))
                    return act(handler, item.Path)
                        .Catch((Exception ex) =>
                        {
                            // A package's handler may throw anything; its message is never shown.
                            logger?.LogWarning(ex, "Inbox: the {NodeType} handler faulted on {Path}", item.NodeType, item.Path);
                            return Observable.Return(new InboxItemOutcome(item.Path, false));
                        });
            }))
            .Concat()
            .Aggregate(ImmutableList<InboxItemOutcome>.Empty, (done, outcome) => done.Add(outcome));
    }

    /// <summary>The outcome line: how many, and every item that was not done with its reason.</summary>
    internal static string Summary(LayoutAreaHost host, string key, IReadOnlyCollection<InboxItemOutcome> outcomes)
    {
        var failed = outcomes.Where(o => !o.Done).ToList();
        var line = host.Localize(key, outcomes.Count - failed.Count);
        return failed.Count == 0
            ? line
            : line + " " + host.Localize("inbox.summary.failed", failed.Count,
                string.Join("; ", failed.Select(o =>
                    $"{o.Path}: {(string.IsNullOrWhiteSpace(o.Reason) ? host.Localize("inbox.failed.generic") : o.Reason)}")));
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
            .Select(leg => new InboxSection(leg.Band, leg.Source, leg.Order, leg.Query) { SourceLabelKey = leg.SourceLabelKey })
            .ToArray();
    }

    /// <summary>The localization key of a band's title.</summary>
    internal static string BandTitleKey(string band) => "inbox.band." + band;
}
