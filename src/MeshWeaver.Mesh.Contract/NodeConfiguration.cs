using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Configuration for a node type that defines how to handle nodes of a specific type.
/// Maps a NodeType string (e.g., "org", "project", "story") to:
/// - DataType: The CLR type for Content serialization/deserialization
/// - HubConfiguration: The factory for configuring the message hub
/// </summary>
public record NodeTypeConfiguration
{
    /// <summary>
    /// The node type identifier (e.g., "org", "project", "story", "pricing").
    /// This matches MeshNode.NodeType.
    /// </summary>
    public required string NodeType { get; init; }

    /// <summary>
    /// The CLR type of the content entity for this node type.
    /// Used for serialization/deserialization of MeshNode.Content.
    /// </summary>
    public required Type DataType { get; init; }

    /// <summary>
    /// Factory function to configure the message hub for this node type.
    /// </summary>
    public required Func<MessageHubConfiguration, MessageHubConfiguration> HubConfiguration { get; init; }

    /// <summary>
    /// Optional display name for the node type.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Optional icon name for UI display.
    /// </summary>
    public string? IconName { get; init; }

    /// <summary>
    /// Optional description for the node type.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Display order for sorting in autocomplete lists (lower values appear first).
    /// </summary>
    public int DisplayOrder { get; init; }
}
