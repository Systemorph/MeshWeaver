using System.Collections.Immutable;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The <b>data</b> half of the node menu: how each entry PRESENTS itself (text, icon, position,
/// nesting, whether it shows at all). One node per menu context, so changing a label, an icon, an
/// order or a grouping is a node edit — no build, no image, no rollout.
///
/// <para>🚨 Presentation is data; <b>applicability stays code</b>. Whether an entry may appear at
/// all — the viewer's permission, the node's type, whether sync is configured, whether this is a
/// protected partition root — is decided by the framework providers that emit the item, because
/// those predicates read live hub state. This catalog never grants visibility it only takes it
/// away (<see cref="MenuEntryPresentation.Hidden"/>) or re-dresses what a provider already
/// decided to emit. That split is deliberate: it keeps the security-relevant decision in compiled,
/// CI-tested code while making the part people actually iterate on — the wording and the shape of
/// the menu — editable at runtime. See <c>Doc/Architecture/MenuAsData</c>.</para>
///
/// <para><b>Fail-safe by construction.</b> The catalog is an OVERLAY over the items the code
/// providers emit. A missing catalog node, a corrupt one, or an entry naming an area nobody emits
/// all reduce to "no override applied" — the menu still renders exactly the compiled defaults.
/// There is no state in which a bad edit produces an empty menu.</para>
/// </summary>
/// <param name="Context">
/// The menu context these entries dress — <c>Node</c>, <c>Mesh</c>, <c>AI</c>, <c>GitHub</c>, or any
/// named context a provider declares. Matches <c>INodeMenuProvider.Context</c>.
/// </param>
/// <param name="Entries">The per-entry presentation, keyed by <see cref="MenuEntryPresentation.Area"/>.</param>
public record MenuPresentation(string Context = "Node", IReadOnlyList<MenuEntryPresentation>? Entries = null)
{
    /// <summary>Partition holding the menu catalog. Platform configuration, so it lives with the
    /// rest of it on the Admin partition — editing it changes navigation for EVERY viewer, which is
    /// a platform-admin act, not a per-space one.</summary>
    public const string CatalogPartition = "Admin";

    /// <summary>Catalog name for the unnamed (root <c>$Menu</c>) context — kept distinct from
    /// <c>Node</c> so the root slot and the Node dropdown cannot accidentally share one catalog.</summary>
    public const string DefaultContextName = "Default";

    /// <summary>Node path of the catalog for <paramref name="context"/> (e.g. <c>Admin/Menu/Node</c>).</summary>
    public static string PathFor(string context)
        => $"{CatalogPartition}/Menu/{(string.IsNullOrWhiteSpace(context) ? DefaultContextName : context)}";

    /// <summary>Per-entry presentation, keyed by area.</summary>
    public IReadOnlyList<MenuEntryPresentation> Entries { get; init; } = Entries ?? [];

    /// <summary>
    /// Indexes <see cref="Entries"/> by <see cref="MenuEntryPresentation.Area"/> for lookup during
    /// menu aggregation. Entries with a blank area are dropped — and <paramref name="onSkipped"/> is
    /// invoked with the reason so the drop is NAMED in the log rather than silently swallowed. A
    /// duplicate area keeps the FIRST entry and reports the rest, so a copy-paste edit degrades to
    /// "the first one wins" instead of an unpredictable last-write.
    /// </summary>
    public ImmutableDictionary<string, MenuEntryPresentation> Index(Action<string>? onSkipped = null)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, MenuEntryPresentation>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Entries.Count; i++)
        {
            var entry = Entries[i];
            if (entry is null)
            {
                onSkipped?.Invoke($"entry #{i} in menu catalog '{Context}' is null");
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.Area))
            {
                onSkipped?.Invoke($"entry #{i} in menu catalog '{Context}' has no Area and cannot be matched to a menu item");
                continue;
            }
            if (builder.ContainsKey(entry.Area))
            {
                onSkipped?.Invoke($"entry #{i} in menu catalog '{Context}' repeats Area '{entry.Area}'; the first entry wins");
                continue;
            }
            builder.Add(entry.Area, entry);
        }
        return builder.ToImmutable();
    }
}

