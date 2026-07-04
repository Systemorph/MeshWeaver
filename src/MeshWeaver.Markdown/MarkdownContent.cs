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
