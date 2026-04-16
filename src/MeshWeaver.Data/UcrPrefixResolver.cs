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
        { "menu", "$Menu" }
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

        // Legacy format: prefix:path (e.g., "content:logo.svg")
        var colonIndex = path.IndexOf(':');
        if (colonIndex > 0)
        {
            var colonPrefix = path[..colonIndex];
            if (PrefixToAreaMap.TryGetValue(colonPrefix, out var colonArea))
            {
                area = colonArea;
                var pathAfterColon = path[(colonIndex + 1)..];
                remainingPath = string.IsNullOrEmpty(pathAfterColon) ? null : pathAfterColon;
                return true;
            }
        }

        // New format: prefix/path (e.g., "content/logo.svg")
        var slashIndex = path.IndexOf('/');
        if (slashIndex > 0)
        {
            var slashPrefix = path[..slashIndex];
            if (PrefixToAreaMap.TryGetValue(slashPrefix, out var slashArea))
            {
                area = slashArea;
                var pathAfterSlash = path[(slashIndex + 1)..];
                remainingPath = string.IsNullOrEmpty(pathAfterSlash) ? null : pathAfterSlash;
                return true;
            }
        }

        // Exact match on prefix alone (e.g., just "content" or "data")
        if (PrefixToAreaMap.TryGetValue(path, out var exactArea))
        {
            area = exactArea;
            remainingPath = null;
            return true;
        }

        return false;
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
