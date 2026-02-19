using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Represents the current navigation context containing resolved path information.
/// Automatically updated by INavigationService when the location changes.
/// </summary>
public record NavigationContext
{
    /// <summary>
    /// The current relative path from navigation.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// The resolved address resolution containing prefix and remainder.
    /// </summary>
    public required AddressResolution Resolution { get; init; }

    /// <summary>
    /// The mesh node for the resolved path, if available.
    /// </summary>
    public MeshNode? Node { get; init; }

    /// <summary>
    /// The area parsed from the remainder (first segment after the resolved address).
    /// </summary>
    public string? Area { get; init; }

    /// <summary>
    /// The id parsed from the remainder (everything after the area).
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// The resolved address from the resolution prefix.
    /// </summary>
    public Address Address => (Address)Resolution.Prefix;

    /// <summary>
    /// The namespace string representation of the address.
    /// Used as the default namespace for queries when none is specified.
    /// </summary>
    public string Namespace => Address.ToString();

    /// <summary>
    /// The primary node's path. For satellite nodes (Comment, Thread),
    /// this is the main node's path. For regular nodes, same as Namespace.
    /// Used for permission resolution and menu context.
    /// </summary>
    public string PrimaryPath =>
        Node?.Content is ISatelliteContent satellite && !string.IsNullOrEmpty(satellite.PrimaryNodePath)
            ? satellite.PrimaryNodePath
            : Namespace;

    /// <summary>
    /// Whether the current node is a satellite (exists in conjunction with a primary node).
    /// </summary>
    public bool IsSatellite =>
        Node?.Content is ISatelliteContent satellite
        && !string.IsNullOrEmpty(satellite.PrimaryNodePath);
}
