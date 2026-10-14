using System.Collections.Immutable;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Application.Styles;
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

    /// <summary>The data id of the side menu's options — All, then each category with its count.</summary>
    public const string CategoryOptionsId = "inboxCategoryOptions";

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
            .Select(source => (Id: source.Key, Label: SourceLabel(host, source.First()),
                Shown: source.Any(s => s.ShownByDefault),
                Icon: source.Select(s => s.SourceIcon).FirstOrDefault(i => i.Length > 0) ?? string.Empty))
            .ToImmutableList();
        // Seeded ONCE per session: a re-render must not reset what the viewer typed, switched or ticked.
        if (host.TryMarkEditStateInitialized("inbox:" + InboxRows.FilterId))
        {
            host.UpdateData(InboxRows.FilterId, new InboxFilter
            {
                // A source is shown at first when any of its legs says so — the noisy ones are a
                // switch away, never removed.
                Sources = sources.ToImmutableDictionary(source => InboxRows.SourceKey(source.Id), source => source.Shown),
            });
            host.UpdateData(UnreadId, string.Empty);
            host.UpdateData(CappedId, string.Empty);
            host.UpdateData(HiddenId, string.Empty);
            host.UpdateData(CategoryOptionsId, CategoryOptions(host.Localize("inbox.category.all"), CategoriesOf(host, sections), InboxRows.CategoryCounts([])));
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
        // One icon toggle per source, always in view: a source is switched on or off with one click,
        // its name the tooltip. Bound to the same filter field the settings menu's switches are.
        var toggles = Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap: 4px; flex-wrap: wrap; align-items: center;");
        // The labelled switches, kept in the settings menu for the viewer who wants names, not symbols.
        var switches = Controls.Stack.WithVerticalGap(4);
        foreach (var source in sources)
        {
            var key = InboxRows.SourceKey(source.Id);
            toggles = toggles.WithView(
                Controls.ToggleButton(new JsonPointerReference($"sources/{key}")) with
                {
                    DataContext = filterContext, Label = source.Label,
                    // A source without a symbol shows its NAME: a single letter was read as an icon
                    // name, found no icon and rendered an empty button.
                    Icon = source.Icon.Length > 0 ? source.Icon : null,
                }, "Toggle" + key);
            switches = switches.WithView(
                Controls.Switch(new JsonPointerReference($"sources/{key}")) with
                {
                    DataContext = filterContext,
                    Label = source.Icon.Length > 0 ? $"{source.Icon} {source.Label}" : source.Label,
                }, "Show" + key);
        }

        // Settings out of the way: the labelled source switches sit behind one icon. There is no
        // order to choose — the list is always newest first (InboxRows.Apply).
        var settings = Controls.MenuItem(host.Localize("inbox.settings"), FluentIcons.Settings()).WithIconOnly()
            .WithView(switches, "Sources");

        // The side menu: All, then one entry per category, in the page's band order — a mail
        // client's folders. Re-rendered from the counts the rows publish; a click picks the category.
        // The side menu: the platform's one-choice control, its value bound to the filter's
        // category (two-way, like the search) and its options fed with the live counts.
        var menu = (Controls.RadioGroup(new JsonPointerReference("category"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(CategoryOptionsId)),
                // The CLR type NAME the client resolves through its registry — "String", never the
                // C# keyword: "string" is not a type name, and the menu failed with "Type not found".
                typeof(string).Name)
            with { DataContext = filterContext })
            .WithOrientation(Orientation.Vertical)
            .WithStyle("width: 200px; flex: none;");

        var main = Controls.Stack.WithWidth("100%").WithVerticalGap(8)
            .WithView(Controls.H2(host.Localize("inbox.title")).WithStyle("margin: 0;"), "Title")
            // One command row, as a mail client has it: search, the acts, the source toggles, settings.
            .WithView(Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap: 8px; flex-wrap: wrap; align-items: end;")
                .WithView(Controls.Text(new JsonPointerReference("search")) with
                {
                    DataContext = filterContext, Label = host.Localize("inbox.search"),
                    Placeholder = host.Localize("inbox.search.placeholder"),
                }, "Search")
                // Ordinary buttons: the framework keeps each busy while its bulk act runs and shows
                // the outcome line that names every item that was not put away
                // (Doc/GUI/ButtonPendingState). Backspace / Delete press Delete, as in a mail client
                // — never while typing in the search box.
                .WithView(Controls.Button(host.Localize("inbox.delete")).WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Delete()).WithShortcut(DeleteShortcut)
                    .WithReactiveClickAction(ctx => Delete(ctx, acts, owner, rows)), "Delete")
                .WithView(Controls.Button(host.Localize("inbox.undo")).WithAppearance(Appearance.Neutral)
                    .WithReactiveClickAction(ctx => Undo(ctx, acts)), "Undo")
                // Decide every ticked approval request — the approval's own type decides and
                // authorizes, as the clicker (InboxApprovals). Other ticked rows are left alone.
                .WithView(Controls.Button(host.Localize("inbox.approve")).WithAppearance(Appearance.Neutral)
                    .WithIconStart(FluentIcons.Checkmark())
                    .WithReactiveClickAction(ctx => Decide(ctx, acts, owner, rows, ApprovalDecisions.Approve)), "Approve")
                .WithView(Controls.Button(host.Localize("inbox.reject")).WithAppearance(Appearance.Neutral)
                    .WithIconStart(FluentIcons.Dismiss())
                    .WithReactiveClickAction(ctx => Decide(ctx, acts, owner, rows, ApprovalDecisions.Reject)), "Reject")
                .WithView(toggles, "Sources")
                .WithView(settings, "Settings"), "Commands")
            // Says which sources could not be read — an unreadable source must never look like an
            // empty one. Bound: it appears and clears with the reads, with no re-render.
            .WithView(Controls.Label(new JsonPointerReference(LayoutAreaReference.GetDataPointer(UnreadId))), "Unread")
            // Says when a source has more than the page lists — a capped list must never read as
            // "that is everything".
            .WithView(Controls.Label(new JsonPointerReference(LayoutAreaReference.GetDataPointer(CappedId))), "Capped")
            // Says how many items needing the viewer the switches hide — "nothing here" must never
            // be a switch's doing.
            .WithView(Controls.Label(new JsonPointerReference(LayoutAreaReference.GetDataPointer(HiddenId))), "Hidden")
            .WithView(Grid(host, rows), "Grid");
        return Observable.Return<UiControl?>(Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithWidth("100%").WithStyle("gap: 16px; align-items: flex-start;")
            .WithView(menu, "Categories")
            .WithView(main.WithStyle("flex: 1; min-width: 0;"), "Main"));
    }

    /// <summary>The page's categories — key, label in the viewer's language, symbol — in band order.</summary>
    internal static ImmutableList<(string Key, string Label, string Icon)> CategoriesOf(LayoutAreaHost host, ImmutableArray<InboxSection> sections)
        => [.. sections.GroupBy(s => s.Category, StringComparer.Ordinal)
            .Select(c => (Key: c.Key, Label: Label(key => host.Localize(key), c.First().CategoryLabelKey, c.Key),
                Icon: c.Select(s => s.SourceIcon).FirstOrDefault(i => i.Length > 0) ?? string.Empty))];

    /// <summary>
    /// The side menu's options: All (empty key), then each category with its symbol and count — the
    /// unread count when it has unread rows, else all its rows. Pure.
    /// </summary>
    internal static ImmutableList<Option> CategoryOptions(string allLabel,
        ImmutableList<(string Key, string Label, string Icon)> categories,
        ImmutableDictionary<string, (int Total, int Unread)> counts)
    {
        static string Text(string icon, string label, int count)
            => (icon.Length > 0 ? $"{icon} {label}" : label) + (count > 0 ? $"  {count}" : string.Empty);
        static int Shown((int Total, int Unread) c) => c.Unread > 0 ? c.Unread : c.Total;
        var all = counts.Values.Aggregate((Total: 0, Unread: 0), (sum, c) => (sum.Total + c.Total, sum.Unread + c.Unread));
        return
        [
            new Option<string>(string.Empty, Text("📥", allLabel, Shown(all))),
            .. categories.Select(c => (Option)new Option<string>(c.Key,
                Text(c.Icon, c.Label, counts.TryGetValue(c.Key, out var n) ? Shown(n) : 0))),
        ];
    }

    /// <summary>The keys that press Delete — as a mail client: Backspace or Delete, with the rows ticked.</summary>
    internal const string DeleteShortcut = "Backspace|Delete";

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
        // Resolved ONCE, on the render turn: every text a row may carry.
        var running = host.Localize("inbox.blocked.running");
        var noHandler = host.Localize("inbox.blocked.noHandler");
        var culture = ViewerCulture(host.ViewerLocale());
        var allLabel = host.Localize("inbox.category.all");
        var categories = CategoriesOf(host, sections);
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
            Nodes(hub, section, term, owner)
                .Select(nodes => (nodes
                    .Select(node => InboxRows.Of(node, section, zone,
                        BlockedReason(section.Band, InboxItemHandlers.For(handlers, node.NodeType), running, noHandler),
                        bands[section.Band].Title, bands[section.Band].rank, labels[section.Source], culture,
                        hub.JsonSerializerOptions))
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
            .Select(legs => InboxRows.Merge(legs.Select(leg => leg.Rows)))
            .Do(merged => host.UpdateData(CategoryOptionsId, CategoryOptions(allLabel, categories, InboxRows.CategoryCounts(merged))));

        // The date bounds (before:/after:) are applied to the rows by InboxRows.Apply; only the
        // remaining text goes to the sources.
        return filter.Select(f => InboxRows.SplitDates(f.Search).Text.Trim())
            .DistinctUntilChanged(StringComparer.Ordinal)
            // The first read and a cleared box answer at once; a term waits for the typing to pause,
            // and Switch drops the read a newer keystroke superseded.
            .Select(term => term.Length == 0 ? Observable.Return(term) : Observable.Return(term).Delay(SearchQuiet))
            .Switch()
            .Select(Merged)
            .Switch()
            .CombineLatest(filter, (rows, asked) =>
            {
                var shown = InboxRows.Apply(rows, asked, asked.Category);
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
        => Label(key => host.Localize(key), section.SourceLabelKey, section.Source);

    /// <summary>
    /// The catalog text for <paramref name="key"/>, or <paramref name="fallback"/> when no key was
    /// given or the catalog does not define it. A package can name a key a NEWER platform adds; on a
    /// platform that lacks it the viewer reads the provider's own name, never the raw key.
    /// </summary>
    internal static string Label(Func<string, string> localize, string? key, string fallback)
        => !string.IsNullOrWhiteSpace(key) && LocalizationCatalog.ContainsKey(key) ? localize(key) : fallback;

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
    /// One leg's nodes — the newest <see cref="InboxQueries.LegLimit"/> of them, as a live read of
    /// the leg (plus the term, when there is one) that lives exactly as long as the page's
    /// subscription: it ends when the term changes or the page closes.
    ///
    /// <para>🚨 Never the process-wide synced query (<c>GetQuery</c>): that cache is kept for the
    /// process's life per distinct query text, and every leg here is resolved for its OWNER — so it
    /// would keep one live subscription and snapshot per leg for every person who ever opened their
    /// inbox, growing with cumulative visitors rather than open pages.</para>
    ///
    /// <para>🚨 The term is FREE TEXT (<see cref="SearchText"/>) and the bound is pinned on the
    /// request (<see cref="MeshQueryRequest.Limit"/>, which wins over any <c>limit:</c> in the
    /// string): what the viewer types can narrow a leg, never widen it. The read names the viewer as
    /// an argument — it runs on a data emission, where the ambient identity is no longer the page's.</para>
    /// </summary>
    internal static IObservable<IEnumerable<MeshNode>> Nodes(IMessageHub hub, InboxSection section, string term, string owner)
        => hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                term.Length == 0
                    ? InboxQueries.Bounded(section.Query, section.TimeField)
                    : $"{InboxQueries.Bounded(section.Query, section.TimeField)} {SearchText(term)}",
                owner) with
            {
                Limit = InboxQueries.EffectiveLimit(section.Query),
            })
            .Scan(ImmutableDictionary<string, MeshNode>.Empty.WithComparers(StringComparer.Ordinal), Fold)
            .Select(nodes => (IEnumerable<MeshNode>)nodes.Values);

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
    /// The grid: the selection column, the source's symbol, what the row is, who brought it, when —
    /// and Open, which hands off to the item's own page; an unread row is bold (its row class). The inbox lists and hands off; it is not a
    /// second mail client or chat client.
    /// </summary>
    private static DataGridControl Grid(LayoutAreaHost host, IObservable<ImmutableList<InboxRow>> rows)
    {
        var grid = rows.Select(set => (IEnumerable<InboxRow>)set)
            .BindGrid(InboxRows.GridId, host.Localize("inbox.band.empty"), reason => host.Localize("inbox.listFailed", reason))
            .WithRowSelection(InboxRows.SelectionId, InboxRows.KeyProperty, InboxRows.BlockedReasonProperty)
            // Unread rows carry their class (bold), as a mail client shows new mail.
            .WithRowClassProperty(InboxRows.RowClassProperty)
            // The source's symbol first, so a glance tells mail from a message from an approval.
            .WithColumn(new TemplateColumnControl(Controls.Icon(new ContextProperty(InboxRows.SourceIconProperty)))
                .WithTitle(string.Empty).WithWidth("40px").WithSortable(false));
        // Outlook's columns: what, from whom, when. Ordered by date always: a header sort would be a
        // second, unbound ordering.
        foreach (var (property, key) in new[]
                 {
                     (nameof(InboxRow.Title), "inbox.column.item"),
                     (nameof(InboxRow.From), "inbox.column.source"),
                     (nameof(InboxRow.When), "inbox.column.when"),
                 })
            grid = grid.WithColumn(new PropertyColumnControl<string> { Property = property.ToCamelCase() }
                .WithTitle(host.Localize(key)).WithSortable(false));
        return grid
            // A click on a row OPENS it, as in a mail client: a mail in its provider's own client, a
            // mesh item at its page. (Ticking a box and shift/cmd-clicks are selection gestures the
            // grid keeps to itself.) Paged, newest first.
            .WithClickAction(OpenRow)
            .WithShowHover()
            .WithItemsPerPage(50)
            .WithShowPageSizeSelector()
            .Resizable();
    }

    /// <summary>Opens the clicked row: its external link when it has one, else its node's page.</summary>
    private static void OpenRow(UiActionContext ctx)
    {
        var options = ctx.Hub.JsonSerializerOptions;
        // The payload is an object boundary: it may arrive typed or as JSON, so it is read through
        // the shared accessor, never a CLR pattern that drops the JSON envelope.
        if (OpenTarget(ctx.Payload.As<DataGridCellClick>(options)?.Item, options) is { } target)
            ctx.NavigateTo(target.Url, forceLoad: target.External);
    }

    /// <summary>
    /// Where a clicked row goes: its https <see cref="InboxRow.Link"/> (a full page load — it leaves the
    /// portal), else <c>/{path}</c>; null for a row without either, or one that cannot be read as a row.
    /// The row arrives typed or as JSON (read with the shared <c>As</c> accessor). Pure.
    /// </summary>
    internal static (string Url, bool External)? OpenTarget(object? item, System.Text.Json.JsonSerializerOptions options)
    {
        var row = item.As<InboxRow>(options);
        if (row is null)
            return null;
        if (Uri.TryCreate(row.Link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            return (row.Link, true);
        return row.Path is { Length: > 0 } path ? ("/" + path.TrimStart('/'), false) : null;
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
    /// Approve all / Reject all: every ticked row that is an approval request (the Approvals source),
    /// re-checked against the owner's own feed exactly as Delete is, decided one at a time as the
    /// clicker. Not undoable — a decision is the approval's own record — so nothing enters the Undo
    /// batch; a row that is not an approval request is not touched, and the outcome line names every
    /// request that was not decided, with the reason its type gave.
    /// </summary>
    private static IObservable<Unit> Decide(UiActionContext ctx, InboxActs acts, string owner,
        IObservable<ImmutableList<InboxRow>> rows, string decision)
    {
        var host = ctx.Host;
        var hub = host.Hub;
        var access = hub.ServiceProvider.GetService<AccessService>();
        var caller = access?.Context;
        var handlers = hub.ServiceProvider.GetServices<IInboxItemHandler>().ToImmutableArray();
        var noAnswer = host.Localize("inbox.approval.noAnswer");
        var approve = decision == ApprovalDecisions.Approve;
        return acts.Exclusive(host.Localize("inbox.busy"), ctx.SelectedRowKeys(InboxRows.SelectionId)
            .Zip(rows.Take(1), (keys, current) => InboxRows.Targets(current, keys, owner)
                .Where(row => string.Equals(row.SourceId, InboxQueries.ApprovalsSource, StringComparison.Ordinal))
                .ToImmutableList())
            .SelectMany(targets => targets.Count == 0
                ? Observable.Throw<Unit>(new InvalidOperationException(host.Localize("inbox.nothingToDecide")))
                : Act(ctx, access, caller, DecideBatch(targets).Select(row => new InboxUndoItem(row.Path, row.NodeType)).ToImmutableList(),
                        handlers, (_, path) => InboxApprovals.Decide(hub, path, decision, noAnswer),
                        approve ? "inbox.progress.approving" : "inbox.progress.rejecting", _ => { })
                    .Do(outcomes =>
                    {
                        host.UpdateData(InboxRows.SelectionId, new DataGridSelectionState());
                        var summary = Summary(host, approve ? "inbox.summary.approved" : "inbox.summary.rejected", outcomes);
                        // The rest of a selection larger than one step-up covers is left untouched — said.
                        if (targets.Count > outcomes.Count)
                            summary += " " + host.Localize("inbox.decide.capped", targets.Count - outcomes.Count, StepUpPaths.MaxTargets);
                        ctx.ReportSummary(summary);
                        // 🚨 ONE step-up for the batch: every decision held for the approver's
                        // confirmation is confirmed together, then the approver comes back here.
                        // A FULL page load — the step-up endpoint is an MVC route the circuit never reaches.
                        if (StepUpOf(outcomes) is { Count: > 0 } held)
                            ctx.NavigateTo(StepUpPaths.StartUrl(held, "/" + owner.TrimStart('/') + "/" + AreaName), forceLoad: true);
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

    /// <summary>
    /// The approval requests ONE click decides: the first <see cref="StepUpPaths.MaxTargets"/> in page
    /// order. Every held decision of the click goes into one step-up, and the step-up endpoint refuses
    /// a larger batch — deciding more would hold decisions the approver could never confirm.
    /// </summary>
    internal static ImmutableList<T> DecideBatch<T>(IReadOnlyCollection<T> targets)
        => [.. targets.Take(StepUpPaths.MaxTargets)];

    /// <summary>
    /// The step-up targets a batch still waits for — each held decision once, in batch order. Empty
    /// when nothing was held, so the inbox only leaves for a step-up when there is one to give.
    /// </summary>
    internal static ImmutableList<StepUpTarget> StepUpOf(IEnumerable<InboxItemOutcome> outcomes)
        => [.. outcomes.Select(o => o.StepUp).OfType<StepUpTarget>().DistinctBy(t => t.Key)];

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
                    Unread = leg.Unread, ShownByDefault = leg.ShownByDefault, SourceIcon = leg.SourceIcon ?? string.Empty,
                    TimeField = string.IsNullOrWhiteSpace(leg.TimeField) ? null : leg.TimeField,
                    Category = string.IsNullOrWhiteSpace(leg.Category) ? leg.Source : leg.Category,
                    CategoryLabelKey = string.IsNullOrWhiteSpace(leg.CategoryLabelKey) ? leg.SourceLabelKey : leg.CategoryLabelKey,
                })
        ];

    /// <summary>
    /// A band's title in the viewer's language: core's own bands from core's catalog, a provider's
    /// band from the key its leg gave, else its identifier.
    /// </summary>
    internal static string BandTitle(LayoutAreaHost host, string band, IEnumerable<InboxSection> sections)
        => BandOrder.Contains(band) ? host.Localize(BandTitleKey(band))
            : Label(key => host.Localize(key), sections.FirstOrDefault(s => s.Band == band && !string.IsNullOrWhiteSpace(s.BandLabelKey))?.BandLabelKey, band);

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
