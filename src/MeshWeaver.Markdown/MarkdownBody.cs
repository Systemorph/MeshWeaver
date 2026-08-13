using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown;

/// <summary>
/// Reads the markdown BODY out of a <see cref="MeshNode"/> — the one place anything on the mesh
/// asks "what text does this node contribute?".
///
/// <para>🚨 It lives here, in compiled framework code, because every caller that needs it is
/// invisible to <c>dotnet build</c>. The export templates are <c>.csx</c> compiled at RUNTIME
/// (#1374/#1381), and the sample Article views are NodeType <c>Source/*.cs</c> stored in mesh nodes
/// and compiled at RUNTIME too — no build and no test type-checks either. Hand-copying the
/// extractor into each is exactly how the defect below survived: it was pasted
/// character-for-character into three sample spaces (ACME, Northwind, Cornerstone) and nothing
/// anywhere could flag the second and third copies (#1383).</para>
///
/// <para><b>The defect.</b> Each copy read the body with a direct type test:</para>
/// <code>
/// if (node.Content is MarkdownContent mc) return mc.Content;   // ❌ trap-door
/// </code>
/// <para>That is correct only when the value already happens to be that CLR type. A node whose
/// content was stored as plain JSON — an import, an MCP <c>create</c>/<c>patch</c> carrying a raw
/// body, a document written before its content type existed — has no <c>$type</c> discriminator for
/// the polymorphic converter to resolve, so it arrives (and STAYS, even on the node's own owning
/// hub) a raw <see cref="JsonElement"/>. The test is simply false, the reader returns nothing, and
/// the article renders as a bare title with no body — no exception, no log line, nothing to grep.
/// <see cref="MeshNodeContentExtensions.ContentAs{T}"/> recovers exactly that shape, and a
/// same-short-named <see cref="MarkdownContent"/> from another dynamic-node build by JSON
/// round-trip.</para>
///
/// <para><b>Why this is a legitimate boundary read and not a routing mistake.</b> The rule's first
/// question is whether the value should have been deserialized closer to where its type IS
/// registered. Here it cannot be: the shape that fails carries <b>no discriminator at all</b>, so no
/// registry can type it — a <c>Markdown</c> node whose content was written as bare JSON is still a
/// <see cref="JsonElement"/> when read from its OWN per-node hub, the very hub that declares
/// <c>WithContentType&lt;MarkdownContent&gt;()</c>. Moving the read would change nothing.</para>
/// </summary>
public static class MarkdownBody
{
    /// <summary>
    /// The node's markdown body, or <c>null</c> when it carries none that can be read.
    ///
    /// <para><c>null</c> rather than <c>""</c> so a caller can tell "this node is not
    /// markdown-bodied" (the normal case when walking a subtree of typed content nodes) from "this
    /// node has an empty body", and decide for itself whether the miss is worth a diagnostic —
    /// which is what <c>ExportSource.MarkdownOf</c> does for the one shape that is.</para>
    /// </summary>
    /// <param name="node">The node to read. A null node, or one with no content, yields <c>null</c>.</param>
    /// <param name="options">The reading hub's <c>JsonSerializerOptions</c> — the registry behind
    /// them is what resolves a <c>$type</c>.</param>
    /// <param name="logger">Optional. Pass one when the caller reads a SINGLE known node (a view of
    /// its own node): an unreadable body is then diagnosed instead of silently blank. Leave it null
    /// when walking many nodes of mixed types — <see cref="ObjectAsExtensions.As{T}"/> logs an error
    /// per typed-but-foreign value, which at subtree size is a storm rather than a diagnosis.</param>
    public static string? Of(MeshNode? node, JsonSerializerOptions options, ILogger? logger = null)
    {
        if (node?.Content is null)
            return null;

        // The accessor FIRST: it covers the already-typed value, the degraded JsonElement/JsonNode,
        // and a same-short-named MarkdownContent from another build.
        if (node.ContentAs<MarkdownContent>(options, logger)?.Content is { } markdown)
            return markdown;

        // A body stored as a bare string, either still typed or degraded to a JSON string.
        if (node.Content is string s)
            return s;
        if (node.Content is JsonElement { ValueKind: JsonValueKind.String } je)
            return je.GetString();
        if (node.Content is JsonValue jv && jv.TryGetValue<string>(out var raw))
            return raw;

        return null;
    }
}
