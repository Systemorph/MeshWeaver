using System.Globalization;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Application.Styles;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Global settings "What's New" tab — lists the platform release-note entries shipped as documentation
/// nodes under <see cref="WhatsNewNamespace"/> (one node per entry, produced by the <c>/pullrequest</c>
/// skill and shipped with the build), newest first. Each entry links to its full note in the normal
/// documentation view. Ungated: visible to every user. Reactive + pure <see cref="Controls"/> (no
/// hand-built HTML), and it only LISTS children (a valid query use) — the note content is read by the
/// doc view when opened, never from the lagging query index (CQRS rule).
///
/// <para><b>Shape of the list.</b> Entries are grouped by the day they shipped, newest day first.
/// Inside a day, a <see cref="FeatureCategory"/> entry gets its own line with its one-line
/// description; the day's <see cref="FixCategory"/> entries are bundled into a single "N fixes" line
/// of links. A release day routinely carries a handful of fixes and one or two features — listing all
/// of them flat (the pre-2026-08 shape) buried the features and made the page read as an
/// alphabetical wall, since the entry titles, not their dates, drove the eye.</para>
/// </summary>
public static class WhatsNewSettingsTab
{
    /// <summary>
    /// The tab id — the trailing segment of the tab's deep link, built with
    /// <see cref="GlobalSettingsNodeType.TabHref"/> (<c>/_Setting/GlobalSettings/WhatsNew</c>).
    /// </summary>
    public const string TabId = "WhatsNew";

    /// <summary>The documentation namespace holding per-entry release-note nodes (served under <c>Doc/</c>).</summary>
    public const string WhatsNewNamespace = "Doc/WhatsNew";

    /// <summary>
    /// The node type a release-note entry declares in its front matter (<c>nodeType: WhatsNew</c>)
    /// when it does NOT live under <see cref="WhatsNewNamespace"/> — which is every entry authored
    /// outside this repository.
    /// </summary>
    public const string EntryNodeType = "WhatsNew";

    /// <summary>
    /// The listing, as a UNION of two lanes (#2539) — <c>GetQuery</c> takes several queries and
    /// unions their result sets, deduped by path.
    ///
    /// <para><b>Why two.</b> The feed used to be a single <c>path:Doc/WhatsNew scope:children</c>
    /// listing, and that path exists only in the platform repository. A satellite — Plugins,
    /// Education, Reinsurance, SocialMedia, Memex — had NO route to file an entry from its own PR,
    /// so a user-noticeable fix landing there was simply absent from the changelog. That is not a
    /// gap that holds steady: every extraction moves more user-visible behaviour out of core, so
    /// the feed acquires a systematic bias toward platform work and a reader would conclude that
    /// plugin-side fixes had stopped happening.</para>
    ///
    /// <para>The second lane keys on the node TYPE instead of a path, so a satellite writes
    /// <c>WhatsNew/&lt;date&gt;-&lt;slug&gt;.md</c> in its own tree with <c>nodeType: WhatsNew</c>
    /// in the front matter and it appears in the one feed — authorship stays with the change and no
    /// cross-repo PR is needed. The 612 entries already under <c>Doc/WhatsNew</c> keep working
    /// untouched, which is why this is a union and not a migration.</para>
    /// </summary>
    public static readonly string[] ListingQueries =
    [
        $"path:{WhatsNewNamespace} scope:children",
        // The type lane is mesh-wide BY DESIGN — a satellite files its entries in its own tree —
        // and says so (#3202 — fan-out is opt-in).
        MeshWideQuery.OfType(EntryNodeType),
    ];

    /// <summary>
    /// <c>Category</c> of an entry that announces something new or improved — rendered in full, with
    /// its description. Written by the <c>/pullrequest</c> skill from the branch prefix.
    /// </summary>
    public const string FeatureCategory = "Feature";

    /// <summary>
    /// <c>Category</c> of an entry that announces a repair — bundled into the day's one-line "N
    /// fixes" summary rather than given a paragraph of its own.
    /// </summary>
    public const string FixCategory = "Fix";

