using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// Represents the current application context for an agent chat session.
/// </summary>
public record AgentContext
{
    /// <summary>
    /// The target address (e.g., pricing/MS-2024).
    /// </summary>
    public Address? Address { get; init; }

    /// <summary>
    /// The current layout area reference.
    /// </summary>
    public LayoutAreaReference? LayoutArea { get; init; }

    /// <summary>
    /// The human-readable name of the MeshNode associated with this context.
    /// </summary>
    public string? MeshNodeName { get; init; }

    /// <summary>
    /// The full unified reference path (e.g., "pricing/MS-2024/Summary" or "pricing/MS-2024/data/Collection").
    /// Format: addressType/addressId[/keyword[/remainingPath]]
    /// If no keyword specified, defaults to area.
    /// This is the canonical context string for autocomplete and routing.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Standard prefixes that indicate the reference type when used as first segment.
    /// These get stripped and the remaining path is parsed as addressType/addressId/...
    /// </summary>
    private static readonly HashSet<string> StandardPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "data", "content", "area"
    };

    /// <summary>
    /// Creates an AgentContext from a full unified reference path.
    /// </summary>
    /// <param name="unifiedPath">The unified path (e.g., "pricing/MS-2024/Summary" or "area/pricing/MS-2024/Summary").</param>
    /// <returns>A new AgentContext with parsed Address and LayoutArea.</returns>
    public static AgentContext FromUnifiedPath(string unifiedPath)
    {
        if (string.IsNullOrEmpty(unifiedPath))
            return new AgentContext { Context = unifiedPath };

        var segments = unifiedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Handle reserved prefixes (agent/, model/) - no address
        if (segments.Length > 0 && IsReservedPrefix(segments[0]))
        {
            return new AgentContext { Context = unifiedPath };
        }

        // Check if first segment is a standard prefix (area/, data/, content/)
        // If so, skip it and parse the rest
        var startIndex = 0;
        string? prefixKeyword = null;
        if (segments.Length > 0 && StandardPrefixes.Contains(segments[0]))
        {
            prefixKeyword = segments[0];
            startIndex = 1;
        }

        // Need at least addressType and addressId after any prefix
        if (segments.Length < startIndex + 2)
            return new AgentContext { Context = unifiedPath };

        var addressType = segments[startIndex];
        var addressId = segments[startIndex + 1];
        var address = new Address(addressType, addressId);

        // Extract layout area from remaining segments after address
        LayoutAreaReference? layoutArea = null;
        var areaStartIndex = startIndex + 2;
        if (segments.Length > areaStartIndex)
        {
            var areaName = segments[areaStartIndex];
            var areaId = segments.Length > areaStartIndex + 1
                ? string.Join('/', segments.Skip(areaStartIndex + 1))
                : null;

            layoutArea = new LayoutAreaReference(areaName)
            {
                Id = areaId
            };
        }

        return new AgentContext
        {
            Address = address,
            LayoutArea = layoutArea,
            Context = unifiedPath
        };
    }

    /// <summary>
    /// Converts this AgentContext to a unified reference path.
    /// </summary>
    /// <returns>The unified reference path string.</returns>
    public string? ToUnifiedPath()
    {
        // If Context is already set, return it
        if (!string.IsNullOrEmpty(Context))
            return Context;

        // Build from Address and LayoutArea
        if (Address == null)
            return null;

        var path = $"{Address.Type}/{Address.Id}";

        if (LayoutArea?.Area != null)
        {
            path += $"/{LayoutArea.Area}";
            if (LayoutArea.Id != null)
            {
                path += $"/{LayoutArea.Id}";
            }
        }

        return path;
    }

    private static bool IsReservedPrefix(string segment)
        => segment.Equals("agent", StringComparison.OrdinalIgnoreCase)
           || segment.Equals("model", StringComparison.OrdinalIgnoreCase);
}
