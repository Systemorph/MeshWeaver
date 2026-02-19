namespace MeshWeaver.Mesh;

/// <summary>
/// Marker interface for content types that are satellite/companion to a primary MeshNode.
/// Satellite nodes derive their permissions from the primary node, not their own path.
/// Examples: Comment (satellite of a document), Thread (satellite of a parent node).
/// </summary>
public interface ISatelliteContent
{
    /// <summary>
    /// Path of the primary MeshNode this satellite belongs to.
    /// Used for permission resolution and navigation context.
    /// </summary>
    string? PrimaryNodePath { get; }
}
