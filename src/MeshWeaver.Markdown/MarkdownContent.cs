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
    /// Creates a MarkdownContent from raw content by parsing and rendering.
    /// </summary>
    /// <param name="content">The raw markdown content (without YAML front matter).</param>
    /// <param name="hubPath">Optional hub path for the markdown pipeline configuration.</param>
    /// <returns>A fully populated MarkdownContent.</returns>
    public static MarkdownContent Parse(string content, string? hubPath = null)
    {
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(hubPath ?? "");
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
