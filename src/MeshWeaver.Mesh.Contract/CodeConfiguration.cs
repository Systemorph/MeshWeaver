using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a code configuration with C# source code for dynamic compilation.
/// Stored in the Code sub-partition of NodeType hubs.
/// Registered with collection name "Code" in the workspace.
/// </summary>
public record CodeConfiguration
{
    /// <summary>
    /// Unique identifier for this code file.
    /// </summary>
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The C# source code content.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// The programming language for syntax highlighting (e.g., "csharp", "json", "javascript").
    /// Defaults to "csharp".
    /// </summary>
    public string Language { get; init; } = "csharp";

    /// <summary>
    /// Optional display name for the file in UI.
    /// </summary>
    public string? DisplayName { get; init; }
}
