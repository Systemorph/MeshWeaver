using System.Reactive.Linq;
using System.Text.Json;
using HtmlAgilityPack;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Html;

/// <summary>
/// Replaces every layout-area placeholder in rendered markdown with real, static markup.
///
/// <para>The markdown pipeline emits an embedded area as an EMPTY anchor div
/// (<c>&lt;div class='layout-area' data-address=… data-area=…&gt;&lt;/div&gt;</c>) and leaves the
/// filling to a live client. Any export that skips this step therefore ships a document with
/// holes where its embeds were — which is precisely what PDF and DOCX do today. This resolver
/// closes that hole: it opens each area's synchronization stream server-side (the same stream the
/// browser subscribes to), snapshots the settled control tree, and swaps the anchor for the
/// serialized markup.</para>
/// </summary>
public static class LayoutAreaResolver
{
    /// <summary>Result of one document's area resolution — for logging and assertions.</summary>
    /// <param name="Resolved">Placeholders replaced with rendered markup.</param>
    /// <param name="Unresolved">Placeholders that produced nothing and became a visible notice.</param>
    public record Result(int Resolved, int Unresolved);

    /// <summary>
    /// Localization key for the notice that stands in for an area that could not be rendered.
    /// </summary>
    public const string UnavailableKey = "export.areaUnavailable";

