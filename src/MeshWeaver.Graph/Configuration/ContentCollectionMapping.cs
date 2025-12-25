namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configures a content collection mapping for a node type.
/// The collection will be configured in the hub when nodes of this type are created.
/// </summary>
public record ContentCollectionMapping
{
    /// <summary>
    /// The name of the content collection to register in the hub.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The base collection name (e.g., "graph").
    /// This corresponds to the root of the data directory or Azure container.
    /// </summary>
    public required string BaseCollection { get; init; }

    /// <summary>
    /// Sub-path under the base collection.
    /// Can include {id} placeholder which will be replaced with the node's address id.
    /// Examples: "logos", "persons/{id}", "submission/{id}"
    /// </summary>
    public required string SubPath { get; init; }
}
