using System.Collections.Frozen;

namespace MeshWeaver.Data;

/// <summary>
/// Resolves Unified Content Reference (UCR) prefixes to their corresponding special areas.
/// UCR prefixes (content:, data:, schema:, model:) are mapped to special areas ($Content, $Data, $Schema, $Model).
/// </summary>
public static class UcrPrefixResolver
{
    /// <summary>
    /// UCR prefix to special area mappings.
    /// Maps prefixes like "content" to special areas like "$Content".
    /// </summary>
    public static readonly FrozenDictionary<string, string> PrefixToAreaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "content", "$Content" },
        { "data", "$Data" },
        { "schema", "$Schema" },
        { "model", "$Model" },
        { "metadata", "$Metadata" }
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tries to parse a UCR prefix from a path and returns the corresponding area and remaining path.
    /// </summary>
    /// <param name="path">The input path (e.g., "content:logo.svg" or "data:")</param>
    /// <param name="area">The resolved special area (e.g., "$Content")</param>
    /// <param name="remainingPath">The path after the prefix (e.g., "logo.svg"), or null if empty</param>
    /// <returns>True if a UCR prefix was found and resolved, false otherwise</returns>
    public static bool TryResolve(string? path, out string? area, out string? remainingPath)
    {
        area = null;
        remainingPath = null;

        if (string.IsNullOrEmpty(path))
            return false;

        var colonIndex = path.IndexOf(':');
        if (colonIndex <= 0)
            return false;

        var potentialPrefix = path[..colonIndex];
        if (!PrefixToAreaMap.TryGetValue(potentialPrefix, out var specialArea))
            return false;

        area = specialArea;
        var pathAfterPrefix = path[(colonIndex + 1)..];
        remainingPath = string.IsNullOrEmpty(pathAfterPrefix) ? null : pathAfterPrefix;
        return true;
    }

    /// <summary>
    /// Resolves a UCR prefix path to a LayoutAreaReference.
    /// </summary>
    /// <param name="path">The input path (e.g., "content:logo.svg")</param>
    /// <returns>A LayoutAreaReference if the path contains a UCR prefix, null otherwise</returns>
    public static LayoutAreaReference? ResolveToLayoutAreaReference(string? path)
    {
        if (!TryResolve(path, out var area, out var remainingPath))
            return null;

        return new LayoutAreaReference(area) { Id = remainingPath };
    }
}
