using System.ComponentModel.DataAnnotations;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Represents a code file with C# source code for dynamic compilation.
/// Stored as codeConfiguration.json in the node type partition folder.
/// </summary>
public record CodeFile
{
    /// <summary>
    /// Unique identifier for this code file.
    /// </summary>
    [Key]
    public string Id { get; init; } = Guid.NewGuid().AsString();

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
