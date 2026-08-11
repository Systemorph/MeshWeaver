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
/// <c>%</c> or commas work as written, and <c>urls</c> entries are comma-separated (a comma
/// inside a URL is written <c>%2C</c>). Mesh paths and external URLs can be mixed.</para>
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
            Title: preview.Title ?? HostOf(url),
            Description: preview.Description,
            ImageUrl: preview.CardIcon,
            Href: url);

    /// <summary>The placeholder card shown until a target's data arrives (and the terminal card
    /// for an unreachable one): its name alone, still a working link.</summary>
    private static MeshNodeCardControl PlaceholderCard(string target) =>
        IsExternal(target)
            ? new MeshNodeCardControl(NodePath: string.Empty, Title: HostOf(target), Href: target)
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
        var value = id.Trim().TrimStart('?');

        if (value.StartsWith("urls=", StringComparison.OrdinalIgnoreCase))
            return value["urls=".Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(SafeUnescape)
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .ToImmutableList();

        if (value.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
        {
            var single = SafeUnescape(value["url=".Length..].Trim());
            return string.IsNullOrWhiteSpace(single) ? [] : [single];
        }

        return [SafeUnescape(value)];
    }

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

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}
