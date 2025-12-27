namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Represents a single code file with its content and metadata.
/// </summary>
public record CodeFile
{
    /// <summary>
    /// The source code content.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// The programming language for syntax highlighting (e.g., "csharp", "json", "javascript").
    /// Defaults to "csharp".
    /// </summary>
    public string Language { get; init; } = "csharp";

    /// <summary>
    /// Optional display name for the file in UI.
    /// If not set, the dictionary key will be used.
    /// </summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// Configuration containing C# source code for dynamic compilation.
/// All code (types, views, etc.) is compiled together as a single unit.
/// Stored as codeConfiguration.json in the node type partition folder.
/// </summary>
public record CodeConfiguration
{
    /// <summary>
    /// C# source code to compile (legacy single-file support).
    /// Can contain any valid C# code: records, classes, views, etc.
    /// All code is compiled into a single assembly.
    /// For new configurations, prefer using the Files property instead.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// Dictionary of code files, keyed by logical file name.
    /// Example keys: "Models", "Views", "Services".
    /// Takes precedence over Code property when getting combined code.
    /// </summary>
    public Dictionary<string, CodeFile>? Files { get; init; }

    /// <summary>
    /// List of NodeType paths this configuration depends on.
    /// Used for Monaco autocomplete to include types from dependencies.
    /// Example: ["type/Person", "type/Organization"]
    /// </summary>
    public List<string>? Dependencies { get; init; }

    /// <summary>
    /// Gets all code for compilation, combining Files or falling back to Code.
    /// Files take precedence if present and non-empty.
    /// </summary>
    public string GetCombinedCode()
    {
        if (Files != null && Files.Count > 0)
        {
            return string.Join("\n\n", Files.Values
                .Where(f => !string.IsNullOrWhiteSpace(f.Code))
                .Select(f => f.Code));
        }
        return Code ?? string.Empty;
    }
}
