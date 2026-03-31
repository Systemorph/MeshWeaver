namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a code configuration with C# source code for dynamic compilation.
/// Stored as MeshNode.Content in the _Source sub-partition of NodeType hubs.
/// Identity (Id) and display name (Name) live on the parent MeshNode.
/// </summary>
public record CodeConfiguration
{
    /// <summary>
    /// The C# source code content.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// The programming language for syntax highlighting (e.g., "csharp", "json", "javascript").
    /// Defaults to "csharp".
    /// </summary>
    public string Language { get; init; } = "csharp";
}
