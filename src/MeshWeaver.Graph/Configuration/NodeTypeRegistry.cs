using System.Collections.Concurrent;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Static registry for NodeType definitions that are compiled into assemblies.
/// Types registered here take precedence over persistence-based definitions.
/// </summary>
public static class NodeTypeRegistry
{
    private static readonly ConcurrentDictionary<string, NodeTypeRegistration> ById = new();
    private static readonly ConcurrentDictionary<string, NodeTypeRegistration> ByPath = new();

    /// <summary>
    /// Registers a NodeType definition statically.
    /// </summary>
    /// <param name="registration">The registration containing definition, node, and optional code configuration.</param>
    public static void Register(NodeTypeRegistration registration)
    {
        ById[registration.Definition.Id] = registration;
        ByPath[registration.Node.Path] = registration;
    }

    /// <summary>
    /// Tries to get a registration by type ID (e.g., "NodeType", "story").
    /// </summary>
    public static bool TryGetById(string typeId, out NodeTypeRegistration? registration)
        => ById.TryGetValue(typeId, out registration);

    /// <summary>
    /// Tries to get a registration by path (e.g., "type/NodeType", "type/story").
    /// </summary>
    public static bool TryGetByPath(string path, out NodeTypeRegistration? registration)
        => ByPath.TryGetValue(path, out registration);

    /// <summary>
    /// Gets all registered NodeTypes.
    /// </summary>
    public static IEnumerable<NodeTypeRegistration> GetAll() => ById.Values;

    /// <summary>
    /// Checks if a type ID is registered.
    /// </summary>
    public static bool IsRegistered(string typeId) => ById.ContainsKey(typeId);

    /// <summary>
    /// Clears all registrations. Primarily for testing.
    /// </summary>
    public static void Clear()
    {
        ById.Clear();
        ByPath.Clear();
    }
}

/// <summary>
/// A registration entry for a statically defined NodeType.
/// </summary>
public record NodeTypeRegistration
{
    /// <summary>
    /// The NodeType definition.
    /// </summary>
    public required NodeTypeDefinition Definition { get; init; }

    /// <summary>
    /// The MeshNode representing this NodeType.
    /// </summary>
    public required MeshNode Node { get; init; }

    /// <summary>
    /// Optional code file for types with compiled code.
    /// </summary>
    public CodeFile? Code { get; init; }
}
