using MeshWeaver.AI;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Mesh;

/// <summary>
/// Extension methods for MeshNode.
/// </summary>
public static class MeshNodeExtensions
{
    /// <summary>
    /// Gets the parent path for this node.
    /// Returns null for root-level nodes.
    /// </summary>
    public static string? GetParentPath(this MeshNode node) =>
        GetParentPath(node.Path);

    /// <summary>
    /// Gets the parent path from a given path string.
    /// Returns null for root-level paths.
    /// </summary>
    public static string? GetParentPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 1 ? null : string.Join("/", segments.Take(segments.Length - 1));
    }

    /// <summary>
    /// Gets the primary node path for this node.
    /// For satellite nodes (Comment, Thread), returns the primary node's path.
    /// For regular nodes, returns the node's own path.
    /// </summary>
    public static string GetPrimaryPath(this MeshNode node)
    {
        if (node.Content is ISatelliteContent satellite && !string.IsNullOrEmpty(satellite.PrimaryNodePath))
            return satellite.PrimaryNodePath;
        return node.Path;
    }

    /// <summary>
    /// Registers all graph-related content types with the type registry for polymorphic deserialization.
    /// This is the global registry for content types — used by the import tool, persistence layer,
    /// and runtime serialization. All built-in content types must be registered here.
    /// </summary>
    public static ITypeRegistry WithGraphTypes(this ITypeRegistry typeRegistry)
    {
        typeRegistry.WithType(typeof(NodeTypeDefinition), nameof(NodeTypeDefinition));
        typeRegistry.WithType(typeof(CodeConfiguration), nameof(CodeConfiguration));
        typeRegistry.WithType(typeof(AgentConfiguration), nameof(AgentConfiguration));
        typeRegistry.WithType(typeof(AgentDelegation), nameof(AgentDelegation));
        typeRegistry.WithType(typeof(Comment), nameof(Comment));
        typeRegistry.WithType(typeof(MarkdownContent), nameof(MarkdownContent));
        typeRegistry.WithType(typeof(AI.Thread), nameof(AI.Thread));
        typeRegistry.WithType(typeof(ThreadMessage), nameof(ThreadMessage));
        typeRegistry.WithType(typeof(AccessAssignment), nameof(AccessAssignment));
        typeRegistry.WithType(typeof(RoleAssignment), nameof(RoleAssignment));
        typeRegistry.WithType(typeof(Role), nameof(Role));
        typeRegistry.WithType(typeof(AccessObject), nameof(AccessObject));
        typeRegistry.WithType(typeof(GroupMembership), nameof(GroupMembership));
        typeRegistry.WithType(typeof(MembershipEntry), nameof(MembershipEntry));
        typeRegistry.WithType(typeof(MeshNodeCardControl), nameof(MeshNodeCardControl));
        return typeRegistry;
    }
}
