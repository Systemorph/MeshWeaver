using System.Collections.Immutable;
using System.Reactive.Linq;
using Markdig.Syntax;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Model;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Html;

/// <summary>
/// Resolves the layout areas a document embeds into <see cref="DocumentElement"/> content, ahead of
/// the synchronous <see cref="DocumentBuilder"/> pass that renders it.
///
/// <para>The split is forced by the two halves having different natures and is the same shape the
/// export already uses for client-captured Mermaid/Math SVGs. Reading a layout area is reactive and
/// cross-hub — it opens the area's synchronization stream, waits for the tree to settle and tears
/// the stream down — whereas building the document model is a pure, synchronous AST walk. So the
/// areas are resolved FIRST into a map, and the builder looks each one up by key. The builder stays
/// synchronous and testable; nothing blocks a hub action block waiting on a stream.</para>
///
/// <para>Keyed by the embed's raw path exactly as written, so two identical embeds resolve once and
/// share the result, and the key never depends on document order (an index-based key silently
/// mismatches the moment the two passes disagree about what counts as a block).</para>
/// </summary>
public static class DocumentAreaResolution
{
    /// <summary>
    /// Resolves every layout area embedded anywhere in <paramref name="chapters"/>.
    ///
    /// <para>Cold: subscribing runs the resolution. Emits once with the finished map — empty when
    /// the document embeds nothing, in which case not a single stream is opened.</para>
    /// </summary>
    /// <param name="hub">Hub used to open area streams and resolve paths.</param>
    /// <param name="chapters">The document's markdown bodies, in order.</param>
    /// <param name="nodePath">Owning node path — what makes a RELATIVE embed resolvable.</param>
    /// <param name="options">Settle window, timeout and card metrics.</param>
    /// <param name="logger">Optional logger.</param>
    public static IObservable<ImmutableDictionary<string, ImmutableArray<DocumentElement>>> Resolve(
        IMessageHub hub,
        IEnumerable<(string Title, string Markdown)> chapters,
        string? nodePath,
        DocumentHtmlOptions options,
        ILogger? logger = null)
        => Observable.Defer(() =>
        {
            var embeds = Collect(chapters, nodePath);
            if (embeds.Count == 0)
                return Observable.Return(ImmutableDictionary<string, ImmutableArray<DocumentElement>>.Empty);

            // Capture the caller's identity ONCE, on the subscribing thread. Stream creation happens
            // inside reactive continuations where the ambient context is hub-shaped or wiped; without
            // re-applying this the area would be read as System and bypass the owner's read gate, so
            // a caller denied the embedded content would receive it in their export.
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            var caller = accessService?.Context ?? accessService?.CircuitContext;

            return embeds
                .ToObservable()
                .SelectMany(embed => LayoutAreaResolver
                    .RenderEmbed(embed.RawPath, embed.Address, embed.Area, embed.Id,
                        hub, options, caller, logger)
                    .Select(markup => (embed.Key, Elements: ToElements(hub, embed, markup))))
                .ToList()
                .Select(results =>
                {
                    var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<DocumentElement>>();
                    foreach (var (key, elements) in results)
                        builder[key] = elements;

                    logger?.LogInformation(
                        "Document export resolved {Count} layout-area embed(s)", builder.Count);
                    return builder.ToImmutable();
                });
        });

    /// <summary>
    /// Maps one resolved embed to document elements, substituting the visible notice when the area
    /// produced nothing. The notice is a real paragraph, so it lands in the PDF/DOCX flow exactly
    /// where the embed was — never a blank the reader cannot notice.
    /// </summary>
    private static ImmutableArray<DocumentElement> ToElements(
        IMessageHub hub, Embed embed, MarkupNode? markup)
    {
        var elements = MarkupToDocument.Convert(markup);
        if (!elements.IsEmpty)
            return elements;

        var label = embed.Area ?? embed.RawPath ?? string.Empty;
        return
        [
            new ParagraphElement(
            [
                new TextInline(
                    LayoutAreaResolver.UnavailableText(hub, label),
                    Bold: false, Italic: true, Strike: false)
            ])
        ];
    }

    /// <summary>
    /// Finds every embed in the document, de-duplicated by raw path. Parses with
    /// <see cref="ExportMarkdownPipeline"/> — the SAME pipeline <see cref="DocumentBuilder"/>
    /// renders with, so the set found here is exactly the set the builder will look up.
    /// </summary>
    private static IReadOnlyList<Embed> Collect(
        IEnumerable<(string Title, string Markdown)> chapters, string? nodePath)
    {
        var pipeline = ExportMarkdownPipeline.For(nodePath);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var embeds = new List<Embed>();

        foreach (var (_, markdown) in chapters)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                continue;

            var document = Markdig.Markdown.Parse(markdown, pipeline);
            foreach (var info in document.Descendants<LayoutAreaComponentInfo>())
            {
                var embed = Embed.From(info);
                if (seen.Add(embed.Key))
                    embeds.Add(embed);
            }
        }

        return embeds;
    }

    /// <summary>One embed as the resolver needs it: the key plus the parser's resolution.</summary>
    private sealed record Embed(string Key, string? RawPath, string? Address, string? Area, string? Id)
    {
        public static Embed From(LayoutAreaComponentInfo info)
        {
            var rawPath = info.RawPath;
            var address = info.Address?.ToString();
            var area = info.Area;
            var id = info.Id?.ToString();

            // Raw path is the author's own spelling and is always present for an `@@(…)` embed;
            // the address/area triple is the fallback for a block the parser built directly.
            var key = !string.IsNullOrWhiteSpace(rawPath)
                ? rawPath!
                : $"{address}|{area}|{id}";
            return new Embed(key, rawPath, address, area, id);
        }
    }

    /// <summary>
    /// The key <see cref="DocumentBuilder"/> looks a resolved embed up by. Kept here so the two
    /// sides cannot disagree about how a key is formed.
    /// </summary>
    public static string KeyFor(LayoutAreaComponentInfo info) => Embed.From(info).Key;
}
