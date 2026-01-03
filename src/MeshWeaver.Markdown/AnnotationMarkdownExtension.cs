using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;

namespace MeshWeaver.Markdown;

/// <summary>
/// Markdig extension that transforms annotation markers into HTML spans.
/// Supports multiple marker formats:
/// 1. Simple: &lt;!--comment:id--&gt;text&lt;!--/comment:id--&gt;
/// 2. With metadata: &lt;!--comment:id:author:date--&gt;text&lt;!--/comment:id--&gt;
/// 3. With comment text: &lt;!--comment:id:author:date|comment text--&gt;text&lt;!--/comment:id--&gt;
/// </summary>
public class AnnotationMarkdownExtension : IMarkdownExtension
{
    // With metadata AND comment text: <!--comment:id:author:date|comment text-->highlighted<!--/comment:id-->
    // Must match first as it's most specific
    internal static readonly Regex CommentMarkerWithTextPattern = new(
        @"<!--comment:([^:]+):([^:]*):([^|]*)\|([^-]*)-->(.*?)<!--/comment:\1-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // With metadata only: <!--comment:id:author:date-->text<!--/comment:id-->
    internal static readonly Regex CommentMarkerWithMetaPattern = new(
        @"<!--comment:([^:]+):([^:]*):([^-]*)-->(.*?)<!--/comment:\1-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    internal static readonly Regex InsertMarkerWithMetaPattern = new(
        @"<!--insert:([^:]+):([^:]*):([^-]*)-->(.*?)<!--/insert:\1-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    internal static readonly Regex DeleteMarkerWithMetaPattern = new(
        @"<!--delete:([^:]+):([^:]*):([^-]*)-->(.*?)<!--/delete:\1-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Simple format: <!--comment:id-->text<!--/comment:id-->
    // ID can contain any characters except - (which ends the comment marker)
    internal static readonly Regex CommentMarkerPattern = new(
        @"<!--comment:([^-]+)-->(.*?)<!--/comment:\1-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    internal static readonly Regex InsertMarkerPattern = new(
        @"<!--insert:([^-]+)-->(.*?)<!--/insert:\1-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    internal static readonly Regex DeleteMarkerPattern = new(
        @"<!--delete:([^-]+)-->(.*?)<!--/delete:\1-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Transforms annotation markers in markdown content to HTML spans.
    /// Call this before passing content to Markdig.
    /// Supports simple format, metadata format, and full format with comment text.
    /// </summary>
    public static string TransformAnnotations(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        // Transform comments with text first (most specific)
        // Format: <!--comment:id:author:date|comment text-->highlighted<!--/comment:id-->
        var result = CommentMarkerWithTextPattern.Replace(markdown,
            m => BuildCommentSpan(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value));

        // Transform markers with metadata (no comment text)
        result = CommentMarkerWithMetaPattern.Replace(result,
            m => BuildCommentSpan(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, null, m.Groups[4].Value));

        result = InsertMarkerWithMetaPattern.Replace(result,
            m => BuildChangeSpan("track-insert", m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value));

        result = DeleteMarkerWithMetaPattern.Replace(result,
            m => BuildChangeSpan("track-delete", m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value));

        // Then transform simple markers (no metadata)
        result = CommentMarkerPattern.Replace(result,
            m => BuildCommentSpan(m.Groups[1].Value, null, null, null, m.Groups[2].Value));

        result = InsertMarkerPattern.Replace(result,
            m => BuildChangeSpan("track-insert", m.Groups[1].Value, null, null, m.Groups[2].Value));

        result = DeleteMarkerPattern.Replace(result,
            m => BuildChangeSpan("track-delete", m.Groups[1].Value, null, null, m.Groups[2].Value));

        return result;
    }

    private static string BuildCommentSpan(string id, string? author, string? date, string? commentText, string highlightedContent)
    {
        var authorAttr = !string.IsNullOrEmpty(author) ? $" data-author=\"{EscapeHtml(author)}\"" : "";
        var dateAttr = !string.IsNullOrEmpty(date) ? $" data-date=\"{EscapeHtml(date)}\"" : "";
        var commentAttr = !string.IsNullOrEmpty(commentText) ? $" data-comment-text=\"{EscapeHtml(commentText)}\"" : "";

        // Simple highlighted span - buttons will be in side panel
        return $"<span class=\"comment-highlight\" data-comment-id=\"{id}\"{authorAttr}{dateAttr}{commentAttr}>{highlightedContent}</span>";
    }

    private static string BuildChangeSpan(string cssClass, string id, string? author, string? date, string content)
    {
        var authorAttr = !string.IsNullOrEmpty(author) ? $" data-author=\"{EscapeHtml(author)}\"" : "";
        var dateAttr = !string.IsNullOrEmpty(date) ? $" data-date=\"{EscapeHtml(date)}\"" : "";

        // Simple styled span - buttons will be in side panel
        return $"<span class=\"{cssClass}\" data-change-id=\"{id}\"{authorAttr}{dateAttr}>{content}</span>";
    }

    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    /// <summary>
    /// Checks if content contains any annotation markers.
    /// </summary>
    public static bool HasAnnotations(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return false;

        return CommentMarkerPattern.IsMatch(markdown)
               || InsertMarkerPattern.IsMatch(markdown)
               || DeleteMarkerPattern.IsMatch(markdown);
    }

    /// <summary>
    /// Strips all annotation markers from content, keeping the annotated text.
    /// Useful for getting a "clean" version of the document.
    /// </summary>
    public static string StripAnnotations(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        var result = CommentMarkerPattern.Replace(markdown, "$2");
        result = InsertMarkerPattern.Replace(result, "$2");
        result = DeleteMarkerPattern.Replace(result, "$2");

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
        var result = CommentMarkerPattern.Replace(markdown, "$2");
        result = InsertMarkerPattern.Replace(result, "$2");
        result = DeleteMarkerPattern.Replace(result, ""); // Remove deleted content

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
        var result = CommentMarkerPattern.Replace(markdown, "$2");
        result = InsertMarkerPattern.Replace(result, ""); // Remove inserted content
        result = DeleteMarkerPattern.Replace(result, "$2");

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
/// Represents a parsed annotation from markdown content.
/// </summary>
public record ParsedAnnotation
{
    /// <summary>
    /// Unique identifier for the annotation.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Type of annotation: Comment, Insert, or Delete.
    /// </summary>
    public AnnotationType Type { get; init; }

    /// <summary>
    /// Author of the annotation (if provided).
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Date string of the annotation (if provided).
    /// </summary>
    public string? Date { get; init; }

    /// <summary>
    /// The highlighted/affected text in the document.
    /// </summary>
    public string HighlightedText { get; init; } = string.Empty;

    /// <summary>
    /// For comments: the comment text content.
    /// </summary>
    public string? CommentText { get; init; }

    /// <summary>
    /// Position of the annotation in the original markdown (start index).
    /// </summary>
    public int StartPosition { get; init; }
}

/// <summary>
/// Type of annotation marker.
/// </summary>
public enum AnnotationType
{
    Comment,
    Insert,
    Delete
}

public static partial class AnnotationParser
{
    /// <summary>
    /// Extracts all annotations from markdown content as structured data.
    /// </summary>
    public static List<ParsedAnnotation> ExtractAnnotations(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return new List<ParsedAnnotation>();

        var annotations = new List<ParsedAnnotation>();

        // Extract comments with text first (most specific)
        ExtractMatches(markdown, AnnotationMarkdownExtension.CommentMarkerWithTextPattern, AnnotationType.Comment, annotations, hasCommentText: true);

        // Extract comments with metadata only
        ExtractMatches(markdown, AnnotationMarkdownExtension.CommentMarkerWithMetaPattern, AnnotationType.Comment, annotations, hasCommentText: false);

        // Extract track changes with metadata
        ExtractMatches(markdown, AnnotationMarkdownExtension.InsertMarkerWithMetaPattern, AnnotationType.Insert, annotations, hasCommentText: false);
        ExtractMatches(markdown, AnnotationMarkdownExtension.DeleteMarkerWithMetaPattern, AnnotationType.Delete, annotations, hasCommentText: false);

        // Extract simple format markers
        ExtractMatches(markdown, AnnotationMarkdownExtension.CommentMarkerPattern, AnnotationType.Comment, annotations, hasCommentText: false, simpleFormat: true);
        ExtractMatches(markdown, AnnotationMarkdownExtension.InsertMarkerPattern, AnnotationType.Insert, annotations, hasCommentText: false, simpleFormat: true);
        ExtractMatches(markdown, AnnotationMarkdownExtension.DeleteMarkerPattern, AnnotationType.Delete, annotations, hasCommentText: false, simpleFormat: true);

        // Remove duplicates (same ID) keeping the first occurrence
        return annotations
            .GroupBy(a => a.Id)
            .Select(g => g.First())
            .OrderBy(a => a.StartPosition)
            .ToList();
    }

    private static void ExtractMatches(string markdown, Regex regex, AnnotationType type,
        List<ParsedAnnotation> annotations, bool hasCommentText, bool simpleFormat = false)
    {
        foreach (Match match in regex.Matches(markdown))
        {
            // Skip if we already have this ID
            var id = match.Groups[1].Value;
            if (annotations.Any(a => a.Id == id))
                continue;

            ParsedAnnotation annotation;
            if (simpleFormat)
            {
                annotation = new ParsedAnnotation
                {
                    Id = id,
                    Type = type,
                    HighlightedText = match.Groups[2].Value,
                    StartPosition = match.Index
                };
            }
            else if (hasCommentText)
            {
                annotation = new ParsedAnnotation
                {
                    Id = id,
                    Type = type,
                    Author = string.IsNullOrEmpty(match.Groups[2].Value) ? null : match.Groups[2].Value,
                    Date = string.IsNullOrEmpty(match.Groups[3].Value) ? null : match.Groups[3].Value,
                    CommentText = string.IsNullOrEmpty(match.Groups[4].Value) ? null : match.Groups[4].Value,
                    HighlightedText = match.Groups[5].Value,
                    StartPosition = match.Index
                };
            }
            else
            {
                annotation = new ParsedAnnotation
                {
                    Id = id,
                    Type = type,
                    Author = string.IsNullOrEmpty(match.Groups[2].Value) ? null : match.Groups[2].Value,
                    Date = string.IsNullOrEmpty(match.Groups[3].Value) ? null : match.Groups[3].Value,
                    HighlightedText = match.Groups[4].Value,
                    StartPosition = match.Index
                };
            }

            annotations.Add(annotation);
        }
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
