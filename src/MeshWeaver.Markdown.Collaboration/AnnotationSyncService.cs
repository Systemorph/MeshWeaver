namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// Service for separating annotated markdown into clean text + annotation ranges,
/// computing position shifts after edits, and reassembling markers back into text.
/// </summary>
public static class AnnotationSyncService
{
    /// <summary>
    /// Separates annotated markdown content into clean text and annotation range entities.
    /// </summary>
    /// <param name="annotatedContent">Markdown content with inline markers.</param>
    /// <returns>Clean text (markers stripped) and annotation ranges with positions in the clean text.</returns>
    public static (string CleanText, IReadOnlyList<AnnotationRange> Annotations) Separate(string annotatedContent)
    {
        if (string.IsNullOrEmpty(annotatedContent))
            return (annotatedContent ?? "", []);

        var (cleanContent, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(annotatedContent);

        var annotations = ranges.Select(r => new AnnotationRange
        {
            MarkerId = r.MarkerId,
            Type = r.Type,
            Position = r.Start,
            Length = r.End - r.Start
        }).ToList();

        return (cleanContent, annotations);
    }

    /// <summary>
    /// Computes how annotation positions shift when clean content is edited.
    /// Uses common prefix/suffix detection to identify the edit zone.
    /// </summary>
    /// <param name="oldClean">Clean content before the edit.</param>
    /// <param name="newClean">Clean content after the edit.</param>
    /// <param name="annotations">Current annotation positions.</param>
    /// <returns>Updated annotation positions after applying the shift.</returns>
    public static IReadOnlyList<AnnotationRange> ComputePositionShifts(
        string oldClean,
        string newClean,
        IReadOnlyList<AnnotationRange> annotations)
    {
        if (oldClean == newClean || annotations.Count == 0)
            return annotations;

        // Find common prefix length
        int prefixLen = 0;
        int maxPrefix = Math.Min(oldClean.Length, newClean.Length);
        while (prefixLen < maxPrefix && oldClean[prefixLen] == newClean[prefixLen])
            prefixLen++;

        // Find common suffix length (not overlapping with prefix)
        int suffixLen = 0;
        int maxSuffix = Math.Min(oldClean.Length - prefixLen, newClean.Length - prefixLen);
        while (suffixLen < maxSuffix &&
               oldClean[oldClean.Length - 1 - suffixLen] == newClean[newClean.Length - 1 - suffixLen])
            suffixLen++;

        int editEnd = oldClean.Length - suffixLen; // End of edit zone in old content
        int delta = newClean.Length - oldClean.Length;

        var shifted = new List<AnnotationRange>(annotations.Count);

        foreach (var ann in annotations)
        {
            int pos = ann.Position;
            int len = ann.Length;
            int annEnd = pos + len;

            if (annEnd <= prefixLen)
            {
                // Entirely before edit zone — no change
                shifted.Add(ann);
            }
            else if (pos >= editEnd)
            {
                // Entirely after edit zone — shift by delta
                shifted.Add(ann with { Position = pos + delta });
            }
            else
            {
                // Overlaps with edit zone — compute new start/end independently
                int newPos = pos < prefixLen ? pos : prefixLen;
                int newEnd = annEnd >= editEnd ? annEnd + delta : newClean.Length - suffixLen;
                int newLen = Math.Max(0, newEnd - newPos);
                shifted.Add(ann with { Position = newPos, Length = newLen });
            }
        }

        return shifted;
    }

    /// <summary>
    /// Reassembles clean text with annotation markers injected at the specified positions.
    /// </summary>
    /// <param name="cleanText">Clean markdown text (no markers).</param>
    /// <param name="annotations">Annotation ranges with positions in the clean text.</param>
    /// <param name="metadata">Optional metadata provider for marker format (author:date).</param>
    /// <returns>Annotated markdown with markers re-inserted.</returns>
    public static string Reassemble(
        string cleanText,
        IReadOnlyList<AnnotationRange> annotations,
        Func<AnnotationRange, string>? metadata = null)
    {
        if (annotations.Count == 0)
            return cleanText;

        // Sort by position descending so we can inject from end to start
        // without invalidating earlier positions
        var sorted = annotations
            .OrderByDescending(a => a.Position)
            .ThenByDescending(a => a.Length)
            .ToList();

        var result = cleanText;
        foreach (var ann in sorted)
        {
            int pos = Math.Max(0, Math.Min(ann.Position, result.Length));
            int len = Math.Max(0, Math.Min(ann.Length, result.Length - pos));

            string meta = metadata?.Invoke(ann) ?? "";
            string metaSuffix = string.IsNullOrEmpty(meta) ? "" : $":{meta}";

            string openTag = $"<!--{ann.Type}:{ann.MarkerId}{metaSuffix}-->";
            string closeTag = $"<!--/{ann.Type}:{ann.MarkerId}-->";

            result = $"{result[..pos]}{openTag}{result.Substring(pos, len)}{closeTag}{result[(pos + len)..]}";

        }

        return result;
    }
}

/// <summary>
/// Represents an annotation's position range in clean (marker-free) content.
/// </summary>
public record AnnotationRange
{
    /// <summary>
    /// The marker ID linking this range to a specific annotation.
    /// </summary>
    public string MarkerId { get; init; } = string.Empty;

    /// <summary>
    /// The annotation type: "comment", "insert", or "delete".
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Character offset in the clean content.
    /// </summary>
    public int Position { get; init; }

    /// <summary>
    /// Length of the annotated text range.
    /// </summary>
    public int Length { get; init; }
}
