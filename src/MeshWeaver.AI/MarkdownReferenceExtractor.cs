using System.Text.RegularExpressions;

namespace MeshWeaver.AI;

/// <summary>
/// Represents an extracted @ reference from markdown content.
/// </summary>
/// <param name="Path">The full path of the reference</param>
/// <param name="StartIndex">The start index in the original markdown</param>
/// <param name="EndIndex">The end index in the original markdown (exclusive)</param>
/// <param name="OriginalText">The original text including the @ symbol</param>
public record ExtractedReference(
    string Path,
    int StartIndex,
    int EndIndex,
    string OriginalText);

/// <summary>
/// Service for parsing @ references from markdown content.
/// Matches the syntax patterns from LayoutAreaMarkdownParser:
/// - @path/to/node (direct path without spaces)
/// - @(path with spaces) (parentheses syntax)
/// - @"quoted path" (quoted syntax)
/// </summary>
public static class MarkdownReferenceExtractor
{
    // Pattern for direct @ references: @path/to/node (no spaces, stops at whitespace)
    private static readonly Regex DirectReferencePattern = new(
        @"@([a-zA-Z0-9_/\-.]+)",
        RegexOptions.Compiled);

    // Pattern for parentheses @ references: @(path) or @("quoted path")
    private static readonly Regex ParenthesesReferencePattern = new(
        @"@\((?:""([^""]+)""|([^)]+))\)",
        RegexOptions.Compiled);

    // Pattern for quoted @ references: @"path with spaces"
    private static readonly Regex QuotedReferencePattern = new(
        @"@""([^""]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts all @ references from markdown content.
    /// </summary>
    /// <param name="markdown">The markdown content to parse</param>
    /// <returns>List of extracted references with their positions</returns>
    public static IReadOnlyList<ExtractedReference> ExtractReferences(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return Array.Empty<ExtractedReference>();

        var references = new List<ExtractedReference>();
        var usedRanges = new List<(int Start, int End)>();

        // Extract parentheses references first (most specific): @(path) or @("path")
        foreach (Match match in ParenthesesReferencePattern.Matches(markdown))
        {
            // Group 1 is quoted path, Group 2 is unquoted path
            var path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(path))
            {
                references.Add(new ExtractedReference(
                    path.Trim(),
                    match.Index,
                    match.Index + match.Length,
                    match.Value));
                usedRanges.Add((match.Index, match.Index + match.Length));
            }
        }

        // Extract quoted references: @"path"
        foreach (Match match in QuotedReferencePattern.Matches(markdown))
        {
            // Skip if this range overlaps with already extracted references
            if (OverlapsWithExisting(usedRanges, match.Index, match.Index + match.Length))
                continue;

            var path = match.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(path))
            {
                references.Add(new ExtractedReference(
                    path.Trim(),
                    match.Index,
                    match.Index + match.Length,
                    match.Value));
                usedRanges.Add((match.Index, match.Index + match.Length));
            }
        }

        // Extract direct references last (least specific): @path/to/node
        foreach (Match match in DirectReferencePattern.Matches(markdown))
        {
            // Skip if this range overlaps with already extracted references
            if (OverlapsWithExisting(usedRanges, match.Index, match.Index + match.Length))
                continue;

            var path = match.Groups[1].Value;
            // Filter out known non-reference @ patterns (like @agent, @model commands)
            if (!string.IsNullOrWhiteSpace(path) && !IsKnownCommand(path))
            {
                references.Add(new ExtractedReference(
                    path,
                    match.Index,
                    match.Index + match.Length,
                    match.Value));
                usedRanges.Add((match.Index, match.Index + match.Length));
            }
        }

        // Sort by start index for consistent ordering
        return references.OrderBy(r => r.StartIndex).ToList();
    }

    /// <summary>
    /// Removes a specific reference from the markdown content.
    /// </summary>
    /// <param name="markdown">The original markdown content</param>
    /// <param name="reference">The reference to remove</param>
    /// <returns>The markdown with the reference removed</returns>
    public static string RemoveReference(string markdown, ExtractedReference reference)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        // Validate the reference position and original text
        if (reference.StartIndex < 0 || reference.EndIndex > markdown.Length)
            return markdown;

        // Verify the original text is still at the expected position
        var expectedLength = reference.EndIndex - reference.StartIndex;
        if (expectedLength != reference.OriginalText.Length)
            return markdown;

        var actualText = markdown.Substring(reference.StartIndex, expectedLength);
        if (actualText != reference.OriginalText)
        {
            // The markdown has changed, try to find the reference by its original text
            var index = markdown.IndexOf(reference.OriginalText, StringComparison.Ordinal);
            if (index < 0)
                return markdown;

            return RemoveTextAtIndex(markdown, index, reference.OriginalText.Length);
        }

        return RemoveTextAtIndex(markdown, reference.StartIndex, expectedLength);
    }

    /// <summary>
    /// Removes a reference by its path, searching for it in the markdown.
    /// </summary>
    /// <param name="markdown">The original markdown content</param>
    /// <param name="path">The path to remove</param>
    /// <returns>The markdown with the first matching reference removed</returns>
    public static string RemoveReferenceByPath(string markdown, string path)
    {
        var references = ExtractReferences(markdown);
        var matchingRef = references.FirstOrDefault(r =>
            r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

        if (matchingRef != null)
        {
            return RemoveReference(markdown, matchingRef);
        }

        return markdown;
    }

    /// <summary>
    /// Gets unique paths from all references.
    /// </summary>
    /// <param name="markdown">The markdown content</param>
    /// <returns>Distinct reference paths</returns>
    public static IReadOnlyList<string> GetUniquePaths(string? markdown)
    {
        return ExtractReferences(markdown)
            .Select(r => r.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool OverlapsWithExisting(List<(int Start, int End)> ranges, int start, int end)
    {
        foreach (var (rangeStart, rangeEnd) in ranges)
        {
            // Check if ranges overlap
            if (start < rangeEnd && end > rangeStart)
                return true;
        }
        return false;
    }

    private static bool IsKnownCommand(string path)
    {
        // Filter out known @ command patterns that aren't references.
        // Use case-sensitive match for "agent/" so that lowercase @agent/Name
        // (command syntax) is filtered, but @Agent/Research (path reference
        // to the Agent namespace) is preserved.
        if (path.StartsWith("agent/", StringComparison.Ordinal))
            return true;
        if (path.StartsWith("model/", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string RemoveTextAtIndex(string text, int startIndex, int length)
    {
        var result = text.Remove(startIndex, length);

        // Clean up extra whitespace that may result from removal
        // If there are multiple spaces where we removed, collapse to one
        if (startIndex > 0 && startIndex < result.Length)
        {
            var prevChar = result[startIndex - 1];
            var nextChar = result[startIndex];

            // If both surrounding chars are spaces/newlines, remove one
            if (char.IsWhiteSpace(prevChar) && char.IsWhiteSpace(nextChar))
            {
                result = result.Remove(startIndex, 1);
            }
        }

        return result.Trim();
    }
}
