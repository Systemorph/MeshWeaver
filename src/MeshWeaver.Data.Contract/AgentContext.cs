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
    /// The full unified reference path (e.g., "pricing/MS-2024/Summary" or "pricing/MS-2024/data/Collection").
    /// Format: addressType/addressId[/keyword[/remainingPath]]
    /// If no keyword specified, defaults to area.
    /// This is the canonical context string for autocomplete and routing.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Reserved keywords that determine reference type.
    /// </summary>
    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "data", "content", "area"
    };

    /// <summary>
    /// Creates an AgentContext from a full unified reference path.
    /// </summary>
    /// <param name="unifiedPath">The unified path (e.g., "pricing/MS-2024/Summary").</param>
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

        // New format: addressType/addressId[/keyword[/remainingPath]]
        // Need at least addressType and addressId
        if (segments.Length < 2)
            return new AgentContext { Context = unifiedPath };

        var addressType = segments[0];
        var addressId = segments[1];
        var address = new Address(addressType, addressId);

        // Check if third segment is a keyword
        string? keyword = null;
        if (segments.Length > 2 && ReservedKeywords.Contains(segments[2]))
        {
            keyword = segments[2];
        }

        // Extract layout area if this is an area reference (default or explicit)
        LayoutAreaReference? layoutArea = null;
        if (keyword == null || keyword.Equals("area", StringComparison.OrdinalIgnoreCase))
        {
            // Area reference: remaining segments are areaName/areaId
            var areaStartIndex = keyword == null ? 2 : 3;
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
