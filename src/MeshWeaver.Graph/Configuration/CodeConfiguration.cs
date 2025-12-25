namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration containing C# source code for dynamic compilation.
/// All code (types, views, etc.) is compiled together as a single unit.
/// Stored as _config/codeConfiguration.json in the node type partition folder.
/// </summary>
public record CodeConfiguration
{
    /// <summary>
    /// C# source code to compile.
    /// Can contain any valid C# code: records, classes, views, etc.
    /// All code is compiled into a single assembly.
    /// </summary>
    public string? Code { get; init; }
}