    /// <summary>
    /// Resolves every layout-area placeholder in <paramref name="document"/> IN PLACE.
    ///
    /// <para><b>An area that cannot be resolved becomes a VISIBLE notice, never a silent gap.</b>
    /// The anchor the pipeline emits is an empty div, so leaving it (or deleting it) produces a
    /// document that looks complete and simply lacks a section the author put there — the reader
    /// has no way to tell that anything is missing, and neither does the author who is about to
    /// send it. A permission denial, an unknown area and a fetch failure are all real outcomes a
    /// reader must be able to see. The notice names the area and nothing else: a stack trace or a
    /// raw exception message in the middle of a document a user is about to email is not honesty,
    /// it is noise.</para>
    /// </summary>
    public static IObservable<Result> Resolve(
        HtmlDocument document,
        IMessageHub hub,
        DocumentHtmlOptions options,
        ILogger? logger = null)
    {
        var placeholders = document.DocumentNode
            .SelectNodes($"//div[contains(@class,'{LayoutAreaMarkdownRenderer.LayoutArea}')]")
            ?.ToList() ?? [];

        if (placeholders.Count == 0)
            return Observable.Return(new Result(0, 0));

        // Capture the caller's identity ONCE, here on the subscribing thread. The stream
        // creation below happens inside reactive continuations where the ambient AsyncLocal
        // context is hub-shaped or wiped; without re-applying this, the area subscription would
        // fall back to the System identity and bypass the owner's read gate — a caller denied
        // the embedded content would receive it in their export. Same care as MeshOperations.RenderArea.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var caller = accessService?.Context ?? accessService?.CircuitContext;

        return placeholders
            .ToObservable()
            .SelectMany(placeholder => RenderEmbed(
                    Attr(placeholder, LayoutAreaMarkdownRenderer.RawPath),
                    Attr(placeholder, LayoutAreaMarkdownRenderer.Address),
                    Attr(placeholder, LayoutAreaMarkdownRenderer.Area),
                    Attr(placeholder, LayoutAreaMarkdownRenderer.AreaId),
                    hub, options, caller, logger)
                .Select(markup => (placeholder, markup)))
            .ToList()
            .Select(results =>
            {
                var resolved = 0;
                var unresolved = 0;
                foreach (var (placeholder, markup) in results)
                {
                    var html = markup is null ? string.Empty : markup.Render();
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        var label = Attr(placeholder, LayoutAreaMarkdownRenderer.Area)
                                    ?? Attr(placeholder, LayoutAreaMarkdownRenderer.RawPath)
                                    ?? string.Empty;
                        html = UnavailableMarkup(hub, label).Render();
                        unresolved++;
                    }
                    else
                    {
                        resolved++;
                    }

                    var replacement = HtmlNode.CreateNode($"<div>{html}</div>");
                    placeholder.ParentNode.ReplaceChild(replacement, placeholder);
                }

                logger?.LogInformation(
                    "Export resolved {Resolved} of {Total} layout-area embeds",
                    resolved, resolved + unresolved);
                return new Result(resolved, unresolved);
            });
    }

    /// <summary>
    /// Renders ONE embed to a markup tree — the single entry point every export format shares.
    /// Emits <c>null</c> when the embed produced nothing, leaving the caller to substitute its own
    /// notice in whatever shape its output uses (a div for HTML, a paragraph for PDF/DOCX).
    /// Never faults: one unreachable area must not fail a whole document export.
    /// </summary>
    public static IObservable<MarkupNode?> RenderEmbed(
        string? rawPath,
        string? address,
        string? area,
        string? areaId,
        IMessageHub hub,
        DocumentHtmlOptions options,
        AccessContext? caller,
        ILogger? logger)
    {
        // The parser pre-resolves keyword embeds (`area:OgCard?urls=…`) into address/area/id and
        // leaves a bare `@@Node` reference to be resolved at render time — handle both, exactly
        // as the live client does.
        var resolution = !string.IsNullOrEmpty(address) && !string.IsNullOrEmpty(area)
            ? Observable.Return<(string Address, string? Area, string? Id)?>((address!, area, areaId))
            : ResolveRawPath(rawPath, hub);

        return resolution
            .SelectMany(target => target is null
                ? Observable.Return<MarkupNode?>(null)
                : RenderTarget(target.Value, hub, options, caller))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Export could not resolve layout-area embed {RawPath} (address={Address}, area={Area})",
                    rawPath, address, area);
                return Observable.Return<MarkupNode?>(null);
            });
    }

    /// <summary>
    /// The visible stand-in for an area that could not be rendered, as email/print markup.
    /// Localized off the caller's <see cref="AccessContext"/> — never <c>CurrentUICulture</c>,
    /// which does not survive the hub hop this resolution makes.
    /// </summary>
    public static MarkupNode UnavailableMarkup(IMessageHub hub, string label) =>
        MarkupNode.El("p")
            .Style(MarkupStyles.UnavailableNotice)
            .Add(MarkupNode.Text(UnavailableText(hub, label)));

    /// <summary>The localized notice text naming the area that could not be rendered.</summary>
    public static string UnavailableText(IMessageHub hub, string label) =>
        hub.ServiceProvider.GetService<AccessService>().Localize(UnavailableKey, label);

    private static IObservable<(string Address, string? Area, string? Id)?> ResolveRawPath(
        string? rawPath, IMessageHub hub)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return Observable.Return<(string, string?, string?)?>(null);

        var pathResolver = hub.ServiceProvider.GetRequiredService<IPathResolver>();
        return pathResolver.ResolvePath(rawPath.Trim('/'))
            .Take(1)
            .Select(resolution =>
            {
                if (resolution is null)
                    return ((string, string?, string?)?)null;
                var (parsedArea, parsedId) = LayoutAreaMarkdownParser.ParseAreaAndId(resolution.Remainder);
                return (resolution.Prefix, parsedArea, parsedId);
            });
    }

    private static IObservable<MarkupNode?> RenderTarget(
        (string Address, string? Area, string? Id) target,
        IMessageHub hub,
        DocumentHtmlOptions options,
        AccessContext? caller)
        => Observable.Defer(() =>
        {
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            using var callerScope = caller is not null
                ? accessService?.SwitchAccessContext(caller)
                : null;

            var reference = new LayoutAreaReference(target.Area) { Id = target.Id ?? string.Empty };
            var stream = hub.GetWorkspace()
                .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(target.Address), reference);
            if (stream is null)
                return Observable.Return<MarkupNode?>(null);

            return AreaMarkupRenderer
                .Render(stream, target.Area ?? string.Empty, options)
                .Select(node => node == MarkupNode.Empty ? null : node)
                .Finally(stream.Dispose);
        });

    private static string? Attr(HtmlNode node, string suffix)
    {
        var value = node.GetAttributeValue($"data-{suffix}", string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : HtmlEntity.DeEntitize(value);
    }
}
