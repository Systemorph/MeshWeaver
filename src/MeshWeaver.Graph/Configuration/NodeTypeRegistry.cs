using System.Collections.Concurrent;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Static registry for built-in MeshNodes by path.
/// Nodes registered here take precedence over persistence-based nodes.
/// </summary>
public static class NodeTypeRegistry
{
    private static readonly ConcurrentDictionary<string, MeshNode> Nodes = new();

    /// <summary>
    /// Registers a MeshNode by its Path.
    /// </summary>
    public static void Register(MeshNode node)
        => Nodes[node.Path] = node;

    /// <summary>
    /// Tries to get a node by path.
    /// </summary>
    public static bool TryGet(string path, out MeshNode? node)
        => Nodes.TryGetValue(path, out node);

    /// <summary>
    /// Gets all registered nodes.
    /// </summary>
    public static IEnumerable<MeshNode> GetAll() => Nodes.Values;

    /// <summary>
    /// Checks if a path is registered.
    /// </summary>
    public static bool IsRegistered(string path) => Nodes.ContainsKey(path);

    /// <summary>
    /// Clears all registrations. Primarily for testing.
    /// </summary>
    public static void Clear() => Nodes.Clear();
}
