using System.Collections.Immutable;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// <para><b>The search is the mesh's own.</b> A typed term is handed to the query engine with every
/// leg, as FREE TEXT (full text and the semantic index), under the OWNER's identity; it is not a
/// filter over the rows that happened to be loaded. It is never spliced into the query grammar: a
/// typed <c>OR</c> or <c>limit:</c> must not be able to replace a leg's own constraints.</para>
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

    /// <summary>
    /// The key of the page's <see cref="InboxActs"/> — what the last Delete put away, and whether an
    /// act is running. A HOST variable, never the data section: the data section is two-way, so a
    /// batch kept there is whatever the client says it is, and Undo would restore paths the viewer
    /// never deleted.
    /// </summary>
    public const string UndoId = "inboxUndo";

    /// <summary>The data id of the notice naming the sources that could not be read; empty when all were.</summary>
    public const string UnreadId = "inboxUnread";

    /// <summary>The data id of the notice shown when a source answered as many rows as it was asked for; empty otherwise.</summary>
    public const string CappedId = "inboxCapped";

    /// <summary>
    /// The data id of the notice counting the items needing the viewer that the source switches hide;
    /// empty when they hide none. A switch must never silently hide an obligation.
    /// </summary>
    public const string HiddenId = "inboxHidden";

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

        var legs = InboxQueries.All(host.Hub.ServiceProvider);
        // A leg that does not read exactly the owner's partition is never run — and never silently.
        foreach (var refused in InboxQueries.Refused(legs, owner))
            host.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxLayoutArea))
                .LogError("Inbox leg {Source} ({Band}) refused: it does not read exactly the viewer's own partition: {Query}",
                    refused.Source, refused.Band, refused.Query);
        var sections = Sections(legs, owner);
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
            host.UpdateData(UnreadId, string.Empty);
            host.UpdateData(CappedId, string.Empty);
            host.UpdateData(HiddenId, string.Empty);
        }
        host.SeedRowSelection(InboxRows.SelectionId);
        // One per page, created on the render turn; the buttons share it.
        if (host.GetVariable<InboxActs>(UndoId) is not { } acts)
        {
            acts = new InboxActs();
            host.SetVariable(UndoId, acts);
            host.RegisterForDisposal(acts);
        }
        // ONE feed, owner-computed: the grid binds it, and Delete re-reads it at the click.
        var rows = Rows(host, sections, owner, access.ViewerZoneId());

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
                    .WithReactiveClickAction(ctx => Delete(ctx, acts, owner, rows)), "Delete")
                .WithView(Controls.Button(host.Localize("inbox.undo")).WithAppearance(Appearance.Neutral)
                    .WithReactiveClickAction(ctx => Undo(ctx, acts)), "Undo"),
                "Actions")
            // Says which sources could not be read — an unreadable source must never look like an
            // empty one. Bound: it appears and clears with the reads, with no re-render.
            .WithView(Controls.Label(new JsonPointerReference(LayoutAreaReference.GetDataPointer(UnreadId))), "Unread")
            // Says when a source has more than the page lists — a capped list must never read as
            // "that is everything".
            .WithView(Controls.Label(new JsonPointerReference(LayoutAreaReference.GetDataPointer(CappedId))), "Capped")
            // Says how many items needing the viewer the switches hide — "nothing here" must never
            // be a switch's doing.
            .WithView(Controls.Label(new JsonPointerReference(LayoutAreaReference.GetDataPointer(HiddenId))), "Hidden")
            .WithView(Grid(host, rows), "Grid"));
    }

    /// <summary>
    /// The grid's rows: every leg's result for the term the viewer typed, merged, then narrowed to
    /// the sources switched on and put in the chosen order. A leg that cannot be read contributes no
    /// rows and is LOGGED — one provider's failure must not blank the list the others feed.
    /// </summary>
    internal static IObservable<ImmutableList<InboxRow>> Rows(
        LayoutAreaHost host, ImmutableArray<InboxSection> sections, string owner, string? zone)
    {
        var hub = host.Hub;
        var handlers = hub.ServiceProvider.GetServices<IInboxItemHandler>().ToImmutableArray();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxLayoutArea));
        // Captured ONCE, on the render turn: the legs are (re)subscribed on data emissions, where
        // the ambient context is no longer the viewer's.
        var access = hub.ServiceProvider.GetService<AccessService>();
        var caller = access?.Context;
        // Resolved ONCE, on the render turn: every text a row may carry.
        var running = host.Localize("inbox.blocked.running");
        var noHandler = host.Localize("inbox.blocked.noHandler");
        var culture = ViewerCulture(host.ViewerLocale());
        var labels = sections.GroupBy(s => s.Source, StringComparer.Ordinal)
            .ToImmutableDictionary(s => s.Key, s => SourceLabel(host, s.First()), StringComparer.Ordinal);
        var bands = sections.Select(s => s.Band).Distinct(StringComparer.Ordinal)
            .Select((band, rank) => (band, rank, Title: BandTitle(host, band, sections)))
            .ToImmutableDictionary(b => b.band, b => (b.rank, b.Title), StringComparer.Ordinal);
        // The rank of the band whose rows are obligations, or none when no leg feeds it.
        int? needsYou = bands.TryGetValue(InboxBand.NeedsYou, out var owed) ? owed.rank : null;
        // 🚨 Distinct by VALUE. The data section re-emits this id whenever ANY id in it changes —
        // the grid's own rows included — and each emission is a freshly deserialized record whose
        // dictionary is a new instance. Without the fingerprint the page would feed on its own
        // writes: rows written → filter "changes" → rows written again, without end.
        var filter = host.GetDataStream<InboxFilter>(InboxRows.FilterId).Select(f => f ?? new InboxFilter())
            .DistinctUntilChanged(InboxRows.Fingerprint, StringComparer.Ordinal);

        // What each leg last answered: its rows, and whether that answer was a failure. A leg that
        // cannot be read contributes no rows, is LOGGED and is NAMED above the grid — one provider's
        // failure must not blank the others, and must not pass for "nothing here" either. It is
        // read again when the viewer changes the search or reopens the page, never on a timer: a
        // provider that stays broken would otherwise be re-queried, and re-logged, for as long as
        // any inbox is open.
        IObservable<(ImmutableList<InboxRow> Rows, string? Unread)> Leg(InboxSection section, string term) =>
            Nodes(hub, section, term, owner, access, caller)
                .Select(nodes => (nodes
                    .Select(node => InboxRows.Of(node, section, zone,
                        BlockedReason(section.Band, InboxItemHandlers.For(handlers, node.NodeType), running, noHandler),
                        bands[section.Band].Title, bands[section.Band].rank, labels[section.Source], culture))
                    .ToImmutableList(), (string?)null))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "Inbox leg {Source} ({Band}) could not be read", section.Source, section.Band);
                    return Observable.Return((ImmutableList<InboxRow>.Empty, (string?)labels[section.Source]));
                });

        IObservable<ImmutableList<InboxRow>> Merged(string term) => sections
            .Select(section => Leg(section, term))
            .CombineLatest()
            .Do(legs =>
            {
                host.UpdateData(UnreadId, UnreadNotice(host, legs.Select(leg => leg.Unread)));
                // Each leg against ITS OWN bound — a provider may ask for fewer than the page's.
                host.UpdateData(CappedId, sections.Zip(legs).Any(leg => leg.Second.Rows.Count >= InboxQueries.EffectiveLimit(leg.First.Query))
                    ? host.Localize("inbox.capped")
                    : string.Empty);
            })
            .Select(legs => InboxRows.Merge(legs.Select(leg => leg.Rows)));

        return filter.Select(f => f.Search.Trim())
            .DistinctUntilChanged(StringComparer.Ordinal)
            // The first read and a cleared box answer at once; a term waits for the typing to pause,
            // and Switch drops the read a newer keystroke superseded.
            .Select(term => term.Length == 0 ? Observable.Return(term) : Observable.Return(term).Delay(SearchQuiet))
            .Switch()
            .Select(Merged)
            .Switch()
            .CombineLatest(filter, (rows, asked) =>
            {
                var shown = InboxRows.Apply(rows, asked, culture);
                var hidden = InboxRows.HiddenObligations(rows, asked, needsYou);
                host.UpdateData(HiddenId, hidden == 0 ? string.Empty : host.Localize("inbox.hidden", hidden));
                return shown;
            });
    }

    /// <summary>
    /// The notice for the sources that could not be read: empty when every leg answered, else the
    /// sentence naming each such source once.
    /// </summary>
    internal static string UnreadNotice(LayoutAreaHost host, IEnumerable<string?> unread)
    {
        var names = unread.OfType<string>().Distinct(StringComparer.Ordinal).ToImmutableList();
        return names.IsEmpty ? string.Empty : host.Localize("inbox.unread", string.Join(", ", names));
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
    /// One leg's nodes — the newest <see cref="InboxQueries.LegLimit"/> of them. With no search term
    /// it is the leg's SHARED synced query (one subscription per leg and viewer, replayed). With a
    /// term it is a live read of the leg plus the term — its own subscription, ended when the term
    /// changes — because a synced query is kept for the process's life per distinct query text, and
    /// a typed term is unbounded.
    ///
    /// <para>🚨 The term is FREE TEXT (<see cref="SearchText"/>) and the bound is pinned on the
    /// request (<see cref="MeshQueryRequest.Limit"/>, which wins over any <c>limit:</c> in the
    /// string): what the viewer types can narrow a leg, never widen it.</para>
    ///
    /// <para>🚨 BOTH branches name the viewer, neither trusts the ambient identity: this runs on a
    /// data emission, where the ambient context is no longer the page's. The
    /// synced query takes the identity it finds when it is CREATED — with none it hands back the
    /// System-loaded stream, unfiltered — so the captured caller is re-established around that
    /// call; the live read takes the viewer as an argument.</para>
    /// </summary>
    internal static IObservable<IEnumerable<MeshNode>> Nodes(
        IMessageHub hub, InboxSection section, string term, string owner, AccessService? access, AccessContext? caller)
    {
        var query = InboxQueries.Bounded(section.Query);
        if (term.Length == 0)
            using (access?.SwitchAccessContext(caller))
                return hub.GetQuery(InboxRows.QueryId(section), query);
        return hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"{query} {SearchText(term)}", owner) with
            {
                Limit = InboxQueries.EffectiveLimit(section.Query),
            })
            .Scan(ImmutableDictionary<string, MeshNode>.Empty.WithComparers(StringComparer.Ordinal), Fold)
            .Select(nodes => (IEnumerable<MeshNode>)nodes.Values);
    }

    /// <summary>
    /// <paramref name="term"/> as one quoted free-text phrase. The quote characters are removed
    /// first, so nothing the viewer types can close the phrase: inside it, <c>OR</c>,
    /// <c>content.status:Archived</c>, <c>limit:all</c> and <c>namespace:…</c> are words to look for,
    /// never grammar — a search may narrow a leg, never replace its constraints.
    /// </summary>
    /// <param name="term">The trimmed, non-empty search text.</param>
    internal static string SearchText(string term)
        => "\"" + new string([.. term.Where(c => c is not ('"' or '\''))]).Trim() + "\"";

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
    /// Delete: every ticked row that the OWNER's own feed lists right now, confined to the owner's
    /// partition, put away by its type's handler under the CLICKER's identity. What was put away
    /// becomes the Undo batch; what was not is named in the outcome line.
    ///
    /// <para>🚨 The selection is the only thing taken from the client, and only as a set of keys.
    /// The rows it is intersected with — and the node type that picks each handler — are
    /// <paramref name="rows"/>, re-read here from the legs: the grid's rows in the data section are
    /// two-way, so pruning the client's keys against them would prove nothing.</para>
    /// </summary>
    private static IObservable<Unit> Delete(UiActionContext ctx, InboxActs acts, string owner, IObservable<ImmutableList<InboxRow>> rows)
    {
        var host = ctx.Host;
        var hub = host.Hub;
        var access = hub.ServiceProvider.GetService<AccessService>();
        // Captured HERE, on the click turn — the writes below are issued after a read, on whatever
        // thread that read answers on, where the ambient context is no longer the clicker's.
        var caller = access?.Context;
        var handlers = hub.ServiceProvider.GetServices<IInboxItemHandler>().ToImmutableArray();
        return acts.Exclusive(host.Localize("inbox.busy"), ctx.SelectedRowKeys(InboxRows.SelectionId)
            .Zip(rows.Take(1), (keys, current) => InboxRows.Targets(current, keys, owner))
            .SelectMany(targets => targets.Count == 0
                ? Observable.Throw<Unit>(new InvalidOperationException(host.Localize("inbox.nothingSelected")))
                : Observable.Defer(() =>
                    {
                        // Each item enters the Undo batch the moment it is put away — never at the
                        // end: a click can be cancelled after some items were written, and those
                        // must stay undoable. The first one put away starts this click's batch.
                        acts.BeginDelete();
                        return Act(ctx, access, caller, targets.Select(row => new InboxUndoItem(row.Path, row.NodeType)).ToImmutableList(),
                            handlers, (handler, path) => handler.Dismiss(hub, path), "inbox.progress.deleting", acts.PutAway);
                    })
                    .Do(outcomes =>
                    {
                        // The rows are gone from the grid; so are their ticks.
                        host.UpdateData(InboxRows.SelectionId, new DataGridSelectionState());
                        ctx.ReportSummary(Summary(host, "inbox.summary.deleted", outcomes));
                    })
                    .Select(_ => Unit.Default)));
    }

    /// <summary>
    /// Undo: brings back what the last Delete put away, through the same handlers. The batch is the
    /// HOST's own record of what this page deleted — never client data — so Undo can only restore
    /// what Delete put away.
    /// </summary>
    private static IObservable<Unit> Undo(UiActionContext ctx, InboxActs acts)
    {
        var host = ctx.Host;
        var hub = host.Hub;
        var access = hub.ServiceProvider.GetService<AccessService>();
        var caller = access?.Context;
        var handlers = hub.ServiceProvider.GetServices<IInboxItemHandler>().ToImmutableArray();
        return acts.Exclusive(host.Localize("inbox.busy"), Observable.Defer(() =>
        {
            // Read once the act holds the page — never before, or a Delete that finished in
            // between would be overwritten from a stale batch.
            var batch = acts.Batch;
            return batch is not { Count: > 0 }
            ? Observable.Throw<Unit>(new InvalidOperationException(host.Localize("inbox.nothingToUndo")))
            // Each item leaves the batch the moment it is brought back, so a cancelled Undo leaves
            // exactly what is still away; whatever could not be brought back stays undoable.
            : Act(ctx, access, caller, batch, handlers, (handler, path) => handler.Restore(hub, path), "inbox.progress.restoring",
                    acts.BroughtBack)
                .Do(outcomes =>
                {
                    ctx.ReportSummary(Summary(host, "inbox.summary.restored", outcomes));
                })
                .Select(_ => Unit.Default);
        }));
    }

    /// <summary>
    /// Runs <paramref name="act"/> for each item in turn, each call opened under the clicker's
    /// access context. An item whose type has no handler, or whose handler faults, answers for
    /// itself — the batch never stops at the first refusal. <paramref name="done"/> is told of each
    /// item the moment it is done, so what was written is recorded even if the click is cancelled
    /// before the batch finishes.
    /// </summary>
    private static IObservable<ImmutableList<InboxItemOutcome>> Act(
        UiActionContext ctx, AccessService? access, AccessContext? caller, ImmutableList<InboxUndoItem> items,
        ImmutableArray<IInboxItemHandler> handlers,
        Func<IInboxItemHandler, string, IObservable<InboxItemOutcome>> act, string progressKey,
        Action<InboxUndoItem> done)
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
                IObservable<InboxItemOutcome> Faulted(Exception ex)
                {
                    // A package's handler may throw anything; its message is never shown.
                    logger?.LogWarning(ex, "Inbox: the {NodeType} handler faulted on {Path}", item.NodeType, item.Path);
                    return Observable.Return(new InboxItemOutcome(item.Path, false));
                }
                using (access?.SwitchAccessContext(caller))
                {
                    // The handler builds its write inside this call (IInboxItemHandler), so it may
                    // also throw here rather than on the returned stream — either is that item's
                    // answer, never the batch's.
                    try
                    {
                        return Detached(act(handler, item.Path).Catch((Func<Exception, IObservable<InboxItemOutcome>>)Faulted),
                            outcome =>
                            {
                                if (outcome.Done)
                                    done(item);
                            });
                    }
                    catch (Exception ex)
                    {
                        return Faulted(ex);
                    }
                }
            }))
            .Concat()
            .Aggregate(ImmutableList<InboxItemOutcome>.Empty, (done, outcome) => done.Add(outcome));
    }

    /// <summary>
    /// Runs <paramref name="write"/> on its OWN subscription, which <paramref name="landed"/> sees to
    /// the end, and hands the click a view of its outcome. A click is cancellable, and cancelling
    /// disposes the click's chain — but a write already posted may still be committed by its owner
    /// afterwards, and an outcome observed only through the click's chain would then be lost: the
    /// item changes state and never enters (or leaves) the Undo batch. Detached, cancelling stops the
    /// items not yet started; it never stops the bookkeeping of one that was.
    /// </summary>
    /// <param name="write">One item's write, already built under the clicker's identity.</param>
    /// <param name="landed">Told of the outcome however the click ends.</param>
    internal static IObservable<InboxItemOutcome> Detached(IObservable<InboxItemOutcome> write, Action<InboxItemOutcome> landed)
    {
        var outcome = new AsyncSubject<InboxItemOutcome>();
        write.Take(1).Do(landed).Subscribe(outcome);
        return outcome;
    }

    /// <summary>The outcome line: how many, and every item that was not done with its reason.</summary>
    internal static string Summary(LayoutAreaHost host, string key, IReadOnlyCollection<InboxItemOutcome> outcomes)
    {
        var failed = outcomes.Where(o => !o.Done).ToImmutableList();
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
    internal static ImmutableArray<InboxSection> Sections(IEnumerable<InboxQueryLeg> legs, string owner)
        =>
        [
            .. InboxQueries.Resolve(legs, owner)
                .OrderBy(leg => BandOrder.IndexOf(leg.Band) is var i and >= 0 ? i : BandOrder.Length)
                .ThenBy(leg => leg.Band, StringComparer.Ordinal)
                .ThenBy(leg => leg.Order)
                // Each distinct query once, under the first leg that asked for it (GroupBy keeps
                // first-occurrence order).
                .GroupBy(leg => leg.Query, StringComparer.Ordinal)
                .Select(same => same.First())
                .Select(leg => new InboxSection(leg.Band, leg.Source, leg.Order, leg.Query)
                {
                    SourceLabelKey = leg.SourceLabelKey, BandLabelKey = leg.BandLabelKey,
                })
        ];

    /// <summary>
    /// A band's title in the viewer's language: core's own bands from core's catalog, a provider's
    /// band from the key its leg gave, else its identifier.
    /// </summary>
    internal static string BandTitle(LayoutAreaHost host, string band, IEnumerable<InboxSection> sections)
        => BandOrder.Contains(band) ? host.Localize(BandTitleKey(band))
            : sections.FirstOrDefault(s => s.Band == band && !string.IsNullOrWhiteSpace(s.BandLabelKey))?.BandLabelKey is { } key
                ? host.Localize(key)
                : band;

    /// <summary>The localization key of a band's title.</summary>
    internal static string BandTitleKey(string band) => "inbox.band." + band;
}