/// <summary>
/// Presentation for ONE menu entry, matched to the item a provider emits by
/// <see cref="Area"/> — the stable identity every <see cref="NodeMenuItemDefinition"/> already
/// carries (it is the navigation target, so it cannot drift without the navigation drifting too).
///
/// <para>Every property is optional and <c>null</c> means "leave the compiled value alone", so a
/// catalog entry is a patch, not a replacement. An entry that sets nothing is a no-op rather than a
/// way to accidentally blank a label.</para>
/// </summary>
/// <param name="Area">
/// The item's <see cref="NodeMenuItemDefinition.Area"/> — the match key (case-insensitive).
/// </param>
/// <param name="Labels">
/// Locale tag → display text (e.g. <c>{"en": "Edit", "de": "Bearbeiten"}</c>). This is what makes a
/// label change build-free: today a label lives in <c>strings.{en,de}.json</c>, which is COMPILED
/// INTO the hub assembly, so re-wording a menu entry costs a full CI + image + rollout cycle.
/// Carrying the text per locale here moves that edit into data. Resolution falls back
/// locale → English → the compiled label, so a missing translation degrades to the current
/// behaviour rather than to an empty entry.
/// </param>
/// <param name="Tooltips">Locale tag → hover tooltip. Same fallback chain as <paramref name="Labels"/>.</param>
/// <param name="Icon">Replacement icon (emoji or SVG URL). Null leaves the compiled icon.</param>
/// <param name="Order">Replacement sort order — this is how a group is re-arranged without a build.</param>
/// <param name="Hidden">
/// When true the item is dropped from the menu. This only ever REMOVES an entry the viewer was
/// already entitled to see; it can never reveal one, so a catalog edit cannot widen access.
/// </param>
/// <param name="Parent">
/// <see cref="Area"/> of the entry this one nests under, rendered as a sub-menu via
/// <see cref="NodeMenuItemDefinition.Children"/>. This is how a flat, messy menu is grouped without
/// touching a provider.
///
/// <para>The parent must be an entry some provider already emits at top level — the overlay is
/// override-only and never CONJURES a container, for the same reason it never introduces an entry:
/// nothing on this menu filters on <see cref="NodeMenuItemDefinition.RequiredPermission"/>, so a
/// data-added item would carry no gate. A cyclic, self-referencing or unresolvable parent is reported
/// and the child stays top-level rather than vanishing. To ship a NEW group, a provider emits it with
/// a <c>NodeMenuItemDefinition.GroupArea(name)</c> area — that group is then addressable from here
/// like any other entry.</para>
/// </param>
public record MenuEntryPresentation(
    string Area = "",
    IReadOnlyDictionary<string, string>? Labels = null,
    IReadOnlyDictionary<string, string>? Tooltips = null,
    string? Icon = null,
    int? Order = null,
    bool Hidden = false,
    string? Parent = null)
{
    /// <summary>
    /// Applies this presentation to <paramref name="item"/> for a viewer in <paramref name="locale"/>.
    /// Returns the item unchanged where this entry says nothing.
    /// </summary>
    public NodeMenuItemDefinition Apply(NodeMenuItemDefinition item, string locale)
    {
        var label = Resolve(Labels, locale);
        var tooltip = Resolve(Tooltips, locale);

        return item with
        {
            // A data label wins over the compiled LabelKey — and clears the key, so the central
            // Localized(access) pass does not translate the override back to the compiled text.
            Label = label ?? item.Label,
            LabelKey = label is not null ? null : item.LabelKey,
            Tooltip = tooltip ?? item.Tooltip,
            TooltipKey = tooltip is not null ? null : item.TooltipKey,
            Icon = Icon ?? item.Icon,
            Order = Order ?? item.Order,
        };
    }

    /// <summary>
    /// Picks the text for <paramref name="locale"/>: exact tag, then the base language (<c>de-CH</c>
    /// → <c>de</c>), then English, then null so the caller keeps the compiled value.
    /// </summary>
    internal static string? Resolve(IReadOnlyDictionary<string, string>? byLocale, string? locale)
    {
        if (byLocale is null || byLocale.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(locale))
        {
            if (TryGet(byLocale, locale, out var exact))
                return exact;

            var dash = locale.IndexOf('-');
            if (dash > 0 && TryGet(byLocale, locale[..dash], out var baseLang))
                return baseLang;
        }

        return TryGet(byLocale, Locales.Default, out var english) ? english : null;
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> map, string key, out string? value)
    {
        foreach (var kvp in map)
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(kvp.Value))
            {
                value = kvp.Value;
                return true;
            }

        value = null;
        return false;
    }
}
