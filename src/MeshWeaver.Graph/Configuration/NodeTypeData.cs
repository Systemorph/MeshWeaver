namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Wrapper type containing NodeTypeDefinition with all CodeConfigurations.
/// This is the default data reference for NodeType hubs when subscribing
/// with an empty DataPathReference.
/// </summary>
public record NodeTypeData
{
    /// <summary>
    /// The core NodeTypeDefinition with metadata and configuration lambda.
    /// </summary>
    public required NodeTypeDefinition Definition { get; init; }

    /// <summary>
    /// All CodeConfiguration items stored in the NodeType's partition.
    /// These contain the C# source code for dynamic compilation.
    /// </summary>
    public IReadOnlyList<CodeConfiguration> CodeConfigurations { get; init; } = [];
}
