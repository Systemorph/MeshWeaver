using Markdig;
using Markdig.Syntax;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Kernel;

namespace MeshWeaver.Markdown;

/// <summary>
/// Represents a parsed markdown document with pre-rendered HTML and extracted code submissions.
/// When persisted to the file system, only the Content property is written (as .md file).
/// When stored in-memory or Cosmos, the full document including PrerenderedHtml and CodeSubmissions is preserved.
/// </summary>
public record MarkdownContent : IJsonOnDeserialized
{
    // 🚨 Null ONLY while a JSON read has not met a `content` member — the one fact
    // OnDeserialized needs to tell "the payload carries no body" from "the body is the empty
    // string". Every reader sees Content, which is never null.
    private string? content;

    /// <summary>
    /// The raw markdown content (without YAML front matter). Empty when the document has no body.
    ///
    /// <para>🚨 <b>Not <c>required</c>, and absent means EMPTY — but only when nothing else could
    /// be the body</b> (Systemorph/MeshWeaver#5736, #6639). A payload that omits <c>content</c> and
    /// carries nothing but members this record declares (<c>{"$type":"MarkdownContent"}</c>,
    /// <c>{"tags":[…]}</c>) is a document with no body yet, and it binds as one. When it was
    /// <c>required</c> that payload THREW: the read seam degraded the node to raw JSON (the
    /// <c>ObjectPolymorphicConverter</c> "could not be deserialized; preserving raw JSON" line),
    /// the overview called it Unreadable, and every update of the node logged
    /// <c>As&lt;MarkdownContent&gt; … could not recover value: JsonException</c> at Error.</para>
    ///
    /// <para>🚨 A payload that omits <c>content</c> but carries a member this record does NOT
    /// declare (<c>{"markdown": …}</c>, <c>{"body": …}</c>, <c>{"text": …}</c>) still refuses to
    /// bind — see <see cref="IJsonOnDeserialized.OnDeserialized"/>. Its text is in the store under another key, and an
    /// empty document over it would render the "No content yet" invitation whose first save
    /// overwrites that text (#4600). Every writer of this record emits <c>content</c> (the getter
    /// never returns null), so an absent member is never this record's own output.</para>
    /// </summary>
    public string Content
    {
        get => content ?? string.Empty;
        init => content = value ?? string.Empty;
    }

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
    /// Completes a JSON read: an absent <c>content</c> member becomes the empty body, unless the
    /// payload carries members this record does not declare — then the body may be under one of
    /// them, and binding an empty document over it would hide it (#4600). That case throws a
    /// <see cref="JsonException"/> naming the stray members (never their values), which every read
    /// seam already handles as "stored JSON does not bind": the converter preserves the raw JSON,
    /// <c>ReadMarkdownContent</c> reports it Unreadable, and the update pipeline reports it.
    /// </summary>
    /// <exception cref="JsonException">The payload has no <c>content</c> and carries undeclared members.</exception>
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (content is not null)
            return;
        // `$`-prefixed keys ($type, $id, …) are serializer metadata, never a body. Under options
        // without the polymorphic resolver they land in the extension buffer.
        var stray = UnknownMembers?.Keys
            .Where(k => !k.StartsWith('$'))
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (stray.Length > 0)
            throw new JsonException(
                $"{nameof(MarkdownContent)} has no 'content' member but carries "
                + $"'{string.Join("', '", stray)}', which it does not "
                + "declare — the body may be stored under one of them, so it is not bound as an empty "
                + "document (Systemorph/MeshWeaver#4600, #5736).");
        content = string.Empty;
    }

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
