using System.Text.RegularExpressions;

namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// Parses and manipulates annotation markers embedded in markdown content.
/// Markers follow the pattern: &lt;!--type:id--&gt;content&lt;!--/type:id--&gt;
/// </summary>
public static partial class MarkdownAnnotationParser
{
    /// <summary>
    /// Regex pattern for comment markers: &lt;!--comment:id--&gt;...&lt;!--/comment:id--&gt;
    /// </summary>
    [GeneratedRegex(@"<!--comment:([^-]+)-->(.*?)<!--/comment:\1-->", RegexOptions.Singleline)]
    private static partial Regex CommentMarkerRegex();

    /// <summary>
    /// Regex pattern for insert markers: &lt;!--insert:id--&gt;...&lt;!--/insert:id--&gt;
    /// </summary>
    [GeneratedRegex(@"<!--insert:([^-]+)-->(.*?)<!--/insert:\1-->", RegexOptions.Singleline)]
    private static partial Regex InsertMarkerRegex();

    /// <summary>
    /// Regex pattern for delete markers: &lt;!--delete:id--&gt;...&lt;!--/delete:id--&gt;
    /// </summary>
    [GeneratedRegex(@"<!--delete:([^-]+)-->(.*?)<!--/delete:\1-->", RegexOptions.Singleline)]
    private static partial Regex DeleteMarkerRegex();

    /// <summary>
    /// Regex pattern for any annotation marker.
    /// </summary>
    [GeneratedRegex(@"<!--(comment|insert|delete):([^-]+)-->(.*?)<!--/\1:\2-->", RegexOptions.Singleline)]
    private static partial Regex AnyMarkerRegex();

    /// <summary>
    /// Extracts all comment annotations from markdown content.
    /// </summary>
    public static IReadOnlyList<AnnotationInfo> ExtractComments(string content)
    {
        return ExtractAnnotations(content, CommentMarkerRegex(), AnnotationType.Comment);
    }

    /// <summary>
    /// Extracts all insert track change annotations from markdown content.
    /// </summary>
    public static IReadOnlyList<AnnotationInfo> ExtractInsertions(string content)
    {
        return ExtractAnnotations(content, InsertMarkerRegex(), AnnotationType.Insert);
    }

    /// <summary>
    /// Extracts all delete track change annotations from markdown content.
    /// </summary>
    public static IReadOnlyList<AnnotationInfo> ExtractDeletions(string content)
    {
        return ExtractAnnotations(content, DeleteMarkerRegex(), AnnotationType.Delete);
    }

    /// <summary>
    /// Extracts all annotations (comments and track changes) from markdown content.
    /// </summary>
    public static IReadOnlyList<AnnotationInfo> ExtractAllAnnotations(string content)
    {
        var results = new List<AnnotationInfo>();
        var regex = AnyMarkerRegex();

        foreach (Match match in regex.Matches(content))
        {
            var type = match.Groups[1].Value switch
            {
                "comment" => AnnotationType.Comment,
                "insert" => AnnotationType.Insert,
                "delete" => AnnotationType.Delete,
                _ => AnnotationType.Comment
            };

            results.Add(new AnnotationInfo
            {
                MarkerId = match.Groups[2].Value,
                Type = type,
                AnnotatedText = match.Groups[3].Value,
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length,
                FullMatch = match.Value
            });
        }

        return results.OrderBy(a => a.StartPosition).ToList();
    }

    private static IReadOnlyList<AnnotationInfo> ExtractAnnotations(
        string content,
        Regex regex,
        AnnotationType type)
    {
        var results = new List<AnnotationInfo>();

        foreach (Match match in regex.Matches(content))
        {
            results.Add(new AnnotationInfo
            {
                MarkerId = match.Groups[1].Value,
                Type = type,
                AnnotatedText = match.Groups[2].Value,
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length,
                FullMatch = match.Value
            });
        }

        return results.OrderBy(a => a.StartPosition).ToList();
    }

    /// <summary>
    /// Gets the text content position (within markers) for a given marker ID.
    /// Returns the position after the opening marker and before the closing marker.
    /// </summary>
    public static (int Start, int End)? GetAnnotatedTextRange(string content, string markerId)
    {
        var annotations = ExtractAllAnnotations(content);
        var annotation = annotations.FirstOrDefault(a => a.MarkerId == markerId);

        if (annotation == null)
            return null;

        // Calculate position of the actual text within the markers
        var openingMarkerLength = $"<!--{annotation.Type.ToString().ToLower()}:{markerId}-->".Length;
        var textStart = annotation.StartPosition + openingMarkerLength;
        var textEnd = textStart + annotation.AnnotatedText.Length;

        return (textStart, textEnd);
    }

    /// <summary>
    /// Inserts a comment marker around the specified text range.
    /// </summary>
    public static string InsertCommentMarker(string content, int start, int end, string markerId)
    {
        return InsertMarker(content, start, end, markerId, "comment");
    }