/// <summary>
/// One inbox page's acts: what the last Delete put away (what Undo brings back), and whether a
/// Delete or an Undo is running. Shared by the two buttons, and changed from whichever thread a write
/// completes on — a write still in flight when its click was cancelled can land beside the next
/// click's.
///
/// <para>🚨 Every change goes through ONE serialized reactive channel — a subject of changes, folded
/// in arrival order into the state the page reads (<c>Synchronize().Scan(...)</c>). No change is a
/// read-modify-write of shared state, and none is coordinated by hand: each applies on top of the
/// one before, so every item actually put away stays undoable and every item brought back leaves.
/// The fold runs inside the call that pushes the change, so a caller reads its own change at once.</para>
/// </summary>
internal sealed class InboxActs : IDisposable
{
    /// <summary>The batch, whether the next item put away starts a new one, and whether an act runs.</summary>
    private sealed record State(ImmutableList<InboxUndoItem> Batch, bool Fresh, bool Busy);

    private static readonly State Initial = new(ImmutableList<InboxUndoItem>.Empty, false, false);
    private readonly Subject<Func<State, State>> changes = new();
    private readonly BehaviorSubject<State> state = new(Initial);
    private readonly IDisposable fold;

    /// <summary>Opens the page's channel.</summary>
    public InboxActs()
        => fold = changes.Synchronize().Scan(Initial, (current, change) => change(current)).Subscribe(state);

