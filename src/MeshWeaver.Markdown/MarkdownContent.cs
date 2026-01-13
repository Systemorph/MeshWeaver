using Markdig;
using Markdig.Syntax;
using MeshWeaver.Kernel;

namespace MeshWeaver.Markdown;

/// <summary>
/// Represents a parsed markdown document with pre-rendered HTML and extracted code submissions.
/// When persisted to the file system, only the Content property is written (as .md file).
/// When stored in-memory or Cosmos, the full document including PrerenderedHtml and CodeSubmissions is preserved.
/// Also includes legacy Article properties for backwards compatibility.
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

    #region Legacy Article properties

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
    /// URL to an associated video.
    /// </summary>
    public string? VideoUrl { get; init; }

    /// <summary>
    /// Duration of the associated video.
    /// </summary>
    public TimeSpan? VideoDuration { get; init; }

    /// <summary>
    /// Title of the associated video.
    /// </summary>
    public string? VideoTitle { get; init; }

    /// <summary>
    /// Description of the associated video.
    /// </summary>
    public string? VideoDescription { get; init; }

    /// <summary>
    /// Tag line for the associated video.
    /// </summary>
    public string? VideoTagLine { get; init; }

    /// <summary>
    /// Path or URL to the video transcript.
    /// </summary>
    public string? VideoTranscript { get; init; }

    #endregion

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