    // The menu entry is a seeded UiContribution node (PlatformSettingsTabAreas) — the tab's
    // registration surface is the SettingsWhatsNew layout area, not a compiled provider.

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2(host.Localize("settings.whatsNew")).WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(host.Localize("ui.mdWhatsNewIntro")));

        // Live list of entry nodes under Doc/WhatsNew. Listing children is a valid query use; the
        // entry content is rendered by the doc view when the link is opened (never read from the
        // lagging query index).
        stack = stack.WithView((h, _) =>
            h.Hub.GetQuery("whatsnew:list", ListingQueries)
            .Select(nodes => (UiControl?)Controls.Markdown(Render(nodes, h)))
            // Generic message to the (ungated, any-user) UI — never surface the raw exception; log it
            // server-side instead so an internal detail can't leak into the page.
            .Catch<UiControl?, Exception>(ex =>
            {
                h.Hub.ServiceProvider.GetService<ILoggerFactory>()?
                    .CreateLogger(nameof(WhatsNewSettingsTab))
                    .LogWarning(ex, "What's New listing failed for {Namespace}", WhatsNewNamespace);
                return Observable.Return((UiControl?)Controls.Markdown(host.Localize("ui.mdWhatsNewFailed")));
            })
            .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoading"))));

        return stack;
    }

    /// <summary>
    /// The entries as markdown: one section per ship day, newest first, features spelled out and
    /// fixes bundled. A friendly note when there is nothing to show.
    /// </summary>
    private static string Render(IEnumerable<MeshNode> nodes, LayoutAreaHost host)
    {
        // DistinctBy: an entry that lives under Doc/WhatsNew AND declares nodeType:WhatsNew matches
        // both lanes. The synced layer already dedupes by path, so this is belt-and-braces — but a
        // duplicated entry is VISIBLE in the feed, and the cost of being sure is one operator.
        var entries = (nodes ?? []).Where(n => n is not null).DistinctBy(n => n.Path).ToList();
        if (entries.Count == 0)
            return host.Localize("ui.mdWhatsNewEmpty");

        // Explicit off the viewer's AccessContext — never ambient CurrentUICulture, which does not
        // survive the hub's scheduler hop (Localization.md).
        var culture = CultureInfo.GetCultureInfo(host.ViewerLocale());

        return string.Join("\n\n", entries
            .GroupBy(ShipDay)
            .OrderByDescending(g => g.Key ?? DateOnly.MinValue)
            .Select(g => RenderDay(g.Key, g, host, culture)));
    }

    /// <summary>One day's section: the date, then its features, then its bundled fixes.</summary>
    private static string RenderDay(
        DateOnly? day, IEnumerable<MeshNode> nodes, LayoutAreaHost host, CultureInfo culture)
    {
        // Path order inside a day is the authored order (date-prefixed ids), so the section is
        // stable across renders rather than reshuffling on every query emission.
        var ordered = nodes.OrderBy(n => n.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var fixes = ordered.Where(IsFix).ToList();

        var sb = new StringBuilder();
        if (day is { } shipped)
            sb.Append("### ").Append(FormatDay(shipped, culture)).Append("\n\n");

        foreach (var feature in ordered.Where(n => !IsFix(n)))
        {
            sb.Append("**").Append(Link(feature)).Append("**");
            if (!string.IsNullOrWhiteSpace(feature.Description))
                sb.Append("  \n").Append(feature.Description.Trim());
            sb.Append("\n\n");
        }

        if (fixes.Count > 0)
            sb.Append('_').Append(host.LocalizePlural("plural.fix", fixes.Count)).Append("_ — ")
                .Append(string.Join(" · ", fixes.Select(Link)));

        return sb.ToString().TrimEnd();
    }

    private static bool IsFix(MeshNode node)
        => string.Equals(node.Category, FixCategory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The day an entry shipped, read off its ISO-dated id (<c>2026-08-09-some-slug</c>) — the naming
    /// the skill writes. An entry that does not carry one sorts last, under no date heading.
    /// </summary>
    private static DateOnly? ShipDay(MeshNode node)
    {
        var id = LastSegment(node.Path);
        return id.Length >= 10
               && DateOnly.TryParseExact(
                   id[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }

    /// <summary>
    /// The date in the viewer's language, without the weekday: the culture's own long-date pattern
    /// with <c>dddd</c> removed, so English reads "9 August 2026" and German "9. August 2026" without
    /// either being hard-coded.
    /// </summary>
    private static string FormatDay(DateOnly day, CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.LongDatePattern;
        var weekday = pattern.IndexOf("dddd", StringComparison.Ordinal);
        if (weekday >= 0)
            pattern = pattern.Remove(weekday, 4).Trim(' ', ',');

        return day.ToDateTime(TimeOnly.MinValue).ToString(pattern, culture);
    }

    /// <summary>A markdown link to the entry's full note, label escaped so a bracketed title holds.</summary>
    private static string Link(MeshNode node)
        => $"[{(node.Name ?? LastSegment(node.Path)).Replace("[", "\\[").Replace("]", "\\]")}](/{node.Path})";

    private static string LastSegment(string path)
        => string.IsNullOrEmpty(path) ? path : path[(path.LastIndexOf('/') + 1)..];
}