    /// <summary>What the last Delete put away and Undo has not yet brought back.</summary>
    public ImmutableList<InboxUndoItem> Batch => state.Value.Batch;

    /// <summary>
    /// A Delete is starting: the first item it puts away replaces the previous batch. A Delete that
    /// puts nothing away leaves the previous batch undoable.
    /// </summary>
    public void BeginDelete() => Push(s => s with { Fresh = true });

    /// <summary>Records one item a Delete has just put away — called per item, as it happens.</summary>
    /// <param name="item">The item.</param>
    public void PutAway(InboxUndoItem item)
        => Push(s => s with { Batch = s.Fresh ? [item] : s.Batch.Add(item), Fresh = false });

    /// <summary>Records one item an Undo has just brought back — called per item, as it happens.</summary>
    /// <param name="item">The item.</param>
    public void BroughtBack(InboxUndoItem item)
        => Push(s => s with { Batch = s.Batch.RemoveAll(i => string.Equals(i.Path, item.Path, StringComparison.Ordinal)) });

    /// <summary>
    /// Runs <paramref name="act"/> only when no other Delete or Undo is running on this page, and
    /// refuses it — with a line that says so — when one is. The two are separate buttons, so the
    /// click framework would run them side by side. The claim is a change on the same channel, taken
    /// on subscription and released however the act ends.
    /// </summary>
    /// <param name="refusal">What the refused click says, in the viewer's language.</param>
    /// <param name="act">The Delete or the Undo.</param>
    public IObservable<Unit> Exclusive(string refusal, IObservable<Unit> act)
        => Observable.Defer(() =>
        {
            var claimed = false;
            Push(s =>
            {
                claimed = !s.Busy;
                return s.Busy ? s : s with { Busy = true };
            });
            return claimed
                ? act.Finally(() => Push(s => s with { Busy = false }))
                : Observable.Throw<Unit>(new InvalidOperationException(refusal));
        });

    /// <summary>
    /// Pushes one change. A write can complete after its page was closed; the channel is then
    /// COMPLETED, and a completed subject ignores what arrives after — so the late change is dropped,
    /// never thrown into the write's own pipeline. (Checking "disposed?" first would be a race.)
    /// </summary>
    private void Push(Func<State, State> change) => changes.OnNext(change);

    /// <summary>
    /// Closes the channel: completes it — never disposes it, so a late push is ignored rather than
    /// refused — and ends the fold. The last state stays readable.
    /// </summary>
    public void Dispose()
    {
        changes.OnCompleted();
        fold.Dispose();
    }
}
