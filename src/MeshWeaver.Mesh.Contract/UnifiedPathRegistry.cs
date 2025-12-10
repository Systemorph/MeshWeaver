using System.Collections.Concurrent;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Thread-safe implementation of the unified path registry.
/// Path format: addressType/addressId/keyword/remainingPath
/// where keyword is one of the registered handlers (data, area, content).
/// If no keyword is specified, defaults to "area".
/// </summary>
public class UnifiedPathRegistry : IUnifiedPathRegistry
{
    private readonly ConcurrentDictionary<string, IUnifiedPathHandler> handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(string keyword, IUnifiedPathHandler handler)
    {
        if (string.IsNullOrEmpty(keyword))
            throw new ArgumentException("Keyword cannot be null or empty", nameof(keyword));
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        handlers[keyword] = handler;
    }

    /// <inheritdoc />
    public bool TryResolve(string path, out Address? targetAddress, out WorkspaceReference? reference)
    {
        targetAddress = null;
        reference = null;

        if (string.IsNullOrEmpty(path))
            return false;

        // Path format: addressType/addressId[/keyword[/remainingPath]]
        var parts = path.Split('/', StringSplitOptions.None);
        if (parts.Length < 2)
            return false;

        var addressType = parts[0];
        var addressId = parts[1];

        if (string.IsNullOrEmpty(addressType) || string.IsNullOrEmpty(addressId))
            return false;

        // Determine keyword and remaining path
        string keyword;
        string remainingPath;

        if (parts.Length >= 3 && handlers.TryGetValue(parts[2], out var handler))
        {
            // Explicit keyword specified (e.g., host/1/data/Collection)
            keyword = parts[2];
            remainingPath = parts.Length > 3 ? string.Join("/", parts.Skip(3)) : "";
        }
        else
        {
            // No keyword or unrecognized keyword - default to "area"
            keyword = "area";
            remainingPath = parts.Length > 2 ? string.Join("/", parts.Skip(2)) : "";
            if (!handlers.TryGetValue(keyword, out handler))
                return false;
        }

        try
        {
            var result = handler.Parse(addressType, addressId, remainingPath);
            targetAddress = result.Address;
            reference = result.Reference;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public IEnumerable<string> Keywords => handlers.Keys;
}
