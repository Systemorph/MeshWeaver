using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Ast;

/// <summary>
/// The export's view of a node's markdown body — <see cref="MarkdownBody.Of"/> plus the one
/// diagnostic that only an export can state: "this chapter came out empty".
///
/// <para>🚨 It lives here, in compiled framework code, because the three export templates
/// (<c>ExportPdf.csx</c>, <c>ExportDocx.csx</c>, <c>ExportHtml.csx</c>) are <c>.csx</c> compiled at
/// RUNTIME: no <c>dotnet build</c> ever type-checks them and no unit test could reach a private
/// local function inside one. Three hand-copied extractors is how the trap-door defect (#1374)
/// survived — and how a fix applied to one template would have drifted from the other two. The
/// reading itself moved one level down to <see cref="MarkdownBody"/> for the same reason: the
/// sample Article views are runtime-compiled NodeType source carrying their OWN hand-copied copy of
/// the same extractor (#1383), so there must be exactly one implementation for both to call.</para>
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
        // The shared reader: it covers the already-typed value, the degraded JsonElement/JsonNode, a
        // same-short-named MarkdownContent from another build, and a bare-string body. Deliberately
        // WITHOUT the logger — every non-markdown descendant of an IncludeChildren export lands here
        // too, and As<T> logs an error for a typed-but-foreign value. The one genuinely diagnosable
        // case is reported explicitly below instead, so the diagnosis survives without the storm.
        if (MarkdownBody.Of(node, options) is { } markdown)
            return markdown;

        // JSON-shaped content on a node that DECLARES itself a markdown document, yet no body came
        // out of it. That is the export silently losing its content, so say so rather than
        // returning an empty chapter and letting the author conclude they wrote an empty document.
        //
        // The NodeType is what keeps this a diagnosis instead of a storm: with IncludeChildren an
        // export walks every descendant, and a Todo or a Product whose typed content arrived as
        // JSON is not a fault, it is simply not a chapter. Only a node that was supposed to have a
        // body gets a line.
        if (node?.Content is JsonElement or JsonNode
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
