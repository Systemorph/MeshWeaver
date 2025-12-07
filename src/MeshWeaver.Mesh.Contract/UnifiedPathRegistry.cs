using System.Collections.Concurrent;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Thread-safe implementation of the unified path registry.
/// </summary>
public class UnifiedPathRegistry : IUnifiedPathRegistry
{
    private readonly ConcurrentDictionary<string, IUnifiedPathHandler> handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(string prefix, IUnifiedPathHandler handler)
    {
        if (string.IsNullOrEmpty(prefix))
            throw new ArgumentException("Prefix cannot be null or empty", nameof(prefix));
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        handlers[prefix] = handler;
    }

    /// <inheritdoc />
    public bool TryResolve(string path, out Address? targetAddress, out WorkspaceReference? reference)
    {
        targetAddress = null;
        reference = null;

        if (string.IsNullOrEmpty(path))
            return false;

        // Find the prefix separator (first /)
        var slashIndex = path.IndexOf('/');
        if (slashIndex <= 0)
            return false;

        var prefix = path[..slashIndex];
        var remainder = path[(slashIndex + 1)..];

        if (!handlers.TryGetValue(prefix, out var handler))
            return false;

        try
        {
            var result = handler.Parse(remainder);
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
    public IEnumerable<string> Prefixes => handlers.Keys;
}
