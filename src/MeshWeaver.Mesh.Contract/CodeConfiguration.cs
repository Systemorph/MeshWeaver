namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a code configuration with C# source code for dynamic compilation.
/// Stored as MeshNode.Content in the Source sub-partition of NodeType hubs.
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

    /// <summary>
    /// When <c>true</c>, the Content layout surfaces a Run button next to Edit that
    /// posts a <c>SubmitCodeRequest</c> to the kernel and streams output into a
    /// result pane below the code block. Default <c>false</c> — Code nodes that
    /// aren't marked executable stay read-only.
    /// </summary>
    public bool IsExecutable { get; init; }
}
