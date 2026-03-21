using System.Text.RegularExpressions;

namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// Parses and manipulates annotation markers embedded in markdown content.
/// Supports both simple format: &lt;!--type:id--&gt;content&lt;!--/type:id--&gt;
/// and extended format: &lt;!--type:id:author:date--&gt;content&lt;!--/type:id--&gt;
/// The closing tag always uses just the id (no metadata).
/// </summary>
public static partial class MarkdownAnnotationParser
{
    /// <summary>
    /// Regex pattern for comment markers. Captures id (group 1) and content (group 2).
    /// Handles both simple and extended (with :author:date) opening tags.
    /// </summary>
    [GeneratedRegex(@"<!--comment:([^:-]+)(?::[^-]*)??-->(.*?)<!--/comment:\1-->", RegexOptions.Singleline)]
    private static partial Regex CommentMarkerRegex();

    /// <summary>
    /// Regex pattern for insert markers. Captures id (group 1) and content (group 2).
    /// Handles both simple and extended (with :author:date) opening tags.
    /// </summary>
    [GeneratedRegex(@"<!--insert:([^:-]+)(?::[^-]*)??-->(.*?)<!--/insert:\1-->", RegexOptions.Singleline)]
    private static partial Regex InsertMarkerRegex();

    /// <summary>
    /// Regex pattern for delete markers. Captures id (group 1) and content (group 2).
    /// Handles both simple and extended (with :author:date) opening tags.
    /// </summary>
    [GeneratedRegex(@"<!--delete:([^:-]+)(?::[^-]*)??-->(.*?)<!--/delete:\1-->", RegexOptions.Singleline)]
    private static partial Regex DeleteMarkerRegex();

    /// <summary>
    /// Regex pattern for any annotation marker.
    /// Group 1: type, Group 2: id, Group 3: content.
    /// Handles both simple and extended (with :author:date) opening tags.
    /// </summary>
    [GeneratedRegex(@"<!--(comment|insert|delete):([^:-]+)(?::[^-]*)??-->(.*?)<!--/\1:\2-->", RegexOptions.Singleline)]
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

        // The annotated text starts after the opening tag and before the closing tag.
        // Compute opening tag length from the full match: fullMatch = openTag + annotatedText + closeTag
        var closeTag = $"<!--/{annotation.Type.ToString().ToLower()}:{markerId}-->";
        var openingTagLength = annotation.FullMatch.Length - annotation.AnnotatedText.Length - closeTag.Length;
        var textStart = annotation.StartPosition + openingTagLength;
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
    /// Regex matching any individual opening or closing tag (not paired).
    /// </summary>
    [GeneratedRegex(@"<!--/?(comment|insert|delete):[^-]+-->")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"<!--(comment|insert|delete):([^-]+)-->")]
    private static partial Regex OpeningTagRegex();

    [GeneratedRegex(@"<!--/(comment|insert|delete):([^-]+)-->")]
    private static partial Regex ClosingTagRegex();

    /// <summary>
    /// Strips all markers and returns the clean content plus annotation ranges
    /// mapped to positions in the clean content.
    /// </summary>
    public static (string CleanContent, CleanAnnotationRange[] Ranges) StripMarkersWithRanges(string content)
    {
        if (string.IsNullOrEmpty(content))
            return (content ?? "", []);

        var annotations = ExtractAllAnnotations(content);
        var clean = StripAllMarkers(content);
        var ranges = new List<CleanAnnotationRange>();

        int cumulativeTagLength = 0;
        foreach (var ann in annotations)
        {
            // Total tag overhead = full match length minus the annotated text length
            // This correctly handles both simple (<!--type:id-->) and extended (<!--type:id:author:date-->) formats
            var totalTagLength = ann.FullMatch.Length - ann.AnnotatedText.Length;

            int cleanStart = ann.StartPosition - cumulativeTagLength;
            int cleanEnd = cleanStart + ann.AnnotatedText.Length;

            ranges.Add(new CleanAnnotationRange(
                ann.Type.ToString().ToLower(),
                ann.MarkerId,
                cleanStart,
                cleanEnd
            ));

            cumulativeTagLength += totalTagLength;
        }

        return (clean, ranges.ToArray());
    }

    /// <summary>
    /// Reconstructs annotated content after an edit was made to the display (clean) content.
    /// Maps the edit from clean-content positions back to annotated-content positions.
    /// </summary>
    public static string ReconstructAnnotatedContent(string annotated, string oldClean, string newClean)
    {
        if (oldClean == newClean) return annotated;
        if (string.IsNullOrEmpty(annotated)) return newClean;

        // Find common prefix
        int prefixLen = 0;
        int maxPrefix = Math.Min(oldClean.Length, newClean.Length);
        while (prefixLen < maxPrefix && oldClean[prefixLen] == newClean[prefixLen])
            prefixLen++;

        // Find common suffix (not overlapping with prefix)
        int suffixLen = 0;
        int maxSuffix = Math.Min(oldClean.Length - prefixLen, newClean.Length - prefixLen);
        while (suffixLen < maxSuffix &&
               oldClean[oldClean.Length - 1 - suffixLen] == newClean[newClean.Length - 1 - suffixLen])
            suffixLen++;

        string insertedText = newClean.Substring(prefixLen, newClean.Length - prefixLen - suffixLen);

        var map = BuildCleanToAnnotatedMap(annotated);

        // Map edit start to annotated position
        int aStart = prefixLen < map.Length ? map[prefixLen] : annotated.Length;

        // Map edit end (start of suffix in old clean) to annotated position
        int delEnd = oldClean.Length - suffixLen;
        int aEnd = delEnd > 0 && delEnd < map.Length ? map[delEnd]
                 : delEnd >= map.Length ? annotated.Length
                 : aStart;

        var result = string.Concat(annotated.AsSpan(0, aStart), insertedText, annotated.AsSpan(aEnd));

        return CleanupOrphanedTags(result);
    }

    /// <summary>
    /// Builds a map from clean (marker-stripped) character index to annotated character index.
    /// </summary>
    public static int[] BuildCleanToAnnotatedMap(string annotated)
    {
        var tags = AnyTagRegex().Matches(annotated).OrderBy(m => m.Index).ToList();
        var map = new List<int>();
        int tagIdx = 0;

        for (int i = 0; i < annotated.Length;)
        {
            if (tagIdx < tags.Count && i == tags[tagIdx].Index)
            {
                i += tags[tagIdx].Length;
                tagIdx++;
            }
            else
            {
                map.Add(i);
                i++;
            }
        }

        return map.ToArray();
    }

    /// <summary>
    /// Removes orphaned annotation tags (opening without closing or vice versa).
    /// </summary>
    private static string CleanupOrphanedTags(string content)
    {
        var openIds = new HashSet<string>(OpeningTagRegex().Matches(content).Select(m => m.Groups[2].Value));
        var closeIds = new HashSet<string>(ClosingTagRegex().Matches(content).Select(m => m.Groups[2].Value));
        var pairedIds = new HashSet<string>(openIds.Intersect(closeIds));

        var result = OpeningTagRegex().Replace(content, m => pairedIds.Contains(m.Groups[2].Value) ? m.Value : "");
        result = ClosingTagRegex().Replace(result, m => pairedIds.Contains(m.Groups[2].Value) ? m.Value : "");

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

/// <summary>
/// An annotation range mapped to positions in stripped (clean) content.
/// </summary>
public record CleanAnnotationRange(string Type, string MarkerId, int Start, int End);
