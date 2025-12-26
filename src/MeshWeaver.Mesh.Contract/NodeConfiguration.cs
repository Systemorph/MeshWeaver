using MeshWeaver.Mesh.Services;
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

    /// <summary>
    /// Validator types for node creation specific to this NodeType.
    /// These validators run in addition to any globally registered validators.
    /// </summary>
    public IReadOnlyCollection<Type> CreationValidatorTypes { get; init; } = [];

    /// <summary>
    /// Validator types for node deletion specific to this NodeType.
    /// These validators run in addition to any globally registered validators.
    /// </summary>
    public IReadOnlyCollection<Type> DeletionValidatorTypes { get; init; } = [];

    /// <summary>
    /// Validator types for node update specific to this NodeType.
    /// These validators run in addition to any globally registered validators.
    /// </summary>
    public IReadOnlyCollection<Type> UpdateValidatorTypes { get; init; } = [];

    /// <summary>
    /// Validator types for node read specific to this NodeType.
    /// These validators run in addition to any globally registered validators.
    /// </summary>
    public IReadOnlyCollection<Type> ReadValidatorTypes { get; init; } = [];

    /// <summary>
    /// Adds a creation validator type to this configuration.
    /// </summary>
    public NodeTypeConfiguration WithCreationValidator<T>() where T : INodeCreationValidator
        => this with { CreationValidatorTypes = [..CreationValidatorTypes, typeof(T)] };

    /// <summary>
    /// Adds a creation validator type to this configuration.
    /// </summary>
    public NodeTypeConfiguration WithCreationValidator(Type validatorType)
    {
        if (!typeof(INodeCreationValidator).IsAssignableFrom(validatorType))
            throw new ArgumentException($"Type {validatorType.Name} must implement INodeCreationValidator", nameof(validatorType));
        return this with { CreationValidatorTypes = [..CreationValidatorTypes, validatorType] };
    }

    /// <summary>
    /// Adds a deletion validator type to this configuration.
    /// </summary>
    public NodeTypeConfiguration WithDeletionValidator<T>() where T : INodeDeletionValidator
        => this with { DeletionValidatorTypes = [..DeletionValidatorTypes, typeof(T)] };

    /// <summary>
    /// Adds a deletion validator type to this configuration.
    /// </summary>
    public NodeTypeConfiguration WithDeletionValidator(Type validatorType)
    {
        if (!typeof(INodeDeletionValidator).IsAssignableFrom(validatorType))
            throw new ArgumentException($"Type {validatorType.Name} must implement INodeDeletionValidator", nameof(validatorType));
        return this with { DeletionValidatorTypes = [..DeletionValidatorTypes, validatorType] };
    }

    /// <summary>
    /// Adds an update validator type to this configuration.
    /// </summary>
    public NodeTypeConfiguration WithUpdateValidator<T>() where T : INodeUpdateValidator
        => this with { UpdateValidatorTypes = [..UpdateValidatorTypes, typeof(T)] };

    /// <summary>
    /// Adds an update validator type to this configuration.
    /// </summary>
    public NodeTypeConfiguration WithUpdateValidator(Type validatorType)
    {
        if (!typeof(INodeUpdateValidator).IsAssignableFrom(validatorType))
            throw new ArgumentException($"Type {validatorType.Name} must implement INodeUpdateValidator", nameof(validatorType));
        return this with { UpdateValidatorTypes = [..UpdateValidatorTypes, validatorType] };
    }

    /// <summary>
    /// Adds a read validator type to this configuration.
    /// </summary>
    public NodeTypeConfiguration WithReadValidator<T>() where T : INodeReadValidator
        => this with { ReadValidatorTypes = [..ReadValidatorTypes, typeof(T)] };

    /// <summary>
    /// Adds a read validator type to this configuration.
    /// </summary>
    public NodeTypeConfiguration WithReadValidator(Type validatorType)
    {
        if (!typeof(INodeReadValidator).IsAssignableFrom(validatorType))
            throw new ArgumentException($"Type {validatorType.Name} must implement INodeReadValidator", nameof(validatorType));
        return this with { ReadValidatorTypes = [..ReadValidatorTypes, validatorType] };
    }
}
