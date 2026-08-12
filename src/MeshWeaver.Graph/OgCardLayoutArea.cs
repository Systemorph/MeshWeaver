using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// The <c>OgCard</c> layout area: a link-preview card (image + title + description, the whole
/// card a real link) for any target — an EXTERNAL page (its Open Graph head is fetched
/// server-side via <see cref="OpenGraphPreviewService"/>) or a SAME-MESH node (read live off its
/// node stream, exactly like every other node card). Multiple targets compose into one
/// responsive grid, so a markdown document embeds a whole card catalog with a single reference.
///
/// <para><b>Usage from markdown</b> (the standard <c>@@</c> layout-area embed):</para>
/// <code>
/// @@("Org/Doc/area/OgCard/Some/Node/Path")                        — one card for a mesh node
/// @@("Org/Doc/area/OgCard?url=https://example.org/Page")          — one card for an external page
/// @@("Org/Doc/area/OgCard?urls=https://a.org/X,https://a.org/Y")  — a responsive card grid
/// </code>
/// <para>External URLs travel in the <c>?url=</c>/<c>?urls=</c> query form — the query suffix is
/// the one areaId shape whose <c>://</c> survives every path-resolution hop verbatim. Entries may
/// be percent-encoded (they are unescaped once); plain URLs without <c>&amp;</c>, <c>=</c>,
/// <c>%</c> or commas work as written. Mesh paths and external URLs can be mixed.</para>
/// <para>🚨 A MULTI-target list is comma-separated in EVERY form — the <c>?urls=</c> query form
/// AND the bare path/areaId form alike — and the separator may arrive raw (<c>,</c>) or encoded
/// (<c>%2C</c>), since a path-form areaId is commonly percent-encoded as one whole segment. A
/// literal comma inside a URL must therefore be double-encoded (<c>%252C</c>), or carried by the
/// singular <c>?url=</c> form, which never splits.</para>
/// </summary>
public static class OgCardLayoutArea
{
    /// <summary>The area name.</summary>
    public const string AreaName = "OgCard";

    private const string GridStyle =
        "display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); " +
        "gap: 16px; width: 100%;";

    /// <summary>
    /// Renders the card (single target) or card grid (multiple targets) for the targets carried
    /// in the area id. Each target renders immediately as a URL/path-only placeholder card and
    /// fills in when its node stream / Open Graph fetch emits — one slow target never blocks the
    /// grid.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var targets = ParseTargets(host.Reference.Id?.ToString());
        if (targets.Count == 0)
            return Observable.Return<UiControl?>(
                Controls.Markdown(host.Localize("ui.ogCard.noTarget")));

        var cards = targets
            .Select(target => CardStream(host, target).StartWith(PlaceholderCard(target)))
            .ToArray();

