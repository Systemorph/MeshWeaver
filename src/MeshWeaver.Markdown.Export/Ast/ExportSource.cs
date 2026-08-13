using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Ast;

/// <summary>
/// Reads the markdown BODY out of an exported <see cref="MeshNode"/> — the one place every export
/// template asks "what text does this node contribute to the document?".
///
/// <para>🚨 It lives here, in compiled framework code, because the three export templates
/// (<c>ExportPdf.csx</c>, <c>ExportDocx.csx</c>, <c>ExportHtml.csx</c>) are <c>.csx</c> compiled at
/// RUNTIME: no <c>dotnet build</c> ever type-checks them and no unit test could reach a private
/// local function inside one. Three hand-copied extractors is how the defect below survived — and
/// how a fix applied to one template would have drifted from the other two.</para>
///
/// <para><b>The defect (#1374).</b> Each copy read the body with a direct type test:</para>
/// <code>
/// if (node.Content is MarkdownContent mc) return mc.Content ?? "";   // ❌ trap-door
/// </code>
/// <para>That is correct only when the value already happens to be that CLR type. A node whose
/// content was stored as plain JSON — an import, an MCP <c>create</c>/<c>patch</c> carrying a raw
/// body, a document written before its content type existed — has no <c>$type</c> discriminator for
/// the polymorphic converter to resolve, so it arrives (and STAYS, even on the node's own owning
/// hub) a raw <see cref="JsonElement"/>. The test is simply false, the extractor returns <c>""</c>,
/// and the export produces a cover page, a contents list and <b>no body</b> — no exception, no log
/// line, nothing to grep, and it reads to the author as their own mistake rather than a platform
/// bug. <see cref="MeshNodeContentExtensions.ContentAs{T}"/> recovers exactly that shape (and a
/// same-short-named <c>MarkdownContent</c> from another dynamic-node build, by JSON round-trip).</para>
///
/// <para><b>Why this is a legitimate boundary read and not a routing mistake.</b> The rule's first
/// question is whether the value should have been deserialized closer to where its type IS
/// registered. Here it could not be: the shape that fails carries <b>no discriminator at all</b>, so
/// no registry can type it — measured on the monolith, a <c>Markdown</c> node whose content was
/// written as bare JSON is still a <see cref="JsonElement"/> when read from its OWN per-node hub,
/// the very hub that declares <c>WithContentType&lt;MarkdownContent&gt;()</c>. Moving the read would
/// change nothing. Reading stored JSON whose type cannot be resolved is the textbook case the
/// accessor exists for.</para>
/// </summary>
public static class ExportSource
{
    /// <summary>
    /// The node's markdown body, or <c>""</c> when it has none.
    ///
    /// <para>Returning <c>""</c> for a node that is genuinely not markdown-bodied is deliberate and
    /// is what callers probe on: with <c>IncludeChildren</c> an export walks EVERY descendant, most
    /// of which carry typed non-markdown content, and each is skipped on an empty body. Only a
    /// node whose content is JSON-shaped — i.e. one that could have been a markdown body and could
    /// not be read as one — is worth a log line, and that is the only case that emits one.</para>
    /// </summary>
    /// <param name="node">The node to read. A null node yields <c>""</c>.</param>
    /// <param name="options">
    /// The reading hub's <c>JsonSerializerOptions</c> (<c>Mesh.JsonSerializerOptions</c> in a
    /// template) — the registry behind them is what resolves a <c>$type</c>.
    /// </param>
    /// <param name="logger">
    /// Optional. Without it an unreadable JSON body is still returned as <c>""</c>, just without the
    /// diagnosis — which is the state that made #1374 invisible for as long as it was.
    /// </param>
    public static string MarkdownOf(MeshNode? node, JsonSerializerOptions options, ILogger? logger = null)
    {
        if (node?.Content is null)
            return "";

        // The accessor FIRST: it covers the already-typed value, the degraded JsonElement/JsonNode,
        // and a same-short-named MarkdownContent from another build. Deliberately without the
        // logger — every non-markdown descendant of an IncludeChildren export lands here too, and
        // As<T> logs an error for a typed-but-foreign value. The one genuinely diagnosable case is
        // reported explicitly below instead, so the diagnosis survives without the storm.
        if (node.ContentAs<MarkdownContent>(options)?.Content is { } markdown)
            return markdown;

        // A body stored as a bare string, either still typed or degraded to a JSON string.
        if (node.Content is string s)
            return s;
        if (node.Content is JsonElement { ValueKind: JsonValueKind.String } je)
            return je.GetString() ?? "";
        if (node.Content is JsonValue jv && jv.TryGetValue<string>(out var raw))
            return raw;

        // JSON-shaped content on a node that DECLARES itself a markdown document, yet no body came
        // out of it. That is the export silently losing its content, so say so rather than
        // returning an empty chapter and letting the author conclude they wrote an empty document.
        //
        // The NodeType is what keeps this a diagnosis instead of a storm: with IncludeChildren an
        // export walks every descendant, and a Todo or a Product whose typed content arrived as
        // JSON is not a fault, it is simply not a chapter. Only a node that was supposed to have a
        // body gets a line.
        if (node.Content is JsonElement or JsonNode
            && string.Equals(node.NodeType, MarkdownNodeType.NodeType, StringComparison.Ordinal))
            logger?.LogWarning(
                "Export: {Path} is a {NodeType} node whose content is JSON that yields no markdown "
                + "body; its chapter will be empty. Content: {Raw}",
                node.Path,
                node.NodeType,
                Excerpt(node.Content is JsonElement el ? el.GetRawText() : node.Content.ToString() ?? ""));

        return "";
    }

    /// <summary>Head of an unreadable body, bounded — the value being logged can be megabytes.</summary>
    private static string Excerpt(string rawJson) =>
        rawJson.Length <= RawJsonLogLimit
            ? rawJson
            : $"{rawJson[..RawJsonLogLimit]}… [{rawJson.Length - RawJsonLogLimit} more chars]";

    private const int RawJsonLogLimit = 512;
}
