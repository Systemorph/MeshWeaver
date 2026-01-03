using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;

namespace MeshWeaver.Markdown;

/// <summary>
/// Markdig extension that transforms annotation markers into HTML spans.
/// Converts:
/// - &lt;!--comment:id--&gt;text&lt;!--/comment:id--&gt; to &lt;span class="annotation-comment" data-marker-id="id"&gt;text&lt;/span&gt;
/// - &lt;!--insert:id--&gt;text&lt;!--/insert:id--&gt; to &lt;span class="annotation-insert" data-marker-id="id"&gt;text&lt;/span&gt;
/// - &lt;!--delete:id--&gt;text&lt;!--/delete:id--&gt; to &lt;span class="annotation-delete" data-marker-id="id"&gt;text&lt;/span&gt;
/// </summary>
public class AnnotationMarkdownExtension : IMarkdownExtension
{
    private static readonly Regex CommentMarkerRegex = new(
        @"<!--comment:([^-]+)-->(.*?)<!--/comment:\1-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex InsertMarkerRegex = new(
        @"<!--insert:([^-]+)-->(.*?)<!--/insert:\1-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DeleteMarkerRegex = new(
        @"<!--delete:([^-]+)-->(.*?)<!--/delete:\1-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Transforms annotation markers in markdown content to HTML spans.
    /// Call this before passing content to Markdig.
    /// </summary>
    public static string TransformAnnotations(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        // Transform in order: comments, inserts, deletes
        // Using class names that match collaborative-annotations.css
        var result = CommentMarkerRegex.Replace(markdown,
            m => $"<span class=\"comment-highlight\" data-comment-id=\"{m.Groups[1].Value}\">{m.Groups[2].Value}</span>");

        result = InsertMarkerRegex.Replace(result,
            m => $"<span class=\"track-insert\" data-change-id=\"{m.Groups[1].Value}\">{m.Groups[2].Value}</span>");

        result = DeleteMarkerRegex.Replace(result,
            m => $"<span class=\"track-delete\" data-change-id=\"{m.Groups[1].Value}\">{m.Groups[2].Value}</span>");

        return result;
    }

    /// <summary>
    /// Checks if content contains any annotation markers.
    /// </summary>
    public static bool HasAnnotations(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return false;

        return CommentMarkerRegex.IsMatch(markdown)
               || InsertMarkerRegex.IsMatch(markdown)
               || DeleteMarkerRegex.IsMatch(markdown);
    }

    /// <summary>
    /// Strips all annotation markers from content, keeping the annotated text.
    /// Useful for getting a "clean" version of the document.
    /// </summary>
    public static string StripAnnotations(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        var result = CommentMarkerRegex.Replace(markdown, "$2");
        result = InsertMarkerRegex.Replace(result, "$2");
        result = DeleteMarkerRegex.Replace(result, "$2");

        return result;
    }

    /// <summary>
    /// Strips annotations and their content for the "accepted" view.
    /// - Removes delete markers and their content (deletions are accepted)
    /// - Keeps insert text without markers (insertions are accepted)
    /// - Keeps comment text without markers (comments are resolved)
    /// </summary>
    public static string GetAcceptedContent(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        // Keep comment and insert text, remove delete text
        var result = CommentMarkerRegex.Replace(markdown, "$2");
        result = InsertMarkerRegex.Replace(result, "$2");
        result = DeleteMarkerRegex.Replace(result, ""); // Remove deleted content

        return result;
    }

    /// <summary>
    /// Strips annotations and their content for the "rejected" view.
    /// - Keeps delete markers text (deletions are rejected = text restored)
    /// - Removes insert text without markers (insertions are rejected)
    /// - Keeps comment text without markers (comments are resolved)
    /// </summary>
    public static string GetRejectedContent(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        // Keep comment and delete text, remove insert text
        var result = CommentMarkerRegex.Replace(markdown, "$2");
        result = InsertMarkerRegex.Replace(result, ""); // Remove inserted content
        result = DeleteMarkerRegex.Replace(result, "$2");

        return result;
    }

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        // No parser setup needed - we use pre-processing
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        // No renderer setup needed - we use pre-processing
    }
}

/// <summary>
/// Extension methods for annotation-aware markdown rendering.
/// </summary>
public static class AnnotationMarkdownExtensionMethods
{
    /// <summary>
    /// Converts markdown to HTML with annotation markers transformed to spans.
    /// </summary>
    public static string ToHtmlWithAnnotations(this MarkdownPipeline pipeline, string markdown)
    {
        var transformed = AnnotationMarkdownExtension.TransformAnnotations(markdown);
        return Markdig.Markdown.ToHtml(transformed, pipeline);
    }
}
