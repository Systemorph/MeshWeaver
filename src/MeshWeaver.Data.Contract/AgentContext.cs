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
    /// The full unified reference path (e.g., "area/pricing/MS-2024/Summary" or "pricing/MS-2024/Summary").
    /// This is the canonical context string for autocomplete and routing.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Creates an AgentContext from a full unified reference path.
    /// </summary>
    /// <param name="unifiedPath">The unified path (e.g., "area/pricing/MS-2024/Summary").</param>
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

        // Handle standard prefixes (area/, data/, content/) - skip prefix segment
        var startIndex = 0;
        if (segments.Length > 0 && IsStandardPrefix(segments[0]))
        {
            startIndex = 1;
        }

        // Need at least addressType and addressId after the prefix
        if (segments.Length < startIndex + 2)
            return new AgentContext { Context = unifiedPath };

        var addressType = segments[startIndex];
        var addressId = segments[startIndex + 1];
        var address = new Address(addressType, addressId);

        // Extract layout area if present
        LayoutAreaReference? layoutArea = null;
        if (segments.Length > startIndex + 2)
        {
            var areaName = segments[startIndex + 2];
            var areaId = segments.Length > startIndex + 3
                ? string.Join('/', segments.Skip(startIndex + 3))
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

    private static bool IsStandardPrefix(string segment)
        => segment.Equals("area", StringComparison.OrdinalIgnoreCase)
           || segment.Equals("data", StringComparison.OrdinalIgnoreCase)
           || segment.Equals("content", StringComparison.OrdinalIgnoreCase);
}