        return cards.Length == 1
            ? cards[0].Select(card => (UiControl?)BuildLayout([card]))
            : Observable.CombineLatest(cards).Select(all => (UiControl?)BuildLayout(all.ToImmutableList()));
    }

    /// <summary>
    /// The live card stream of one target: external URLs through the mesh-scoped
    /// <see cref="OpenGraphPreviewService"/> promise cache, mesh paths off the shared node-stream
    /// handle (the same source every node card binds to).
    /// </summary>
    private static IObservable<MeshNodeCardControl> CardStream(LayoutAreaHost host, string target)
    {
        if (IsExternal(target))
        {
            var service = host.Hub.ServiceProvider.GetService<OpenGraphPreviewService>();
            return service is null
                // Minimal fixtures without AddGraph's mesh services: URL-only card, no fetch.
                ? Observable.Return(PlaceholderCard(target))
                : service.Get(target).Select(preview => ExternalCard(target, preview));
        }

        var path = target.Trim('/');
        return host.Hub.GetMeshNodeStream(path)
            .Select(node => MeshNodeCardControl.FromNode(node, path))
            // A read the viewer is denied (or a path that resolves to nothing) is a normal state
            // for a card catalog, not an area fault: keep the path-only placeholder, which still
            // navigates — the target enforces its own access when opened.
            .Catch<MeshNodeCardControl, Exception>(__ => Observable.Return(PlaceholderCard(target)));
    }

    /// <summary>An external target's card, built from its fetched Open Graph metadata (URL host
    /// as the title fallback when the page declared none). The visual is the page's ICON, not its
    /// og:image poster — see <see cref="OpenGraphPreview.CardIcon"/> for the order and why.</summary>
    private static MeshNodeCardControl ExternalCard(string url, OpenGraphPreview preview) =>
        new(NodePath: string.Empty,
            Title: preview.Title ?? LabelOf(url),
            Description: preview.Description,
            ImageUrl: preview.CardIcon,
            Href: url);

    /// <summary>The placeholder card shown until a target's data arrives (and the terminal card
    /// for an unreachable one): its name alone, still a working link.</summary>
    private static MeshNodeCardControl PlaceholderCard(string target) =>
        IsExternal(target)
            ? new MeshNodeCardControl(NodePath: string.Empty, Title: LabelOf(target), Href: target)
            : MeshNodeCardControl.FromNode(null, target.Trim('/'));

    /// <summary>One card renders width-bounded like a chat unfurl; several render as a
    /// responsive grid. Child areas are index-named (<c>Card0</c>, <c>Card1</c>, …) so a
    /// placeholder frame and its fetched successor diff onto the SAME child area.</summary>
    internal static UiControl BuildLayout(IReadOnlyList<MeshNodeCardControl> cards) =>
        cards
            .Select((card, index) => (card, index))
            .Aggregate(
                Controls.Stack.WithStyle(cards.Count == 1
                    ? "max-width: 480px; width: 100%;"
                    : GridStyle),
                (stack, item) => stack.WithView(item.card, $"Card{item.index}"));

    /// <summary>
    /// Reads the target list out of the area id. Two id shapes arrive here for the same embed —
    /// the markdown parser strips the leading <c>?</c> (<c>url=…</c>) while the runtime path
    /// resolution keeps it (<c>?url=…</c>) — so both are accepted. Entries are unescaped once,
    /// letting robust embeds percent-encode while plain URLs pass through unchanged.
    /// </summary>
    internal static IReadOnlyList<string> ParseTargets(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return [];
        var value = StripReferenceParameters(id.Trim().TrimStart('?'));

        if (value.StartsWith("urls=", StringComparison.OrdinalIgnoreCase))
            return SplitTargets(value["urls=".Length..]);

        if (value.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
        {
            // The SINGULAR form is exactly one target and never splits — a comma in it is data.
            var single = SafeUnescape(value["url=".Length..].Trim());
            return string.IsNullOrWhiteSpace(single) ? [] : [single];
        }

        return SplitTargets(value);
    }

    /// <summary>
    /// Drops REFERENCE PARAMETERS the client appends to the area id, which are configuration and
    /// never targets.
    /// </summary>
    /// <remarks>
    /// 🚨 An <c>@@</c> embed hides the embedded node's header, and the client carries that by
    /// appending to the <c>LayoutAreaReference.Id</c> — see
    /// <c>PathBasedLayoutArea.AppendParameter</c>, which picks its separator with
    /// <c>s.Contains('?') ? "&amp;" : "?"</c>. Our id already IS a query
    /// (<c>ParseAreaAndId</c> keeps the leading '?'), so the append lands as
    /// <c>?urls=A,B,C&amp;showHeader=false</c>.
    /// <para>Every other area reads its parameters with query semantics and is unaffected. This
    /// one does not: the multi-target list is COMMA-separated, so without this strip the trailing
    /// <c>&amp;showHeader=false</c> is swallowed into the LAST entry, turning it into
    /// <c>https://host/Page&amp;showHeader=false</c> — one path segment matching no route. The
    /// portal answers that 200 with its "does not match any registered address pattern" shell,
    /// which carries no og: tags, so the final card of EVERY <c>@@</c>-embedded grid renders blank
    /// and its link dead-ends. Found in production 2026-08-12: two grids of eight cards, and in
    /// each the eighth was empty.</para>
    /// <para>Splitting on the first '&amp;' is the documented contract, not a heuristic: a target
    /// carrying a literal <c>&amp;</c>, <c>=</c>, <c>%</c> or comma must be percent-encoded (see
    /// the class remarks), so an unencoded '&amp;' is always a parameter boundary.</para>
    /// </remarks>
    private static string StripReferenceParameters(string value)
    {
        var amp = value.IndexOf('&');
        return amp < 0 ? value : value[..amp];
    }

    /// <summary>
    /// Splits a comma-separated target list into its entries.
    ///
    /// <para>🚨 The split runs on BOTH sides of the single unescape, because the separator itself
    /// arrives raw or encoded depending on how the embed was authored. A PATH-form areaId is
    /// commonly percent-encoded as one whole path segment — every <c>:</c>, <c>/</c> and
    /// <c>,</c> alike — so its separators reach us as <c>%2C</c>; a query-form <c>?urls=</c> list
    /// usually keeps its commas raw. Splitting raw, unescaping, then splitting again handles
    /// either form (and a mix of the two) under one rule.</para>
    ///
    /// <para>Without this the path form did not split AT ALL: the whole joined string became a
    /// single target, so four URLs rendered as ONE card whose href was the four URLs concatenated
    /// — a fetch that could never succeed, degrading to a card titled with the bare domain that
    /// read as a broken link to the portal.</para>
    ///
    /// <para>Consequence of treating <c>%2C</c> as a separator: a literal comma INSIDE a URL must
    /// be double-encoded (<c>%252C</c>), or written with the singular <c>?url=</c> form, which
    /// never splits.</para>
    /// </summary>
    private static IReadOnlyList<string> SplitTargets(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SafeUnescape)
            .SelectMany(entry =>
                entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .ToImmutableList();

    /// <summary>Unescapes a user-authored target defensively: a malformed percent sequence in
    /// markdown must degrade to the raw token, never fault the whole area render.</summary>
    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static bool IsExternal(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The readable label for an external target whose page declared no title — the card's state
    /// before the fetch lands, and its terminal state when the target is unreachable.
    ///
    /// <para>The LAST PATH SEGMENT, not the bare host: a grid of cards all titled
    /// <c>memex.meshweaver.cloud</c> reads as "several broken links to the portal", while
    /// <c>Reinsurance</c> / <c>Underwriting</c> says which page each one actually points at. The
    /// href is always the real target, so the label and the link agree. Falls back to the host
    /// for an origin-root URL, which genuinely has no segment to name it by.</para>
    /// </summary>
    private static string LabelOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var segment = uri.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrWhiteSpace(segment) ? uri.Host : SafeUnescape(segment);
    }
}
