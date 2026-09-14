using Markdig;
using Markdig.Syntax;
using MeshWeaver.Kernel;

namespace MeshWeaver.Markdown;

/// <summary>
/// Represents a parsed markdown document with pre-rendered HTML and extracted code submissions.
/// When persisted to the file system, only the Content property is written (as .md file).
/// When stored in-memory or Cosmos, the full document including PrerenderedHtml and CodeSubmissions is preserved.
/// </summary>
public record MarkdownContent
{
    /// <summary>
    /// The raw markdown content (without YAML front matter).
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Pre-rendered HTML from the markdown content.
    /// This is populated when reading from the file system and preserved in memory/Cosmos.
    /// </summary>
    public string? PrerenderedHtml { get; init; }

    /// <summary>
    /// Extracted executable code submissions from code blocks with --render or --execute flags.
    /// </summary>
    public IReadOnlyList<SubmitCodeRequest>? CodeSubmissions { get; init; }

    /// <summary>
    /// List of author names.
    /// </summary>
    public IReadOnlyList<string>? Authors { get; init; }

    /// <summary>
    /// List of tags/categories.
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Thumbnail image path or URL.
    /// </summary>
    public string? Thumbnail { get; init; }

    /// <summary>
    /// Abstract/summary text extracted from YAML front matter.
    /// </summary>
    public string? Abstract { get; init; }

    /// <summary>
    /// 🚨 <b>The front-matter keys the FALLBACK markdown parser could not bind</b> — the
    /// IMPORT-side twin of <see cref="UnknownMembers"/>, and the record that makes a lossy
    /// import visible instead of silent (Systemorph/MeshWeaver#4319).
    ///
    /// <para>A <c>.md</c> file that declares a <c>nodeType</c> whose own parser is not registered
    /// on the importing host falls through to <c>MarkdownFileParser</c>, which accepts EVERY
    /// <c>.md</c> file. The node then carries the right <c>NodeType</c>, the right name and the
    /// instructions body — and <c>MarkdownContent</c> where its typed configuration should be, with
    /// every key that type declared discarded. Nothing throws, nothing logs, and the node looks
    /// complete in a listing: <c>Crm/Agent/crm-assistant</c> served for twelve days as an agent
    /// with no description, no plugins and no context match, because
    /// <c>displayName</c>/<c>exposedInNavigator</c>/<c>contextMatchPattern</c>/<c>plugins</c> went
    /// nowhere.</para>
    ///
    /// <para>🚨 This does NOT restore the values, and it must not be read as data. It names the
    /// keys that were dropped, so the degradation is a fact ON the node — visible in <c>get</c>,
    /// reachable from a query — rather than something a human notices months later. The repair is
    /// to import again on a host that registers the type's parser; the record then disappears
    /// because the fallback no longer wins.</para>
    ///
    /// <para>Null whenever nothing was discarded, which is every ordinary markdown page — so an
    /// export's bytes and every existing node are untouched. Set only for a file that DECLARED a
    /// <c>nodeType</c>: an untyped page has no type-specific configuration to lose, and its stray
    /// keys are the author's own.</para>
    ///
    /// <para>🚨 <c>[Browsable(false)]</c> for the same reason as <see cref="UnknownMembers"/> below,
    /// and it is load-bearing here: <c>MeshNodeContentEditorControl.FromType</c> puts EVERY property
    /// of a content record on the form unless it carries that attribute. Without it a viewer could
    /// edit — or clear — the record of what was lost while the settings themselves stay lost, which
    /// is the one way a diagnosis becomes worse than no diagnosis.</para>
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    public IReadOnlyList<string>? UnboundFrontMatter { get; init; }

    /// <summary>
    /// 🚨 Round-trip buffer for content members this compiled shape does not declare
    /// (schema evolution: written by a newer build, or removed since the JSON was
    /// persisted). <c>[JsonExtensionData]</c> captures them on read and re-emits them on
    /// write, and rides record <c>with</c>-copies — so neither the persistence echo nor
    /// an edit through a narrower shape can silently drop them (the content-narrowing
    /// silent-data-loss class; see <c>NodeTypeDefinition.UnknownMembers</c>). Never read
    /// programmatically. <c>[Browsable(false)]</c> keeps it out of reflected editors.
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, System.Text.Json.JsonElement>? UnknownMembers { get; init; }

    /// <summary>
    /// Creates a MarkdownContent from raw content by parsing and rendering.
    /// </summary>
    /// <param name="content">The raw markdown content (without YAML front matter).</param>
    /// <param name="hubPath">Optional hub path for the markdown pipeline configuration.</param>
    /// <param name="currentNodePath">Optional current node path used to resolve relative references in the content.</param>
    /// <returns>A fully populated MarkdownContent.</returns>
    public static MarkdownContent Parse(string content, string? hubPath = null, string? currentNodePath = null)
    {
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(hubPath ?? "", currentNodePath);
        var document = Markdig.Markdown.Parse(content, pipeline);

        // Extract executable code blocks
        var executableBlocks = document.Descendants<ExecutableCodeBlock>().ToList();
        var codeSubmissions = new List<SubmitCodeRequest>();

        foreach (var block in executableBlocks)
        {
            block.Initialize();
            var submitCode = block.GetSubmitCodeRequest();
            if (submitCode != null)
            {
                codeSubmissions.Add(submitCode);
            }
        }

        // Render to HTML
        var html = document.ToHtml(pipeline);

        return new MarkdownContent
        {
            Content = content,
            PrerenderedHtml = html,
            CodeSubmissions = codeSubmissions.Count > 0 ? codeSubmissions : null
        };
    }
}
