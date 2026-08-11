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

namespace MeshWeaver.Markdown.Export.Email;

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
public static class EmailAreaResolver
{
    /// <summary>Result of one document's area resolution — for logging and assertions.</summary>
    /// <param name="Resolved">Placeholders replaced with rendered markup.</param>
    /// <param name="Unresolved">Placeholders that produced nothing and were removed.</param>
    public record Result(int Resolved, int Unresolved);

    /// <summary>
    /// Resolves every layout-area placeholder in <paramref name="document"/> IN PLACE.
    ///
    /// <para>An area that cannot be resolved has its empty anchor REMOVED rather than left in the
    /// output: an empty div is invisible in a browser but shows as a stray gap in mail, and it
    /// would silently misrepresent the document as complete.</para>
    /// </summary>
    public static IObservable<Result> Resolve(
        HtmlDocument document,
        IMessageHub hub,
        EmailHtmlOptions options,
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
            .SelectMany(placeholder => RenderPlaceholder(placeholder, hub, options, caller, logger)
                .Select(markup => (placeholder, markup)))
            .ToList()
            .Select(results =>
            {
                var resolved = 0;
                var unresolved = 0;
                foreach (var (placeholder, markup) in results)
                {
                    if (string.IsNullOrWhiteSpace(markup))
                    {
                        placeholder.Remove();
                        unresolved++;
                        continue;
                    }

                    var replacement = HtmlNode.CreateNode($"<div>{markup}</div>");
                    placeholder.ParentNode.ReplaceChild(replacement, placeholder);
                    resolved++;
                }

                logger?.LogInformation(
                    "Email export resolved {Resolved} of {Total} layout-area embeds",
                    resolved, resolved + unresolved);
                return new Result(resolved, unresolved);
            });
    }

    private static IObservable<string> RenderPlaceholder(
        HtmlNode placeholder,
        IMessageHub hub,
        EmailHtmlOptions options,
        AccessContext? caller,
        ILogger? logger)
    {
        var address = Attr(placeholder, LayoutAreaMarkdownRenderer.Address);
        var area = Attr(placeholder, LayoutAreaMarkdownRenderer.Area);
        var areaId = Attr(placeholder, LayoutAreaMarkdownRenderer.AreaId);
        var rawPath = Attr(placeholder, LayoutAreaMarkdownRenderer.RawPath);

        // The parser pre-resolves keyword embeds (`area:OgCard?urls=…`) into address/area/id and
        // leaves a bare `@@Node` reference to be resolved at render time — handle both, exactly
        // as the live client does.
        var resolution = !string.IsNullOrEmpty(address) && !string.IsNullOrEmpty(area)
            ? Observable.Return<(string Address, string? Area, string? Id)?>((address!, area, areaId))
            : ResolveRawPath(rawPath, hub);

        return resolution
            .SelectMany(target => target is null
                ? Observable.Return(string.Empty)
                : RenderTarget(target.Value, hub, options, caller))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Email export could not resolve layout-area embed {RawPath} (address={Address}, area={Area})",
                    rawPath, address, area);
                return Observable.Return(string.Empty);
            });
    }

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

    private static IObservable<string> RenderTarget(
        (string Address, string? Area, string? Id) target,
        IMessageHub hub,
        EmailHtmlOptions options,
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
                return Observable.Return(string.Empty);

            return EmailControlRenderer
                .Render(stream, target.Area ?? string.Empty, options)
                .Select(node => node.Render())
                .Finally(stream.Dispose);
        });

    private static string? Attr(HtmlNode node, string suffix)
    {
        var value = node.GetAttributeValue($"data-{suffix}", string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : HtmlEntity.DeEntitize(value);
    }
}
