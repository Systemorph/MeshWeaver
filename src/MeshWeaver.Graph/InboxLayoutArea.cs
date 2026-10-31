using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Reactive;
using System.Reactive.Concurrency;
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

    /// <summary>
    /// The data id of the notice naming the sources that have not answered within <see cref="LegAnswerWait"/>; empty
    /// when every source answered. The list shows what the others sent meanwhile — one silent source never holds it.
    /// </summary>
    public const string SlowId = "inboxSlow";

    /// <summary>How long one source may take to answer before the list goes on without it and names it.</summary>
    internal static readonly TimeSpan LegAnswerWait = TimeSpan.FromSeconds(10);

    /// <summary>The data id of the notice shown when a source answered as many rows as it was asked for; empty otherwise.</summary>
    public const string CappedId = "inboxCapped";

    /// <summary>
    /// The data id of the notice counting the items needing the viewer that the source switches hide;
    /// empty when they hide none. A switch must never silently hide an obligation.
    /// </summary>
    public const string HiddenId = "inboxHidden";

    /// <summary>The data id of the Only select's options — all sources, then each source by name.</summary>
    public const string SourceOptionsId = "inboxSourceOptions";

    /// <summary>The data id of the Account select's options — all addresses, then each address the rows name.</summary>
    public const string AccountOptionsId = "inboxAccountOptions";

    /// <summary>The data id of the side menu's tag options — all tags, Important, then each tag with its count.</summary>
    public const string TagOptionsId = "inboxTagOptions";

    /// <summary>
    /// The data id of the notice counting the ignorable items the switch hides; empty when it hides none.
    /// </summary>
    public const string IgnorableId = "inboxIgnorable";

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
        var zone = access.ViewerZoneId();
        // An item named in the address: its reading view, inside the portal (InboxReadingView).
        if (host.Reference.Id is { } id && LayoutAreaReference.NormalizeScalar(id) is { Length: > 0 })
            return InboxReadingView.Render(host, owner, zone, InboxReadingView.PathOf(id));
        // The TEMPLATE, at once: every source's toggle and switch is declared, each with its style
        // BOUND to data. Whether a source is connected is DATA the rows feed publishes
        // (PublishConnected) — a source the viewer has not connected is hidden and never read
        // (owner, 2026-10-15: "must get only configured sources"). Never wait for the connection
        // reads before emitting (Doc/GUI/DataBinding → "Templates first, data later").
        return Observable.Return<UiControl?>(Page(host, owner, zone, Sections(legs, owner)));
    }

    /// <summary>
    /// <paramref name="build"/> applied to each of <paramref name="source"/>'s answers inside
    /// <paramref name="viewer"/>'s identity, on whichever thread the answer lands.
    /// </summary>
    /// <param name="source">The answers the page waits for.</param>
    /// <param name="services">Resolves the mesh's access service.</param>
    /// <param name="viewer">The identity the area was opened for; null passes through.</param>
    /// <param name="build">Builds the output from one answer.</param>
    internal static IObservable<TOut> InViewerContext<TIn, TOut>(
        IObservable<TIn> source, IServiceProvider services, AccessContext? viewer, Func<TIn, TOut> build)
        => source.CarryAccessContext(services, viewer).Select(build);

    /// <summary>How long the page waits for one source's connection query before it lists the source anyway.</summary>
    internal static readonly TimeSpan ConnectionWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The sources <paramref name="owner"/> has connected: every source with a leg that needs no
    /// connection (<see cref="InboxQueryLeg.AlwaysOn"/>), and every other whose connection query
    /// (<see cref="InboxQueryLeg.ConnectedWhen"/>) finds a node. A source that is neither is not connected.
    /// Answers ONCE, when every source has answered.
    /// <para>🚨 A connection query that cannot be read, or that does not answer within
    /// <see cref="ConnectionWait"/>, LISTS its source — and is logged: an unreadable connection must
    /// not hide a source the viewer did connect. Its leg then says itself whether it could be read.</para>
    /// </summary>
    internal static IObservable<ImmutableHashSet<string>> Connected(IMessageHub hub, ImmutableArray<InboxSection> sections, string owner)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxLayoutArea));
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        IObservable<bool> Probe(string source, string query) =>
            mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(InboxQueries.ConnectionQuery(query), owner) with { Limit = 1 })
                .Take(1)
                .Select(change =>
                {
                    var found = FoundConnection(change);
                    if (found && change.Items.Count == 0)
                        logger?.LogWarning(
                            "Inbox: the connection read for {Source} was incomplete (silent providers: {Silent}; snapshot incomplete: {Incomplete}) — {Query}; listing it",
                            source, string.Join(", ", change.SilentProviders ?? []), change.SnapshotIncomplete, query);
                    return found;
                })
                // A read that ends without answering is no answer — listed, like a failure.
                .DefaultIfEmpty(true)
                .Timeout(ConnectionWait)
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "Inbox: could not tell whether {Source} is connected ({Query}); listing it", source, query);
                    return Observable.Return(true);
                });
        var bySource = sections.GroupBy(section => section.Source, StringComparer.Ordinal)
            .Select(source => source.Any(s => s.AlwaysOn)
                ? Observable.Return((Source: source.Key, Connected: true))
                // 🚨 No connection query and not always on: nothing says the viewer configured it, so it is NOT connected.
                : !source.Any(s => s.ConnectedWhen is not null)
                    ? Observable.Return((Source: source.Key, Connected: false))
                    : source.Where(s => s.ConnectedWhen is not null).Select(s => s.ConnectedWhen!).Distinct(StringComparer.Ordinal)
                        .Select(query => Probe(source.Key, query))
                        .CombineLatest()
                        .Take(1)
                        .Select(found => (Source: source.Key, Connected: found.Any(f => f))))
            .ToImmutableArray();
        return bySource.IsEmpty
            ? Observable.Return(ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal))
            : bySource.CombineLatest().Take(1)
                .Select(answers => answers.Where(a => a.Connected).Select(a => a.Source).ToImmutableHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether one connection query's first answer LISTS its source: it found a node, or it could not
    /// see everything (<see cref="QueryResultChange{T}.SilentProviders"/>,
    /// <see cref="QueryResultChange{T}.SnapshotIncomplete"/>). Only a COMPLETE empty answer says the
    /// source is not connected — an incomplete one is no answer, and must not hide a source the
    /// viewer did connect.
    /// </summary>
    /// <param name="change">The query's first answer.</param>
    internal static bool FoundConnection(QueryResultChange<MeshNode> change)
        => change.Items.Count > 0 || change.SnapshotIncomplete || change.SilentProviders is { Count: > 0 };

    /// <summary>The data id of each source's toggle/switch style — hidden while not connected.</summary>
    public const string SourceStylesId = "inboxSourceStyles";

    /// <summary>The style that hides a source's toggle and switch.</summary>
    internal const string HiddenStyle = "display: none;";

    /// <summary>The pointer of one source's style in <see cref="SourceStylesId"/>.</summary>
    internal static string SourceStylePointer(string key) => LayoutAreaReference.GetDataPointer(SourceStylesId) + "/" + key;

    /// <summary>The sources listed without a connection read — the platform's own, which need none (<see cref="InboxQueryLeg.AlwaysOn"/>).</summary>
    internal static ImmutableHashSet<string> Unprobed(ImmutableArray<InboxSection> sections)
        => [.. sections.GroupBy(s => s.Source, StringComparer.Ordinal).Where(g => g.Any(s => s.AlwaysOn)).Select(g => g.Key)];

    /// <summary>Each source's style: shown when <paramref name="connected"/> holds it, hidden otherwise. Pure.</summary>
    internal static ImmutableDictionary<string, string> SourceStyles(ImmutableArray<InboxSection> sections, ImmutableHashSet<string> connected)
        => sections.Select(s => s.Source).Distinct(StringComparer.Ordinal)
            .ToImmutableDictionary(InboxRows.SourceKey, source => connected.Contains(source) ? string.Empty : HiddenStyle);

    /// <summary>The data id of each kind row's style — shown while a source of the kind is connected, hidden otherwise.</summary>
    public const string KindStylesId = "inboxKindStyles";

    internal static string KindStylePointer(string key) => LayoutAreaReference.GetDataPointer(KindStylesId) + "/" + key;

    /// <summary>The data id of the per-state-kind visibility styles.</summary>
    public const string StateStylesId = "inboxStateStyles";

    /// <summary>The state kinds that own a filter in the side panel, in the order it lists them.</summary>
    internal static readonly ImmutableArray<string> StateKinds = [InboxStateKind.Activity, InboxStateKind.Mail, InboxStateKind.Chat];

    /// <summary>The pointer to a state kind's visibility style.</summary>
    internal static string StateStylePointer(string stateKind) => LayoutAreaReference.GetDataPointer(StateStylesId) + "/" + stateKind;

    /// <summary>The <see cref="InboxPreferences"/> field a state kind's filter is stored in (camelCase, as serialized).</summary>
    internal static string StateField(string stateKind)
        => stateKind switch { InboxStateKind.Activity => "activityStates", InboxStateKind.Mail => "mailStates", _ => "chatStates" };

    /// <summary>The localization key of a state kind's group title.</summary>
    internal static string StateTitleKey(string stateKind) => "inbox.section.state." + stateKind;

    /// <summary>
    /// Each state kind's style: shown when a connected section of that kind exists AND its kind (side-menu category)
    /// is enabled — ticked, or no kind ticked at all. A state filter for a kind that is not listed would only mislead. Pure.
    /// </summary>
    /// <param name="connected">The connected sections.</param>
    /// <param name="kinds">The kind ticks (<see cref="InboxPreferences.Kinds"/>).</param>
    internal static ImmutableDictionary<string, string> StateStyles(ImmutableArray<InboxSection> connected, ImmutableDictionary<string, bool> kinds)
    {
        var anyTicked = kinds.Any(kind => kind.Value);
        return StateKinds.ToImmutableDictionary(stateKind => stateKind,
            stateKind => connected.Any(section => InboxRows.StateKindOf(section) == stateKind
                    && (!anyTicked || (kinds.TryGetValue(InboxRows.SourceKey(section.Category), out var on) && on)))
                ? string.Empty : HiddenStyle,
            StringComparer.Ordinal);
    }

    /// <summary>Each kind's style, keyed by <see cref="InboxRows.SourceKey"/> of the category: shown when any source filed under it is in <paramref name="connected"/>. Pure.</summary>
    internal static ImmutableDictionary<string, string> KindStyles(ImmutableArray<InboxSection> sections, ImmutableHashSet<string> connected)
        => sections.GroupBy(s => s.Category, StringComparer.Ordinal)
            .ToImmutableDictionary(g => InboxRows.SourceKey(g.Key), g => g.Any(s => connected.Contains(s.Source)) ? string.Empty : HiddenStyle);

    /// <summary>The page over every source's <paramref name="sections"/>; the rows feed narrows it to the connected ones.</summary>
    private static UiControl Page(LayoutAreaHost host, string owner, string? zone, ImmutableArray<InboxSection> sections)
    {
        var sources = sections.GroupBy(section => section.Source, StringComparer.Ordinal)
            .Select(source => (Id: source.Key, Label: SourceLabel(host, source.First()),
                Shown: source.Any(s => s.ShownByDefault),
                Icon: source.Select(s => s.SourceIcon).FirstOrDefault(i => i.Length > 0) ?? string.Empty))
            .ToImmutableList();
        // Seeded ONCE per session: a re-render must not reset what the viewer typed, switched or ticked.
        if (host.TryMarkEditStateInitialized("inbox:" + InboxRows.FilterId))
        {
            // The view's own state — search, Only, tags. What persists (kinds, sources, states, the address,
            // ignorable) is the person's InboxPreferences node, which the checkboxes are bound to directly.
            host.UpdateData(InboxRows.FilterId, new InboxFilter());
            host.UpdateData(AccountOptionsId, AccountOptions(host.Localize("inbox.account.all"), []));
            EnsurePreferences(host, owner, sections);
            host.UpdateData(UnreadId, string.Empty);
            host.UpdateData(SlowId, string.Empty);
            host.UpdateData(CappedId, string.Empty);
            host.UpdateData(HiddenId, string.Empty);
            host.UpdateData(DecideStyleId, HiddenStyle);
            host.UpdateData(HiddenActionStyleId, HiddenStyle);
            host.UpdateData(IgnorableId, string.Empty);
            host.UpdateData(TagOptionsId, TagOptions(host.Localize("inbox.tag.all"), host.Localize("inbox.tag.important"), []));
            host.UpdateData(TagItemsId, ImmutableList<InboxTagItem>.Empty);
            host.UpdateData(KindLabelsId, KindLabels(CategoriesOf(host, sections), InboxRows.CategoryCounts([])));
            // Until the connection reads answer, only the sources that need none are listed.
            var unprobed = Unprobed(sections);
            host.UpdateData(CategoryOptionsId, CategoryOptions(host.Localize("inbox.category.all"),
                CategoriesOf(host, [.. sections.Where(s => unprobed.Contains(s.Source))]), InboxRows.CategoryCounts([])));
            host.UpdateData(SourceOptionsId, SourceOptions(host.Localize("inbox.only.all"),
                sources.Where(source => unprobed.Contains(source.Id))
                    .Select(source => (Key: InboxRows.SourceKey(source.Id), source.Label, source.Icon))));
            host.UpdateData(SourceStylesId, SourceStyles(sections, unprobed));
            host.UpdateData(KindStylesId, KindStyles(sections, unprobed));
            host.UpdateData(StateStylesId, StateStyles([.. sections.Where(s => unprobed.Contains(s.Source))], ImmutableDictionary<string, bool>.Empty));
        }
        host.SeedRowSelection(InboxRows.SelectionId);
        // One per page, created on the render turn; the buttons share it.
        if (host.GetVariable<InboxActs>(UndoId) is not { } acts)
        {
            acts = new InboxActs();
            host.SetVariable(UndoId, acts);
            host.RegisterForDisposal(acts);
        }
        // ONE feed, owner-computed: the grid binds it, and Delete re-reads it at the click. What it
        // lists is also handed to the toolbar, which shows an act only where it has something to act on.
        var shown = new BehaviorSubject<(ImmutableList<InboxRow> Rows, int? NeedsYou)>((ImmutableList<InboxRow>.Empty, null));
        host.RegisterForDisposal(shown);
        var rows = Rows(host, sections, owner, zone, acts.Rereads, (list, needsYou) => shown.OnNext((list, needsYou)), LegAnswerWait);
        // What Delete and Decide re-read at the click: every source's REAL answer, never a slow source's stand-in — an
        // action must not skip a ticked row because its source has not answered yet. Not shown: it reports nothing.
        var actionRows = Rows(host, sections, owner, zone, acts.Rereads);
        // The framework owns the subscription: it is disposed with the area and a feed failure goes to the area-error boundary.
        host.SubscribeToDataStream(DecideStyleId, shown.CombineLatest(host.RowSelection(InboxRows.SelectionId),
                (feed, selected) => ToolbarOf(feed.Rows, selected, owner, feed.NeedsYou))
            .DistinctUntilChanged()
            .Select(toolbar => toolbar.Decide ? string.Empty : HiddenStyle));

        var filterContext = LayoutAreaReference.GetDataPointer(InboxRows.FilterId);
        // The checkboxes that PERSIST are bound to the person's preferences NODE, never to a copy of it.
        var preferences = LayoutAreaReference.GetMeshNodeDataContext(InboxPreferencePaths.PathFor(owner));

        // ── The side panel: every filter, grouped under a small title. ─────────────────────────
        // Kind: one checkbox per kind (side-menu category), the symbol before it, its name and count
        // bound from the feed. The union of the ticked kinds is listed; none ticked lists all.
        var kind = Controls.Stack.WithVerticalGap(4);
        foreach (var category in CategoriesOf(host, sections))
        {
            var key = InboxRows.SourceKey(category.Key);
            kind = kind.WithView(
                (Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(8)
                    with { Style = new JsonPointerReference(KindStylePointer(key)) })
                    .WithView(Controls.Icon(category.Icon).WithWidth("20px"), "Icon" + key)
                    .WithView(Controls.CheckBox(new JsonPointerReference($"kinds/{key}")) with
                    {
                        DataContext = preferences,
                        Label = new JsonPointerReference(LayoutAreaReference.GetDataPointer(KindLabelsId) + "/" + key),
                    }, "Kind" + key), "Row" + key);
        }
        // State: ONE filter per kind, each with its own vocabulary, its own stored field and its own default —
        // activities Open / Done, mail Unread / Read, chats Unread / Read. A group is shown only while its kind is
        // enabled (connected and ticked); the union of a kind's ticked groups is listed, none ticked lists all of it.
        var stateFilters = Controls.Stack.WithVerticalGap(12);
        foreach (var stateKind in StateKinds)
        {
            var group = Controls.Stack.WithVerticalGap(4)
                .WithView(Controls.H6(host.Localize(StateTitleKey(stateKind))).WithStyle("margin: 0;"), "Title" + stateKind);
            foreach (var state in InboxStateKind.Vocabulary(stateKind))
                group = group.WithView(Controls.CheckBox(new JsonPointerReference($"{StateField(stateKind)}/{state}")) with
                {
                    DataContext = preferences, Label = host.Localize("inbox.filter." + state),
                }, "State" + stateKind + state);
            stateFilters = stateFilters.WithView(
                group with { Style = new JsonPointerReference(StateStylePointer(stateKind)) }, "States" + stateKind);
        }
        // Tags: one checkbox per tag the triage gave — Important first, then each by use. They are
        // known only once the rows are, so the list is a bound collection, one checkbox per item.
        // A row matching ANY ticked tag is listed; none ticked lists all.
        // The list is fed here (RowsOf writes TagItemsId), so the template's own feed stays silent —
        // otherwise its emission would overwrite a box the viewer just ticked.
        var tags = Observable.Never<IEnumerable<InboxTagItem>>()
            .BindMany(TagItemsId, tag => Controls.CheckBox(tag.Checked).WithLabel(tag.Label));
        // Source: Only narrows to the one source picked and wins over the switches; below it ONE row
        // per source — the SAME symbol the grid's first column shows, then a switch named for it. A
        // source stays hidden until the rows feed says the viewer has connected it.
        var sourceList = Controls.Stack.WithVerticalGap(4)
            .WithView(Controls.Select(new JsonPointerReference("only"),
                    new JsonPointerReference(LayoutAreaReference.GetDataPointer(SourceOptionsId)))
                with { DataContext = filterContext, AriaLabel = host.Localize("inbox.only") }, "Only");
        foreach (var source in sources)
        {
            var key = InboxRows.SourceKey(source.Id);
            sourceList = sourceList.WithView(
                (Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(8)
                    with { Style = new JsonPointerReference(SourceStylePointer(key)) })
                .WithView(Controls.Icon(source.Icon).WithWidth("20px"), "Icon" + key)
                .WithView(Controls.CheckBox(new JsonPointerReference($"sources/{key}")) with
                {
                    DataContext = preferences, Label = source.Label,
                }, "Show" + key), "Source" + key);
        }
        // Account: one option per configured ADDRESS (a mailbox), not per provider — bound to the preference.
        sourceList = sourceList.WithView(Controls.Select(new JsonPointerReference("account"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(AccountOptionsId)))
            with { DataContext = preferences, AriaLabel = host.Localize("inbox.account") }, "Account");
        var side = Controls.Stack.WithVerticalGap(16)
            .WithView(Controls.Text(new JsonPointerReference("search")) with
            {
                DataContext = filterContext, Label = host.Localize("inbox.search"),
                Placeholder = host.Localize("inbox.search.placeholder"),
            }, "Search")
            .WithView(Section(host.Localize("inbox.section.kind"), kind), "Kind")
            .WithView(Section(host.Localize("inbox.section.state"), stateFilters), "States")
            .WithView(Section(host.Localize("inbox.section.tags"), tags), "Tags")
            .WithView(Section(host.Localize("inbox.section.source"), sourceList), "Source")
            // Spam, promotions and noise stay out of the list until the viewer asks for them.
            .WithView(Section(host.Localize("inbox.section.options"), Controls.CheckBox(new JsonPointerReference("showIgnorable")) with
            {
                DataContext = preferences, Label = host.Localize("inbox.showIgnorable"),
            }), "Options");

        // ── The toolbar: the bulk acts only, icon-only, the name its tooltip and accessible name. ─
        // Ordinary buttons: the framework keeps each busy while its bulk act runs and shows the
        // outcome line that names every item that was not put away (Doc/GUI/ButtonPendingState).
        // Backspace / Delete press Delete, as in a mail client — never while typing in the search box.
        // Approve and Reject exist only while the list or the ticked rows hold a request the viewer
        // decides (ToolbarOf): "reject" means nothing in a mailbox.
        var decideStyle = new JsonPointerReference(LayoutAreaReference.GetDataPointer(DecideStyleId));
        var toolbar = Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(4)
            .WithView(IconButton(host.Localize("inbox.delete"), FluentIcons.Delete(), Appearance.Stealth)
                .WithShortcut(DeleteShortcut)
                .WithReactiveClickAction(ctx => Delete(ctx, acts, owner, actionRows)), "Delete")
            .WithView(IconButton(host.Localize("inbox.undo"), FluentIcons.ArrowUndo(), Appearance.Stealth)
                .WithReactiveClickAction(ctx => Undo(ctx, acts)), "Undo")
            // Decide every ticked approval request — the approval's own type decides and authorizes,
            // as the clicker (InboxApprovals). Other ticked rows are left alone.
            .WithView((IconButton(host.Localize("inbox.approve"), FluentIcons.Checkmark(), Appearance.Stealth) with { Style = decideStyle })
                .WithReactiveClickAction(ctx => Decide(ctx, acts, owner, actionRows, ApprovalDecisions.Approve)), "Approve")
            .WithView((IconButton(host.Localize("inbox.reject"), FluentIcons.Dismiss(), Appearance.Stealth) with { Style = decideStyle })
                .WithReactiveClickAction(ctx => Decide(ctx, acts, owner, actionRows, ApprovalDecisions.Reject)), "Reject");

        // What the source filter hides, as ONE quiet line with the way back.
        var hiddenLine = Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(8)
            .WithView(Controls.Label(new JsonPointerReference(LayoutAreaReference.GetDataPointer(HiddenId))), "Text")
            .WithView((Controls.Button(host.Localize("inbox.showAll")).WithAppearance(Appearance.Lightweight)
                with { Style = new JsonPointerReference(LayoutAreaReference.GetDataPointer(HiddenActionStyleId)) })
                .WithReactiveClickAction(ctx => ShowAll(ctx.Host, owner)), "ShowAll");

        var main = Controls.Stack.WithWidth("100%").WithVerticalGap(8)
            .WithView(Controls.H2(host.Localize("inbox.title")).WithStyle("margin: 0;"), "Title")
            .WithView(toolbar, "Toolbar")
            // What packages put on the inbox (UiContribution, context Inbox): Mail's Accounts button and its notes.
            .WithView(ContributedAreas(host, owner), "Contributed")
            // Names the sources that have not answered yet — the list shows the others meanwhile.
            .WithView(Controls.Label(new JsonPointerReference(LayoutAreaReference.GetDataPointer(SlowId))), "Slow")
            // Says which sources could not be read — an unreadable source must never look like an
            // empty one. Bound: it appears and clears with the reads, with no re-render.
            .WithView(Controls.Label(new JsonPointerReference(LayoutAreaReference.GetDataPointer(UnreadId))), "Unread")
            // Says when a source has more than the page lists — a capped list must never read as
            // "that is everything".
            .WithView(Controls.Label(new JsonPointerReference(LayoutAreaReference.GetDataPointer(CappedId))), "Capped")
            // Says how many items needing the viewer the switches hide — "nothing here" must never
            // be a switch's doing.
            .WithView(hiddenLine, "Hidden")
            // Says how many ignorable items are hidden — the noise is filtered, never lost.
            .WithView(Controls.Label(new JsonPointerReference(LayoutAreaReference.GetDataPointer(IgnorableId))), "Ignorable")
            .WithView(Grid(host, rows), "Grid");
        // The filters beside the list on a wide page, above it on a narrow one (the platform grid
        // stacks its items below the breakpoint). The PAGE scrolls, as the settings page does.
        return Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2))
            .WithView(side, area => area.WithId("Filters").AddSkin(new LayoutGridItemSkin().WithXs(12).WithMd(4).WithLg(3)))
            .WithView(main.WithStyle("min-width: 0;"), area => area.WithId("Main").AddSkin(new LayoutGridItemSkin().WithXs(12).WithMd(8).WithLg(9)));
    }

    /// <summary>A filter group: a small title over its controls.</summary>
    private static UiControl Section(string title, UiControl content)
        => Controls.Stack.WithVerticalGap(4)
            .WithView(Controls.H6(title).WithStyle("margin: 0;"), "Title")
            .WithView(content, "Content");

    /// <summary>An icon-only button: no visible text, the name carried as its tooltip and accessible name.</summary>
    private static ButtonControl IconButton(string name, MeshWeaver.Domain.Icon icon, object appearance)
        => Controls.Button(string.Empty).WithLabel(name).WithIconStart(icon).WithAppearance(appearance);

    /// <summary>Which of the toolbar's acts have something to act on.</summary>
    /// <param name="Decide">Approve and Reject: an approval request the viewer decides is selected, or listed with nothing selected.</param>
    internal readonly record struct ToolbarState(bool Decide);

    /// <summary>
    /// The toolbar for <paramref name="listed"/> rows with <paramref name="selected"/> ticked. The acts
    /// work on the ticked rows, so with a selection only those count; with none the whole list does.
    /// Approve / Reject apply to a request in the band that needs the viewer — a decided approval in
    /// the ledger and every mail, message or notification has nothing to decide. Pure.
    /// </summary>
    /// <param name="listed">The rows the grid lists.</param>
    /// <param name="selected">The ticked row keys.</param>
    /// <param name="owner">The inbox's owner.</param>
    /// <param name="needsYouRank">The rank of the band whose rows are obligations, or null when no leg feeds it.</param>
    internal static ToolbarState ToolbarOf(IReadOnlyCollection<InboxRow> listed, IReadOnlyCollection<string> selected,
        string owner, int? needsYouRank)
    {
        var candidates = selected.Count > 0 ? InboxRows.Targets(listed, selected, owner) : listed;
        return new ToolbarState(needsYouRank is { } rank && candidates.Any(row =>
            row.Rank == rank && InboxRows.IsDecidable(row)));
    }

    /// <summary>
    /// Gives the source switches back as they were: no Only, no picked address, every source on — the first in
    /// the view's own filter, the rest in the person's preferences node (one per-field-safe merge of the current
    /// content, never a copy).
    /// </summary>
    private static IObservable<Unit> ShowAll(LayoutAreaHost host, string owner)
    {
        var hub = host.Hub;
        var options = hub.JsonSerializerOptions;
        return host.GetDataStream<InboxFilter>(InboxRows.FilterId).Take(1)
            .SelectMany(filter =>
            {
                host.UpdateData(InboxRows.FilterId, (filter ?? new InboxFilter()) with { Only = string.Empty });
                return hub.GetMeshNodeStream(InboxPreferencePaths.PathFor(owner))
                    .Update(node => node.ContentAs<InboxPreferences>(options) is { } stored
                        ? node with { Content = stored with { Account = string.Empty, Sources = stored.Sources.ToImmutableDictionary(kv => kv.Key, _ => true, StringComparer.Ordinal) } }
                        : node)
                    .Select(_ => Unit.Default);
            });
    }

    /// <summary>The row with its state spelled for the viewer and its badge coloured; a row without a state keeps the badge hidden.</summary>
    internal static InboxRow WithStateText(InboxRow row, ImmutableDictionary<string, string> texts, string? stepFormat = null)
        => row.State.Length > 0 && texts.TryGetValue(row.State, out var text)
            ? row with
            {
                StateText = text + StepSuffix(row, stepFormat), StateColor = InboxState.Color(row.State), StateStyle = string.Empty,
                Status = text[InboxState.Mark(row.State).Length..] + StepSuffix(row, stepFormat),
            }
            : row;

    /// <summary>A state's label without its mark (<c>Stale</c>), from the texts resolved once on the render turn; the state itself when it has none.</summary>
    internal static string LabelOf(ImmutableDictionary<string, string> texts, string state)
        => texts.TryGetValue(state, out var text) ? text[InboxState.Mark(state).Length..] : state;

    /// <summary>
    /// What follows a running activity's state — <c> · step 3/7: Build</c>, <c> · step 3/7</c> or <c> · Build</c> —
    /// and nothing for a row that carries no step. <paramref name="stepFormat"/> holds <c>{0}</c> for the number.
    /// </summary>
    internal static string StepSuffix(InboxRow row, string? stepFormat)
    {
        var number = row.StepNumber.Length > 0 ? (stepFormat ?? "step {0}").Replace("{0}", row.StepNumber, StringComparison.Ordinal) : string.Empty;
        var parts = new[] { number, row.StepMessage }.Where(part => part.Length > 0).ToArray();
        return parts.Length == 0 ? string.Empty : " · " + string.Join(": ", parts);
    }

    /// <summary>The data id of the kind checkboxes' labels — each kind's name and count, keyed by <see cref="InboxRows.SourceKey"/>.</summary>
    public const string KindLabelsId = "inboxKindLabels";

    /// <summary>The data id of the tag checkboxes — one <see cref="InboxTagItem"/> per tag, most used first.</summary>
    public const string TagItemsId = "inboxTagItems";

    /// <summary>
    /// Each kind's checkbox text — its name and count, the count being the unread ones when it has any, else
    /// all of them — keyed by <see cref="InboxRows.SourceKey"/> of the category. Pure.
    /// </summary>
    internal static ImmutableDictionary<string, string> KindLabels(
        ImmutableList<(string Key, string Label, string Icon)> categories,
        ImmutableDictionary<string, (int Total, int Unread)> counts)
        => categories.ToImmutableDictionary(c => InboxRows.SourceKey(c.Key),
            c => c.Label + (counts.TryGetValue(c.Key, out var n) && (n.Unread > 0 ? n.Unread : n.Total) is > 0 and var shown ? $"  {shown}" : string.Empty),
            StringComparer.Ordinal);

    /// <summary>
    /// The tag checkboxes: Important first, then each tag by use with its count; the ones in <paramref name="ticked"/> are
    /// ticked. 🚨 While the rows are INCOMPLETE (<see cref="Incomplete"/>: a source stands in or could not be read), a ticked box whose tag
    /// is absent from them is kept from <paramref name="held"/>, ticked: its tag may sit only in the source that has not
    /// answered, and dropping it would untick it — silently widening the filter — by the time that source answers. Pure.
    /// </summary>
    /// <param name="counts">Each tag in the rows, with its count.</param>
    /// <param name="importantLabel">The Important box's label.</param>
    /// <param name="ticked">The keys the viewer ticked.</param>
    /// <param name="held">The boxes shown before, whose ticked ones are kept while the rows are incomplete; null once every source has answered.</param>
    internal static ImmutableList<InboxTagItem> TagItems(IEnumerable<(string Key, string Tag, int Count)> counts,
        string importantLabel, ImmutableHashSet<string> ticked, IReadOnlyList<InboxTagItem>? held = null)
    {
        var items = counts.Select(c => new InboxTagItem
        {
            Key = c.Key,
            Label = (string.Equals(c.Key, InboxRows.ImportantKey, StringComparison.Ordinal) ? $"⭐ {importantLabel}" : "#" + c.Tag) + $"  {c.Count}",
            Checked = ticked.Contains(c.Key),
        }).ToImmutableList();
        var present = items.Select(item => item.Key).ToImmutableHashSet(StringComparer.Ordinal);
        return items.AddRange((held ?? []).Where(item => item.Checked && ticked.Contains(item.Key) && !present.Contains(item.Key)));
    }

    /// <summary>The data id of the Approve / Reject style: empty while they apply, hidden otherwise.</summary>
    public const string DecideStyleId = "inboxDecideStyle";

    /// <summary>The data id of the style of the "show all" action: shown only while the filter hides something.</summary>
    public const string HiddenActionStyleId = "inboxHiddenActionStyle";

    /// <summary>The page's categories — key, label in the viewer's language, symbol — in band order.</summary>
    internal static ImmutableList<(string Key, string Label, string Icon)> CategoriesOf(LayoutAreaHost host, ImmutableArray<InboxSection> sections)
        => [.. sections.GroupBy(s => s.Category, StringComparer.Ordinal)
            .Select(c => (Key: c.Key, Label: Label(key => host.Localize(key), c.First().CategoryLabelKey, c.Key),
                Icon: InboxSourceIcons.CategoryIcon(c.Key, c.Select(s => s.SourceIcon))))];

    /// <summary>
    /// The side menu's options: All (empty key), then each category with its symbol and count — the
    /// unread count when it has unread rows, else all its rows. Pure.
    /// </summary>
    internal static ImmutableList<Option> CategoryOptions(string allLabel,
        ImmutableList<(string Key, string Label, string Icon)> categories,
        ImmutableDictionary<string, (int Total, int Unread)> counts)
    {
        static string Text(string icon, string label, int count)
            => (InboxSourceIcons.Glyph(icon) is { Length: > 0 } glyph ? $"{glyph} {label}" : label) + (count > 0 ? $"  {count}" : string.Empty);
        static int Shown((int Total, int Unread) c) => c.Unread > 0 ? c.Unread : c.Total;
        var all = counts.Values.Aggregate((Total: 0, Unread: 0), (sum, c) => (sum.Total + c.Total, sum.Unread + c.Unread));
        return
        [
            new Option<string>(string.Empty, Text("📥", allLabel, Shown(all))),
            .. categories.Select(c => (Option)new Option<string>(c.Key,
                Text(c.Icon, c.Label, counts.TryGetValue(c.Key, out var n) ? Shown(n) : 0))),
        ];
    }

    /// <summary>
    /// The tag menu's options: all tags (empty key), then <see cref="InboxRows.TagCounts"/> — the items
    /// judged important under their own label, every tag as <c>#tag</c> — each with its count. Pure.
    /// </summary>
    internal static ImmutableList<Option> TagOptions(string allLabel, string importantLabel,
        IEnumerable<(string Key, string Tag, int Count)> counts)
        =>
        [
            new Option<string>(string.Empty, allLabel),
            .. counts.Select(c => (Option)new Option<string>(c.Key,
                (string.Equals(c.Key, InboxRows.ImportantKey, StringComparison.Ordinal) ? $"⭐ {importantLabel}" : "#" + c.Tag)
                + $"  {c.Count}")),
        ];

    /// <summary>
    /// Creates the person's preferences node when it is absent, seeded with what a person who never chose
    /// gets (<see cref="InboxPreferencesNodeType.Defaults"/>: the open approvals and the important
    /// notifications). Runs once per page, off the render's critical path: the template is returned at
    /// once and the checkboxes follow the node when it appears.
    /// </summary>
    private static void EnsurePreferences(LayoutAreaHost host, string owner, ImmutableArray<InboxSection> sections)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxLayoutArea));
        host.RegisterForDisposal(InboxPreferencesNodeType.EnsureExists(host.Hub, owner, InboxPreferencesNodeType.Defaults(sections), logger)
            .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "Inbox: the preferences of {Owner} could not be created", owner)));
    }

    /// <summary>
    /// The person's preferences, live: the node's own stream once the node exists, nothing known (null)
    /// while it does not. Existence is asked of a query (empty-on-absent) and the content of the node's
    /// stream — never a point read of a maybe-absent path (Doc/Architecture/CqrsAndContentAccess). The read is
    /// built under the VIEWER's identity, re-established around each existence answer (it lands on another turn).
    /// 🚨 Nothing is emitted before the first answer: the rows of a person whose node exists must never flash the
    /// defaults while it is read; an answer that does not come within <see cref="PreferencesWait"/> reads as "none
    /// stored" (<see cref="FirstAnswerWithin{T}"/>), and a later real answer still replaces it.
    /// </summary>
    internal static IObservable<InboxPreferences?> PreferencesOf(LayoutAreaHost host, string owner, AccessContext? viewer)
    {
        var hub = host.Hub;
        var path = InboxPreferencePaths.PathFor(owner);
        var options = hub.JsonSerializerOptions;
        // The node's whole lifecycle is followed (ObserveNode): deleted — absent, so the defaults apply — and recreated, or
        // deleted and recreated in one Updated frame, the grid keeps feeding and picks the new content up.
        var answers = InboxApprovalTargets.ObserveNode<InboxPreferences?>(hub, viewer, path,
            InboxApprovalTargets.Exists(hub.ServiceProvider.GetRequiredService<IMeshService>(), path, owner),
            node => InboxPreferencesNodeType.Parse(node, options), absent: null);
        return FirstAnswerWithin(answers, PreferencesWait, (InboxPreferences?)null);
    }

    /// <summary>
    /// <paramref name="source"/>, with <paramref name="fallback"/> emitted ONCE when it has said nothing within
    /// <paramref name="wait"/> — or when it COMPLETES without having said anything. The arbitration is serialized: the fallback is emitted only while no real answer
    /// has been, a real answer after it is forwarded as ever, and the observer never sees two notifications
    /// at once.
    /// </summary>
    /// <param name="source">The answers.</param>
    /// <param name="wait">How long the source may stay silent.</param>
    /// <param name="fallback">What a silent source reads as.</param>
    /// <param name="scheduler">Timer seam for tests.</param>
    internal static IObservable<T> FirstAnswerWithin<T>(IObservable<T> source, TimeSpan wait, T fallback, IScheduler? scheduler = null)
        => Observable.Create<T>(observer =>
        {
            var gate = new object();
            var answered = false;
            var timer = Observable.Timer(wait, scheduler ?? DefaultScheduler.Instance).Subscribe(_ =>
                {
                    lock (gate)
                    {
                        if (answered)
                            return;
                        answered = true;
                        observer.OnNext(fallback);
                    }
                },
                error => { lock (gate) { answered = true; observer.OnError(error); } });
            var subscription = source.Subscribe(
                value => { lock (gate) { answered = true; observer.OnNext(value); } },
                error => { lock (gate) { answered = true; observer.OnError(error); } },
                () =>
                {
                    lock (gate)
                    {
                        // A source that ends without an answer reads as the fallback, once: the consumer never waits on a silence.
                        if (!answered)
                            observer.OnNext(fallback);
                        answered = true;
                        observer.OnCompleted();
                    }
                });
            return new System.Reactive.Disposables.CompositeDisposable(timer, subscription);
        });

    /// <summary>How long the page waits for the person's preferences before it lists with the defaults.</summary>
    internal static readonly TimeSpan PreferencesWait = TimeSpan.FromSeconds(5);

    /// <summary><paramref name="view"/> with the persisted choices <paramref name="stored"/> laid over it. Pure.</summary>
    /// <param name="view">The view's own filter (search, Only, tags).</param>
    /// <param name="stored">The person's preferences, or the defaults while none are stored.</param>
    internal static InboxFilter WithPreferences(InboxFilter view, InboxPreferences stored)
    {
        // Resolved per kind: a node written before the filters were per kind still lists its mail.
        var effective = InboxPreferencesNodeType.Effective(stored);
        return view with
        {
            Kinds = stored.Kinds, Sources = stored.Sources,
            ActivityStates = effective.ActivityStates, MailStates = effective.MailStates, ChatStates = effective.ChatStates,
            Account = stored.Account, ShowIgnorable = stored.ShowIgnorable,
        };
    }

    /// <summary>
    /// Writes the entries <paramref name="stored"/> lacks (a kind or a source a package brought since the
    /// node was created, a per-kind state filter a legacy node predates) into the node, ONCE per set of keys: the
    /// write is a merge of the missing keys into the node's CURRENT content, so it never overwrites a choice, and the
    /// next pass finds nothing missing.
    /// <para>🚨 A pass that still sees the keys missing while the write is IN FLIGHT is the expected stale read, not
    /// a loop (#7260): the rows, the filter and the tag ticks re-run this pass on their own, and any of them can
    /// arrive before the committed write has reached the page's view of the node. Such a pass does nothing. The
    /// guard (<see cref="InboxSeedGuard"/>) records a set of keys as done only once the owner CONFIRMED the write, and
    /// it is the CONFIRMED node that is checked for the keys — that, and only that, is a loop, logged as an error and
    /// not repeated (rule 2: never feed a reconcile its own writes). A write that FAILED is released, so a later pass
    /// writes it again instead of the keys staying unwritten for the rest of the session.</para>
    /// </summary>
    private static void SeedMissing(IMessageHub hub, AccessService? access, AccessContext? viewer, ILogger? logger, string path,
        InboxPreferences stored, InboxPreferences expected, InboxSeedGuard seeded)
    {
        if (InboxPreferencesNodeType.Completed(stored, expected) is not { } completed)
            return;
        var fingerprint = string.Join(',', MissingKeys(stored, completed));
        if (!seeded.TryBegin(fingerprint))
            return;
        var options = hub.JsonSerializerOptions;
        // The write is the person's own, under their identity — this runs on whatever turn the rows landed on.
        using (access?.SwitchAccessContext(viewer))
        {
            var write = hub.GetMeshNodeStream(path)
                .Update(node => node.ContentAs<InboxPreferences>(options) is { } current
                                && InboxPreferencesNodeType.Completed(current, expected) is { } merged
                    ? node with { Content = merged }
                    : node);
            // The read of what the owner holds, built under the same identity as the write.
            var held = hub.GetMeshNodeStream(path);
            CommittedMissing(write, held, expected, options, CommittedReadWait)
                .Subscribe(
                    still =>
                    {
                        seeded.Confirm(fingerprint);
                        if (still.Length > 0)
                            logger?.LogError("Inbox: the preference keys {Keys} were written and committed, and {Path} still lacks them; not writing again",
                                string.Join(", ", still), path);
                    },
                    ex =>
                    {
                        seeded.Release(fingerprint);
                        logger?.LogWarning(ex, "Inbox: could not add the missing keys {Keys} to {Path}; a later pass writes them again", fingerprint, path);
                    });
        }
    }

    /// <summary>How long the owner's view of the node may take to show a committed seed write before what it holds is the verdict.</summary>
    internal static readonly TimeSpan CommittedReadWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The keys the OWNER's node still lacks once <paramref name="write"/> committed — empty when it holds them all.
    /// 🚨 The write's own terminal is NOT the verdict: it carries the owner's committed VERSION but the WRITER's
    /// content (<c>UpdateRemote.EmitTerminal</c>), which already passed through
    /// <see cref="InboxPreferencesNodeType.Completed"/> and so always holds the keys. The verdict is read from
    /// <paramref name="ownerView"/> (the node's own stream), at or beyond the committed version: the first state there
    /// that holds every key answers empty; if none does within <paramref name="within"/>, the latest such state names
    /// what it lacks. A failed write, or an owner view that says nothing within the bound, faults.
    /// </summary>
    /// <param name="write">The cold seed write; subscribed here.</param>
    /// <param name="ownerView">The node's own stream — what the owner holds.</param>
    /// <param name="expected">What the page seeds (<see cref="InboxPreferencesNodeType.SeedForExisting"/>).</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <param name="within">How long the owner's view may take to show the commit.</param>
    /// <param name="scheduler">Timer seam for tests.</param>
    internal static IObservable<string[]> CommittedMissing(IObservable<MeshNode> write, IObservable<MeshNode> ownerView,
        InboxPreferences expected, JsonSerializerOptions options, TimeSpan within, IScheduler? scheduler = null)
        => write.LastAsync().SelectMany(committed => Observable.Defer(() =>
        {
            string[]? latest = null;
            return ownerView
                .Where(node => node.Version >= committed.Version)
                .Select(node => StillMissing(node, expected, options))
                .Do(still => latest = still)
                .Where(still => still.Length == 0)
                .Take(1)
                .Timeout(within, scheduler ?? DefaultScheduler.Instance)
                .Catch((TimeoutException ex) => Latest(ex))
                // An owner view that ENDS without holding every key: what it last showed is the verdict.
                .Concat(Observable.Defer(() => Latest(null)))
                .Take(1);

            IObservable<string[]> Latest(Exception? cause) => latest is { } seen
                ? Observable.Return(seen)
                : Observable.Throw<string[]>(new TimeoutException(
                    $"The owner's view of the node showed nothing at or beyond the committed version {committed.Version} within {within}.", cause));
        }));

    /// <summary>The keys <paramref name="node"/> lacks against <paramref name="expected"/>; every key when its content is unreadable. Pure.</summary>
    private static string[] StillMissing(MeshNode node, InboxPreferences expected, JsonSerializerOptions options)
    {
        var held = node.ContentAs<InboxPreferences>(options) ?? new InboxPreferences();
        return InboxPreferencesNodeType.Completed(held, expected) is { } still ? MissingKeys(held, still) : [];
    }

    /// <summary>The keys <paramref name="completed"/> adds to <paramref name="stored"/>, as <c>k:</c>/<c>s:</c>/<c>st:</c> entries in ordinal order. Pure.</summary>
    /// <param name="stored">What the node holds.</param>
    /// <param name="completed">The same with what it lacks added (<see cref="InboxPreferencesNodeType.Completed"/>).</param>
    internal static string[] MissingKeys(InboxPreferences stored, InboxPreferences completed)
        => [.. completed.Kinds.Keys.Except(stored.Kinds.Keys).Select(k => "k:" + k)
            .Concat(completed.Sources.Keys.Except(stored.Sources.Keys).Select(k => "s:" + k))
            .Concat(InboxPreferencesNodeType.MissingStates(stored).Select(k => "st:" + k))
            .Order(StringComparer.Ordinal)];

    /// <summary>The addresses the rows reached — each once, by <see cref="InboxRows.AccountKey"/>, in alphabetical order. Pure.</summary>
    /// <param name="rows">The merged rows.</param>
    internal static ImmutableList<(string Key, string Address)> AccountsOf(IEnumerable<InboxRow> rows)
        => [.. rows.Where(r => r.Account.Length > 0)
            .GroupBy(r => InboxRows.AccountKey(r.Account), StringComparer.Ordinal)
            .Select(g => (Key: g.Key, Address: g.First().Account))
            .OrderBy(a => a.Address, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The Account select's options: every address (empty key), then each configured address. Pure.</summary>
    internal static ImmutableList<Option> AccountOptions(string allLabel, IEnumerable<(string Key, string Address)> accounts)
        =>
        [
            new Option<string>(string.Empty, allLabel),
            .. accounts.Select(a => (Option)new Option<string>(a.Key, a.Address)),
        ];

    /// <summary>The Only select's options: every source (empty key), then each source with its symbol. Pure.</summary>
    internal static ImmutableList<Option> SourceOptions(string allLabel,
        IEnumerable<(string Key, string Label, string Icon)> sources)
        =>
        [
            new Option<string>(string.Empty, allLabel),
            .. sources.Select(s => (Option)new Option<string>(s.Key, InboxSourceIcons.Glyph(s.Icon) is { Length: > 0 } glyph ? $"{glyph} {s.Label}" : s.Label)),
        ];

    /// <summary>The keys that press Delete — as a mail client: Backspace or Delete, with the rows ticked.</summary>
    internal const string DeleteShortcut = "Backspace|Delete";

    /// <summary>
    /// The grid's rows: every leg's result for the term the viewer typed, merged, then narrowed to
    /// the sources switched on and put in the chosen order. A leg that cannot be read contributes no
    /// rows and is LOGGED — one provider's failure must not blank the list the others feed.
    /// </summary>
    /// <param name="host">The inbox area's host.</param>
    /// <param name="sections">The resolved legs.</param>
    /// <param name="owner">The inbox's owner.</param>
    /// <param name="zone">The viewer's time zone.</param>
    /// <param name="rereads">Ticks that read every leg again.</param>
    /// <param name="onShown">Told what the list shows, on every change.</param>
    /// <param name="legBound">The bound on each leg's FIRST answer (<see cref="FanIn"/>) — the DISPLAY's; null for an
    /// action's preflight, which must act on every source's real answer and never on a stand-in.</param>
    internal static IObservable<ImmutableList<InboxRow>> Rows(
        LayoutAreaHost host, ImmutableArray<InboxSection> sections, string owner, string? zone,
        IObservable<long>? rereads = null, Action<ImmutableList<InboxRow>, int?>? onShown = null, TimeSpan? legBound = null)
    {
        // Resolved ONCE, on the render turn — the connection reads answer on another thread.
        var allSources = host.Localize("inbox.only.all");
        // The viewer's identity, captured HERE with the texts: RowsOf is built inside the connection result's callback,
        // which a timeout reaches through a timer, where no identity is ambient. Its reads and its seed write run as the viewer.
        var viewerAccess = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewer = viewerAccess?.Context ?? viewerAccess?.CircuitContext;
        var sourceOptions = sections.GroupBy(s => s.Source, StringComparer.Ordinal)
            .Select(source => (Id: source.Key, Key: InboxRows.SourceKey(source.Key), Label: SourceLabel(host, source.First()),
                Icon: source.Select(s => s.SourceIcon).FirstOrDefault(i => i.Length > 0) ?? string.Empty))
            .ToImmutableList();
        // Only the CONNECTED sources are read: their legs feed the rows, their toggles are shown and
        // they are offered in the Only select. The rest stay hidden and are never asked.
        return Connected(host.Hub, sections, owner)
            .Do(connected =>
            {
                host.UpdateData(SourceStylesId, SourceStyles(sections, connected));
                host.UpdateData(KindStylesId, KindStyles(sections, connected));
                host.UpdateData(SourceOptionsId, SourceOptions(allSources,
                    sourceOptions.Where(s => connected.Contains(s.Id)).Select(s => (s.Key, s.Label, s.Icon))));
            })
            .Select(connected => RowsOf(host, [.. sections.Where(s => connected.Contains(s.Source))], owner, zone, viewer, rereads, onShown, legBound))
            .Switch();
    }

    /// <summary>The rows of <paramref name="sections"/> — the connected sources' legs.</summary>
    private static IObservable<ImmutableList<InboxRow>> RowsOf(
        LayoutAreaHost host, ImmutableArray<InboxSection> sections, string owner, string? zone, AccessContext? viewerContext,
        IObservable<long>? rereads, Action<ImmutableList<InboxRow>, int?>? onShown = null, TimeSpan? legBound = null)
    {
        var hub = host.Hub;
        var handlers = hub.ServiceProvider.GetServices<IInboxItemHandler>().ToImmutableArray();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxLayoutArea));
        var seeded = new InboxSeedGuard();
        // Resolved ONCE, on the render turn: every text a row may carry.
        var running = host.Localize("inbox.blocked.running");
        var noHandler = host.Localize("inbox.blocked.noHandler");
        var locale = host.ViewerLocale();
        var culture = ViewerCulture(locale);
        // Per row only the person differs: the sentences are resolved ONCE here, with "{0}" kept for them.
        var ledgerTexts = InboxApprovalLedger.Texts.Of(
            host.Localize("inbox.approvals.requestedFrom", "{0}"),
            host.Localize("inbox.approvals.approvedBy", "{0}"),
            host.Localize("inbox.approvals.rejectedBy", "{0}"));
        var stateTexts = InboxState.All.ToImmutableDictionary(state => state,
            state => InboxState.Mark(state) + host.Localize(InboxState.LabelKey(state)), StringComparer.Ordinal);
        var stepFormat = host.Localize("inbox.state.step", "{0}");
        var allLabel = host.Localize("inbox.category.all");
        var allTags = host.Localize("inbox.tag.all");
        var importantLabel = host.Localize("inbox.tag.important");
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
        // What persists: the person's own node, read live — absent until the first open creates it, in which case the defaults apply.
        var expected = InboxPreferencesNodeType.Defaults(sections);
        var seedFor = InboxPreferencesNodeType.SeedForExisting(sections);
        var preferencesPath = InboxPreferencePaths.PathFor(owner);
        var viewerAccess = hub.ServiceProvider.GetService<AccessService>();
        var preferences = PreferencesOf(host, owner, viewerContext);
        var tagItems = host.GetDataStream<ImmutableList<InboxTagItem>>(TagItemsId).Select(items => items ?? [])
            .DistinctUntilChanged(items => string.Join('\n', items.Select(item => $"{item.Key}|{item.Label}|{item.Checked}")), StringComparer.Ordinal);

        // What each leg last answered: its rows, and whether that answer was a failure. A leg that
        // cannot be read contributes no rows, is LOGGED and is NAMED above the grid — one provider's
        // failure must not blank the others, and must not pass for "nothing here" either. It is
        // read again when the viewer changes the search or reopens the page, never on a timer: a
        // provider that stays broken would otherwise be re-queried, and re-logged, for as long as
        // any inbox is open.
        IObservable<(ImmutableList<InboxRow> Rows, string? Unread, int Fetched)> Leg(InboxSection section, string term) =>
            Nodes(hub, section, term, owner)
                .Select(nodes => nodes
                    .Select(node => WithStateText(InboxApprovalLedger.Decorate(
                        InboxRows.Of(node, section, zone,
                            BlockedReason(section.Band, InboxItemHandlers.For(handlers, node.NodeType), running, noHandler),
                            bands[section.Band].Title, bands[section.Band].rank, labels[section.Source], culture,
                            hub.JsonSerializerOptions, locale),
                        node, section.Ledger, ledgerTexts, hub.JsonSerializerOptions), stateTexts, stepFormat))
                    .ToImmutableList())
                // An approval request stays only while the approval it names awaits a decision — observed LIVE, and
                // always the latest batch: a newer notification snapshot is never replaced by an older read. What the
                // leg FETCHED is carried beside the rows, so the "capped" notice still reads the window it asked of.
                .Select(legRows => section.Ledger is null && string.Equals(section.Source, InboxQueries.ApprovalsSource, StringComparison.Ordinal)
                    ? InboxApprovalTargets.WithOutcomes(hub, viewerContext, legRows, owner, hub.JsonSerializerOptions, stateTexts)
                        .Select(kept => (kept, (string?)null, legRows.Count))
                    : Observable.Return((legRows, (string?)null, legRows.Count)))
                .Switch()
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "Inbox leg {Source} ({Band}) could not be read", section.Source, section.Band);
                    return Observable.Return((ImmutableList<InboxRow>.Empty, (string?)labels[section.Source], 0));
                });

        // The merged rows, and whether they are incomplete (a source stands in or could not be read).
        IObservable<(ImmutableList<InboxRow> Rows, bool Incomplete)> Merged(string term) => FanIn(
                sections.Select(section => (labels[section.Source], Leg(section, term))), legBound)
            .Do(legs =>
            {
                host.UpdateData(UnreadId, UnreadNotice(host, legs.Select(leg => leg.Unread)));
                if (SlowSources(legs.Select(leg => leg.Slow), display: legBound is not null) is { } slow)
                    host.UpdateData(SlowId, SlowNotice(host, slow));
                // Each leg against ITS OWN bound — a provider may ask for fewer than the page's.
                host.UpdateData(CappedId, Capped(sections.Zip(legs, (section, leg) => (section.Query, leg.Fetched)))
                    ? host.Localize("inbox.capped")
                    : string.Empty);
            })
            .Select(legs => (Rows: InboxRows.Merge(legs.Select(leg => leg.Rows)), Incomplete: Incomplete(legs.Select(leg => (leg.Unread, leg.Slow)))))
            .Do(merged => host.UpdateData(KindLabelsId, KindLabels(categories, InboxRows.CategoryCounts(merged.Rows))));

        // The date bounds (before:/after:) are applied to the rows by InboxRows.Apply; only the
        // remaining text goes to the sources.
        return filter.Select(f => InboxRows.SplitDates(f.Search).Text.Trim())
            .DistinctUntilChanged(StringComparer.Ordinal)
            // The first read and a cleared box answer at once; a term waits for the typing to pause,
            // and Switch drops the read a newer keystroke superseded.
            .Select(term => term.Length == 0 ? Observable.Return(term) : Observable.Return(term).Delay(SearchQuiet))
            .Switch()
            // Every leg is read again after a Delete or an Undo: a LIVE source (a mailbox read from
            // its provider) has no change feed, so the put-away row would otherwise stay listed.
            .CombineLatest(rereads ?? Observable.Return(0L), (term, _) => term)
            .Select(Merged)
            .Switch()
            .CombineLatest(filter, tagItems, preferences, (merged, viewFilter, items, stored) =>
            {
                var rows = merged.Rows;
                // The view's filter (search, Only) with the person's persisted choices.
                var asked = WithPreferences(viewFilter, stored ?? expected);
                if (stored is not null)
                    SeedMissing(hub, viewerAccess, viewerContext, logger, preferencesPath, stored, seedFor, seeded);
                host.UpdateData(AccountOptionsId, AccountOptions(host.Localize("inbox.account.all"), AccountsOf(rows)));
                host.UpdateData(StateStylesId, StateStyles(sections, asked.Kinds));
                // The tag checkboxes follow the rows; a box the viewer ticked stays ticked while its tag exists.
                var ticked = items.Where(item => item.Checked).Select(item => item.Key).ToImmutableHashSet(StringComparer.Ordinal);
                // While a source still stands in, a ticked tag absent from the rows may be that source's: it stays ticked.
                var wanted = TagItems(InboxRows.TagCounts(rows), importantLabel, ticked, merged.Incomplete ? items : null);
                if (!wanted.SequenceEqual(items))
                    host.UpdateData(TagItemsId, wanted);
                asked = asked with { TagKeys = [.. wanted.Where(item => item.Checked).Select(item => item.Key)] };
                var shown = InboxRows.Apply(rows, asked, asked.Category);
                var hidden = InboxRows.HiddenObligations(rows, asked, needsYou);
                host.UpdateData(HiddenId, hidden == 0 ? string.Empty : host.Localize("inbox.hidden", hidden));
                host.UpdateData(HiddenActionStyleId, hidden == 0 ? HiddenStyle : string.Empty);
                onShown?.Invoke(shown, needsYou);
                var ignorable = InboxRows.HiddenIgnorable(rows, asked);
                host.UpdateData(IgnorableId, ignorable == 0 ? string.Empty : host.Localize("inbox.ignorableHidden", ignorable));
                return shown;
            });
    }

    /// <summary>
    /// Whether any leg answered as many nodes as it was asked for — each against ITS OWN bound — so the list may
    /// hide older ones. Counted on what the leg FETCHED, never on the rows kept after the page filtered them: a
    /// window of decided requests that all drop out still leaves older open ones unread. Pure.
    /// </summary>
    /// <param name="legs">Each leg's query and how many nodes it fetched.</param>
    internal static bool Capped(IEnumerable<(string Query, int Fetched)> legs)
        => legs.Any(leg => leg.Fetched >= InboxQueries.EffectiveLimit(leg.Query));

    /// <summary>
    /// Every leg's answer, combined — with ONE bound per leg on its FIRST answer: a leg that has not answered within
    /// <paramref name="bound"/> stands in as an empty answer naming its source (<c>Slow</c>), so the others are listed
    /// at once, and its own answer replaces the stand-in whenever it arrives. Without the bound one source that never
    /// answers holds every other one, and the list spins for all of them (2026-10-30). Emits once every leg has an
    /// answer or a stand-in, then on every change.
    /// </summary>
    /// <param name="legs">Each leg with the label its notice names.</param>
    /// <param name="bound">How long a leg's first answer may take; null waits for every leg's real answer (an action's preflight).</param>
    /// <param name="scheduler">Timer seam for tests.</param>
    internal static IObservable<ImmutableList<(ImmutableList<InboxRow> Rows, string? Unread, int Fetched, string? Slow)>> FanIn(
        IEnumerable<(string Label, IObservable<(ImmutableList<InboxRow> Rows, string? Unread, int Fetched)> Leg)> legs,
        TimeSpan? bound, IScheduler? scheduler = null)
        // 🚨 The arbitration between a leg's first answer and its deadline is SERIALIZED (FirstAnswerWithin): the stand-in
        // is emitted only while no real answer has been, so it can never land after — and replace — the real rows.
        => legs.Select(leg =>
            {
                var answers = leg.Leg.Select(answer => (answer.Rows, answer.Unread, answer.Fetched, Slow: (string?)null));
                return bound is { } wait
                    ? FirstAnswerWithin(answers, wait, (ImmutableList<InboxRow>.Empty, (string?)null, 0, (string?)leg.Label), scheduler)
                    : answers;
            })
            .CombineLatest()
            .Select(answers => answers.ToImmutableList());

    /// <summary>
    /// The sources the slow notice names, once each — or null when this feed must not publish the notice at all. Only
    /// the bounded DISPLAY feed knows which sources stand in: an action's preflight has no bound, so it never carries a
    /// stand-in and would clear the notice while the grid still omits the silent source. Pure.
    /// </summary>
    /// <param name="slow">Each leg's stand-in label, or null when it answered.</param>
    /// <param name="display">Whether this is the bounded display feed.</param>
    internal static ImmutableList<string>? SlowSources(IEnumerable<string?> slow, bool display)
        => display ? slow.OfType<string>().Distinct(StringComparer.Ordinal).ToImmutableList() : null;

    /// <summary>
    /// Whether the merged rows are INCOMPLETE: a leg still stands in (<c>Slow</c>) or could not be read (<c>Unread</c>) —
    /// including a silent leg the query's stall terminal ended. Either way a source's rows are missing, so a ticked tag
    /// absent from the rows may be that source's and must stay ticked. Pure.
    /// </summary>
    /// <param name="legs">Each leg's unread label and stand-in label.</param>
    internal static bool Incomplete(IEnumerable<(string? Unread, string? Slow)> legs)
        => legs.Any(leg => leg.Slow is not null || leg.Unread is not null);

    /// <summary>The notice for the sources that have not answered: empty when none, else the sentence naming each once.</summary>
    internal static string SlowNotice(LayoutAreaHost host, IEnumerable<string?> slow)
    {
        var names = slow.OfType<string>().Distinct(StringComparer.Ordinal).ToImmutableList();
        return names.IsEmpty ? string.Empty : host.Localize("inbox.slow", string.Join(", ", names));
    }

    /// <summary>
    /// The areas packages embed on the inbox (<see cref="UiContribution.InboxContext"/>), live from the catalog: one
    /// row of embedded areas of the user node (or of the contribution's own partition), in their order. Nothing while
    /// none is declared.
    /// </summary>
    private static IObservable<UiControl?> ContributedAreas(LayoutAreaHost host, string owner)
        // A fault reaches the area's error boundary, which says so — never an empty lane that hides the entries.
        => InboxContributions(host, owner)
            .Select(areas =>
            {
                if (areas.Count == 0)
                    return (UiControl?)null;
                var row = Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(12);
                foreach (var entry in areas)
                    row = row.WithView(Controls.LayoutArea(entry.Address is null ? host.Hub.Address : new Address(entry.Address), entry.Area)
                        .WithShowProgress(false), "Contribution-" + entry.Id);
                return (UiControl?)row;
            });

    /// <summary>
    /// The inbox contributions this viewer sees, live: the catalog projected against the user node's SHAPE (read live,
    /// so a shape gate is decided on the node, never on its absence) and filtered by the viewer's LIVE effective
    /// permissions on it — the profile and settings lanes' rule. Data only.
    /// </summary>
    private static IObservable<IReadOnlyList<(string Id, string? Address, string Area, int Order, Permission RequiredPermission)>> InboxContributions(
        LayoutAreaHost host, string owner)
    {
        if (host.Hub.ServiceProvider.GetService<UiContributionCatalog>() is not { } catalog)
            return Observable.Return<IReadOnlyList<(string, string?, string, int, Permission)>>([]);
        var userPath = host.Hub.Address.ToString();
        var userNode = host.Workspace.GetMeshNodeStream().Select(node => (MeshNode?)node).StartWith((MeshNode?)null);
        return catalog.Contributions
            .CombineLatest(userNode, AdminAppNodeType.LiveAdminVerdict(host.Hub, owner), host.Hub.GetEffectivePermissions(userPath),
                (contributions, node, isAdmin, permissions) => UiContributionProjection.FilterInboxAreas(
                    UiContributionProjection.ProjectInboxAreas(contributions, userPath, node, isAdmin, owner), permissions))
            .DistinctUntilChanged(areas => string.Join('\n', areas.Select(a => $"{a.Id}|{a.Address}|{a.Area}")), StringComparer.Ordinal);
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
            // The medium the item came through, first, so a glance tells mail from a message from an
            // approval: a header and the source's name as the tooltip.
            .WithColumn(new TemplateColumnControl(Controls.Icon(new ContextProperty(InboxRows.SourceIconProperty)).WithWidth("20px"))
                .WithTitle(host.Localize("inbox.column.channel")).WithWidth("72px").WithSortable(false));
        // Outlook's columns: what, from whom, when. Ordered by date always: a header sort would be a
        // second, unbound ordering.
        foreach (var (property, key, width) in new[]
                 {
                     (nameof(InboxRow.Title), "inbox.column.item", "3fr"),
                     (nameof(InboxRow.StateText), "inbox.column.state", "150px"),
                     // The detailed status — what the target (or the run) says, beyond the badge.
                     (nameof(InboxRow.Status), "inbox.column.status", "1.5fr"),
                     (nameof(InboxRow.From), "inbox.column.source", "1.5fr"),
                     // Which of the viewer's accounts it reached — several mailboxes in one list.
                     (nameof(InboxRow.Account), "inbox.column.account", "1.5fr"),
                     (nameof(InboxRow.TagText), "inbox.column.tags", "1fr"),
                     (nameof(InboxRow.When), "inbox.column.when", "170px"),
                 })
        {
            // The lifecycle state as a badge — coloured by state, a check only for what is done, and
            // hidden (not drawn empty) for an item that has none.
            if (property == nameof(InboxRow.StateText))
            {
                grid = grid.WithColumn(new TemplateColumnControl(Controls.Badge(new ContextProperty(InboxRows.StateTextProperty)) with
                    {
                        BackgroundColor = new ContextProperty(InboxRows.StateColorProperty),
                        Color = "#ffffff",
                        Style = new ContextProperty(InboxRows.StateStyleProperty),
                    })
                    .WithTitle(host.Localize(key)).WithWidth(width).WithSortable(false));
                continue;
            }
            grid = grid.WithColumn(new PropertyColumnControl<string> { Property = property.ToCamelCase() }
                .WithTitle(host.Localize(key)).WithWidth(width).WithSortable(false));
        }
        return grid
            // A click on a row OPENS it, as in a mail client — always INSIDE the portal, in the
            // inbox's reading view (InboxReadingView), never the provider's own client. (Ticking a box
            // and shift/cmd-clicks are selection gestures the grid keeps to itself.) Paged, newest first.
            .WithClickAction(OpenRow)
            .WithShowHover()
            .WithItemsPerPage(50)
            .WithShowPageSizeSelector()
            .Resizable();
    }

    /// <summary>Opens the clicked row in the owner's reading view.</summary>
    private static void OpenRow(UiActionContext ctx)
    {
        var options = ctx.Hub.JsonSerializerOptions;
        var owner = UserNodeType.OwnerOf(ctx.Host.Hub.Address.ToString());
        var readers = ctx.Hub.ServiceProvider.GetServices<IInboxItemReader>().ToImmutableArray();
        // The payload is an object boundary: it may arrive typed or as JSON, so it is read through
        // the shared accessor, never a CLR pattern that drops the JSON envelope.
        if (OpenTarget(ctx.Payload.As<DataGridCellClick>(options)?.Item, options, owner,
                path => InboxReadingView.ReaderFor(readers, path) is not null) is { } target)
            ctx.NavigateTo(target.Url, forceLoad: target.External);
    }

    /// <summary>
    /// Where a clicked row goes: the owner's reading view of its path (<see cref="InboxReadingView.Url"/>)
    /// — inside the portal. The row's <see cref="InboxRow.Link"/> is offered ON the reading view, never
    /// followed by the click, with ONE transitional exception: a row that lives OUTSIDE the mesh (it
    /// carries an https link) whose source has not yet brought an <see cref="IInboxItemReader"/>
    /// (<paramref name="readable"/> says no) still opens in its provider's own client, because the
    /// reading view would have nothing to show for it — no store holds it. Once the source's package
    /// ships its reader, that row opens inside the portal too. Null for a row without a path, or one
    /// that cannot be read as a row. The row arrives typed or as JSON (read with the shared <c>As</c>
    /// accessor). Pure.
    /// </summary>
    /// <param name="item">The clicked row.</param>
    /// <param name="options">How the row is read.</param>
    /// <param name="owner">The inbox's owner.</param>
    /// <param name="readable">Whether a reader handles a path.</param>
    internal static (string Url, bool External)? OpenTarget(object? item, System.Text.Json.JsonSerializerOptions options, string owner,
        Func<string, bool> readable)
    {
        if (item.As<InboxRow>(options) is not { Path: { Length: > 0 } path } row || owner.Length == 0)
            return null;
        // An approval request is the approval: the click opens the node it names, never the notification that announced it.
        if (row.Target.Length > 0)
            return ("/" + row.Target.TrimStart('/'), false);
        // An approval of the ledger lives beside its document, in ANY partition: the owner's reading view
        // would refuse it ("not in your inbox"), so it opens at its own page, under the normal access check.
        if (string.Equals(row.NodeType, InboxQueries.ApprovalNodeType, StringComparison.Ordinal) && !InboxQueries.InPartition(path, owner))
            return ("/" + path.TrimStart('/'), false);
        return InboxReadingView.IsHttps(row.Link) && !readable(path)
            ? (row.Link, true)
            : (InboxReadingView.Url(owner, path), false);
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
                        acts.Reread();
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
                .Where(InboxRows.IsDecidable)
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
                    acts.Reread();
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
                    Unread = leg.Unread, ShownByDefault = leg.ShownByDefault, SourceIcon = InboxSourceIcons.Resolve(leg.Source, leg.SourceIcon),
                    TimeField = string.IsNullOrWhiteSpace(leg.TimeField) ? null : leg.TimeField,
                    Category = string.IsNullOrWhiteSpace(leg.Category) ? leg.Source : leg.Category,
                    CategoryLabelKey = string.IsNullOrWhiteSpace(leg.CategoryLabelKey) ? leg.SourceLabelKey : leg.CategoryLabelKey,
                    ConnectedWhen = string.IsNullOrWhiteSpace(leg.ConnectedWhen) ? null : leg.ConnectedWhen,
                    AlwaysOn = leg.AlwaysOn, DefaultKind = leg.DefaultKind,
                    StateKind = InboxStateKind.Resolve(leg.StateKind, string.IsNullOrWhiteSpace(leg.Category) ? leg.Source : leg.Category),
                    Ledger = InboxQueries.LedgerKindOf(leg.Query),
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
    private readonly BehaviorSubject<long> rereads = new(0);

    /// <summary>Opens the page's channel.</summary>
    public InboxActs()
        => fold = changes.Synchronize().Scan(Initial, (current, change) => change(current)).Subscribe(state);

    /// <summary>Ticks whenever the page should read every leg again — after a Delete or an Undo.</summary>
    public IObservable<long> Rereads => rereads;

    /// <summary>Asks the page to read every leg again; ignored once the page is closed.</summary>
    public void Reread()
        // A completed subject ignores what arrives after it, so a late call is dropped, never thrown.
        => rereads.OnNext(DateTime.UtcNow.Ticks);

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
        rereads.OnCompleted();
        fold.Dispose();
    }
}
