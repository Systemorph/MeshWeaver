using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Seo;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// 🚨 WHAT IS ON THE INTERNET — the published surface, in a list you can actually look at.
///
/// <para>"Published to the web" has always been a real, enforced state: an explicit <b>Anonymous
/// Read grant</b>, which <c>AnonymousGate</c> fails closed on and which decides whether a page
/// renders for a logged-out visitor, whether it appears in <c>/sitemap.xml</c>, and whether it gets
/// SEO metadata and a share card. What it was NOT is <i>visible</i> — nothing in the product told
/// you which nodes were public, so the honest answer to "what does the world see?" was to fetch the
/// sitemap and read XML.</para>
///
/// <para>This tab is that answer, rendered. It reads
/// <see cref="SeoEndpoints.EnumeratePublished"/> — the SAME enumeration the sitemap renders — so the
/// list a human reads and the list crawlers get cannot drift apart.</para>
///
/// <para><b>Publishing moves nothing.</b> A page's public URL is its ordinary mesh path; there is no
/// <c>Www/</c> namespace and deliberately so — relocating public content would rewrite every shared
/// link, every canonical and every <c>og:url</c>, which is the opposite of publishing well.</para>
///
/// <para>Deliberately READ-ONLY. Granting or revoking public access is an access decision and lives
/// on the node's Access tab; offering an edit control here would put the truth in two places.</para>
/// </summary>
public static class PublishedSettingsTab
{
    /// <summary>The tab's stable id.</summary>
    public const string TabId = "PublishedToTheWeb";

    /// <summary>Registers the "Published to the web" tab on the global settings menu.</summary>
    public static MessageHubConfiguration AddPublishedSettingsTab(this MessageHubConfiguration config)
        => config.AddGlobalSettingsMenuItems(new GlobalSettingsMenuItemProvider(GetPublishedTab));

    private static IObservable<IReadOnlyList<GlobalSettingsMenuItemDefinition>> GetPublishedTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var tab = new GlobalSettingsMenuItemDefinition(
            Id: TabId,
            Label: "Published to the web",
            ContentBuilder: BuildContent,
            Group: "Administration",
            Icon: FluentIcons.Globe(),
            GroupIcon: FluentIcons.Shield(),
            Order: 320)
        { LabelKey = "settings.published", GroupKey = "settings.groupAdministration" };

        // Reactive, gated like its sibling admin tabs: the published surface is not secret, but
        // "what is public" is an administration question.
        return AdminMenuGate.IsPlatformAdmin(host)
            .Select(isAdmin => isAdmin
                ? (IReadOnlyList<GlobalSettingsMenuItemDefinition>)new[] { tab }
                : []);
    }

    /// <summary>One published page, as a plain row the grid binds to.</summary>
    public sealed record PublishedRow
    {
        /// <summary>The page's display name.</summary>
        public string Page { get; init; } = "";
        /// <summary>The public URL path — the node's ordinary path; publishing moves nothing.</summary>
        public string Url { get; init; } = "";
        /// <summary>The node type, so a Space reads differently from a store plugin.</summary>
        public string Type { get; init; } = "";
        /// <summary>The authored share image, or "generated card" when the portal draws one.</summary>
        public string ShareImage { get; init; } = "";
        /// <summary>When the page last changed — a stale public page is worth seeing.</summary>
        public DateTime Changed { get; init; }
    }

    private static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
        => stack
            .WithView(Controls.Markdown(host.Localize("ui.mdPublishedIntro")))
            .WithView((h, _) => LivePublishedList(h), "PublishedList");

    /// <summary>
    /// The published surface, resolved once per render. Cold and bounded by the enumeration's own
    /// timeout; fails open to an explanatory line rather than an empty tab.
    /// </summary>
    private static IObservable<UiControl?> LivePublishedList(LayoutAreaHost host) =>
        SeoEndpoints.EnumeratePublished(host.Hub)
            .Select(pages => (UiControl?)BuildGrid(host, pages))
            .Catch<UiControl?, Exception>(ex => Observable.Return<UiControl?>(
                Controls.Markdown($"The published surface could not be read: {ex.Message}")))
            .StartWith(Controls.Markdown("Reading the published surface…"));

    /// <summary>The published surface as grid rows, ordered by URL so it reads as a site map.</summary>
    public static PublishedRow[] RowsFor(IReadOnlyList<PublishedPage> pages) =>
        pages
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .Select(RowFor)
            .ToArray();

    /// <summary>One published page as a row. Pure, so the columns are testable without a mesh.</summary>
    public static PublishedRow RowFor(PublishedPage page) => new()
    {
        Page = page.Node.Name ?? page.Node.Id,
        // The node's ordinary path IS its public URL — publishing never relocates anything.
        Url = "/" + page.Path,
        Type = page.Node.NodeType ?? "",
        // The same precedence the crawler-facing head uses, so this column cannot claim one thing
        // while the og:image meta tag says another.
        ShareImage = SeoResolver.ExtractImage(page.Node) ?? "generated card",
        Changed = page.Node.LastModified.UtcDateTime,
    };

    private static UiControl BuildGrid(LayoutAreaHost host, IReadOnlyList<PublishedPage> pages)
    {
        if (pages.Count == 0)
            return Controls.Markdown(host.Localize("ui.mdPublishedNone"));

        var rows = RowsFor(pages);

        return Controls.Stack
            .WithView(Controls.Markdown(
                $"**{rows.Length} page{(rows.Length == 1 ? "" : "s")}** published."))
            .WithView(Controls.DataGrid(rows)
                .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PublishedRow.Page).ToCamelCase() }.WithTitle("Page"))
                .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PublishedRow.Url).ToCamelCase() }.WithTitle("Public URL"))
                .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PublishedRow.Type).ToCamelCase() }.WithTitle("Type"))
                .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PublishedRow.ShareImage).ToCamelCase() }.WithTitle("Share image"))
                .WithColumn(new PropertyColumnControl<DateTime>
                { Property = nameof(PublishedRow.Changed).ToCamelCase() }.WithTitle("Last changed")));
    }
}