    /// <summary>
    /// Inserts an insert track change marker around the specified text range.
    /// </summary>
    public static string InsertInsertMarker(string content, int start, int end, string markerId)
    {
        return InsertMarker(content, start, end, markerId, "insert");
    }

    /// <summary>
    /// Inserts a delete track change marker around the specified text range.
    /// </summary>
    public static string InsertDeleteMarker(string content, int start, int end, string markerId)
    {
        return InsertMarker(content, start, end, markerId, "delete");
    }

    private static string InsertMarker(string content, int start, int end, string markerId, string markerType)
    {
        if (start < 0 || end > content.Length || start > end)
            throw new ArgumentOutOfRangeException(
                $"Invalid range: start={start}, end={end}, contentLength={content.Length}");

        var openingTag = $"<!--{markerType}:{markerId}-->";
        var closingTag = $"<!--/{markerType}:{markerId}-->";

        // Insert closing tag first (so positions remain valid)
        var result = content.Insert(end, closingTag);
        result = result.Insert(start, openingTag);

        return result;
    }

    /// <summary>
    /// Removes all markers for a given marker ID, keeping the annotated text.
    /// Used when resolving/accepting a comment or change.
    /// </summary>
    public static string RemoveMarkers(string content, string markerId)
    {
        var annotation = ExtractAllAnnotations(content)
            .FirstOrDefault(a => a.MarkerId == markerId);

        if (annotation == null)
            return content;

        // Replace the full match with just the annotated text
        return content.Replace(annotation.FullMatch, annotation.AnnotatedText);
    }

    /// <summary>
    /// Removes all markers for a given marker ID along with the annotated text.
    /// Used when rejecting an insertion or accepting a deletion.
    /// </summary>
    public static string RemoveMarkersAndContent(string content, string markerId)
    {
        var annotation = ExtractAllAnnotations(content)
            .FirstOrDefault(a => a.MarkerId == markerId);

        if (annotation == null)
            return content;

        // Replace the full match with nothing
        return content.Replace(annotation.FullMatch, string.Empty);
    }

    /// <summary>
    /// Strips all annotation markers from content, keeping the annotated text.
    /// Useful for rendering a "clean" version of the document.
    /// </summary>
    public static string StripAllMarkers(string content)
    {
        var result = content;
        var regex = AnyMarkerRegex();

        // Process in reverse order to maintain position validity
        var matches = regex.Matches(result).Cast<Match>().OrderByDescending(m => m.Index).ToList();

        foreach (var match in matches)
        {
            result = result.Remove(match.Index, match.Length);
            result = result.Insert(match.Index, match.Groups[3].Value);
        }

        return result;
    }

    /// <summary>
    /// Checks if content contains any annotation markers.
    /// </summary>
    public static bool HasAnnotations(string content)
    {
        return AnyMarkerRegex().IsMatch(content);
    }

    /// <summary>
    /// Counts the total number of annotations in the content.
    /// </summary>
    public static int CountAnnotations(string content)
    {
        return AnyMarkerRegex().Matches(content).Count;
    }

    /// <summary>
    /// Gets all marker IDs of a specific type from the content.
    /// </summary>
    public static IReadOnlyList<string> GetMarkerIds(string content, AnnotationType type)
    {
        var regex = type switch
        {
            AnnotationType.Comment => CommentMarkerRegex(),
            AnnotationType.Insert => InsertMarkerRegex(),
            AnnotationType.Delete => DeleteMarkerRegex(),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        return regex.Matches(content)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
    }
}

/// <summary>
/// Information about an annotation found in markdown content.
/// </summary>
public class AnnotationInfo
{
    /// <summary>
    /// The unique marker ID.
    /// </summary>
    public string MarkerId { get; init; } = string.Empty;

    /// <summary>
    /// The type of annotation.
    /// </summary>
    public AnnotationType Type { get; init; }

    /// <summary>
    /// The text that is annotated (between the markers).
    /// </summary>
    public string AnnotatedText { get; init; } = string.Empty;

    /// <summary>
    /// Start position of the full marker in the content.
    /// </summary>
    public int StartPosition { get; init; }

    /// <summary>
    /// End position of the full marker in the content.
    /// </summary>
    public int EndPosition { get; init; }

    /// <summary>
    /// The full matched string including markers.
    /// </summary>
    public string FullMatch { get; init; } = string.Empty;
}

/// <summary>
/// Types of annotations that can be embedded in markdown.
/// </summary>
public enum AnnotationType
{
    /// <summary>
    /// A comment annotation.
    /// </summary>
    Comment,

    /// <summary>
    /// An insertion track change.
    /// </summary>
    Insert,

    /// <summary>
    /// A deletion track change.
    /// </summary>
    Delete
}
