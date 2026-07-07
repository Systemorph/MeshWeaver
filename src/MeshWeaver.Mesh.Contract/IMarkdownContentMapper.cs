namespace MeshWeaver.Mesh;

/// <summary>
/// Maps a plugin-owned MeshNode content type to and from the canonical Markdown file form
/// (a <c>.md</c> body plus YAML front matter), so a NodeType defined OUTSIDE core hosting
/// still round-trips through git export/import without core referencing the plugin's content
/// type. Core's <c>MarkdownFileParser</c> holds the registered mappers and delegates the
/// type-specific construction/projection to them; the owning plugin registers its mapper in
/// DI (for example the Slides plugin registers a Slide mapper from its <c>AddSlides()</c>
/// extension). When no mapper handles a file's NodeType, the parser keeps its generic
/// <c>MarkdownContent</c> behaviour.
/// </summary>
public interface IMarkdownContentMapper
{
    /// <summary>
    /// True when this mapper owns nodes of the given <paramref name="nodeType"/> (for example
    /// the Slides plugin's mapper handles <c>"Slide"</c>).
    /// </summary>
    /// <param name="nodeType">The MeshNode <see cref="MeshNode.NodeType"/> being parsed/serialized.</param>
    /// <returns><c>true</c> when this mapper handles the node type; otherwise <c>false</c>.</returns>
    bool Handles(string nodeType);

    /// <summary>
    /// On import: builds the node's typed Content object from the parsed markdown body and the
    /// generic presenter front-matter fields. Only called when <see cref="Handles"/> returned
    /// true for the file's NodeType, so a re-import reconstructs the typed content instead of
    /// downgrading it to <c>MarkdownContent</c>.
    /// </summary>
    /// <param name="markdownBody">The markdown body below the YAML front matter.</param>
    /// <param name="notes">The optional <c>Notes:</c> front-matter value (presenter/speaker notes).</param>
    /// <param name="background">The optional <c>Background:</c> front-matter value (stage background).</param>
    /// <returns>The typed content object to store on the node.</returns>
    object CreateContent(string markdownBody, string? notes, string? background);

    /// <summary>
    /// On export: projects a node's Content back to the markdown file form. Returns the markdown
    /// body plus the presenter fields to emit as front matter, or <c>null</c> when this mapper
    /// does not recognise the node's content shape (the parser then falls back to its generic
    /// handling). The mapper is expected to handle both the typed content and the untyped
    /// <see cref="System.Text.Json.JsonElement"/> shape that arises after a JSON round-trip.
    /// </summary>
    /// <param name="node">The node being serialized.</param>
    /// <returns>The projection, or <c>null</c> when unhandled.</returns>
    MarkdownContentProjection? Project(MeshNode node);
}

/// <summary>
/// The markdown-file projection of a plugin-owned node content: the markdown body plus the
/// optional presenter front-matter fields (speaker notes, stage background).
/// </summary>
/// <param name="Body">The markdown body to write beneath the front matter (may be null).</param>
/// <param name="Notes">Optional presenter/speaker notes emitted under the <c>Notes:</c> key.</param>
/// <param name="Background">Optional stage background emitted under the <c>Background:</c> key.</param>
public sealed record MarkdownContentProjection(string? Body, string? Notes, string? Background);
