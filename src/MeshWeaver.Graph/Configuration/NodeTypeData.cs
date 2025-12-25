using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Complete data representation for a NodeType, combining definition and code.
/// This is the data type returned by the "type" UnifiedPath resolver.
/// Unlike MeshWeaver.Mesh.NodeTypeConfiguration (the compiled runtime form),
/// this represents the data-at-rest that can be queried and modified.
/// </summary>
public record NodeTypeData
{
    /// <summary>
    /// The node type identifier (e.g., "story", "project").
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// The NodeTypeDefinition containing metadata and hub configuration string.
    /// </summary>
    public NodeTypeDefinition? Definition { get; init; }

    /// <summary>
    /// The CodeConfiguration containing C# source code.
    /// </summary>
    public CodeConfiguration? Code { get; init; }

    /// <summary>
    /// The full path to the NodeType node (e.g., "type/Person").
    /// </summary>
    public string? Path { get; init; }
}
