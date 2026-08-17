namespace MeshWeaver.Compiler;

/// <summary>
/// The Code-node naming conventions the compile toolchain keys on. Defined here — below the
/// graph — because <see cref="MeshWeaver.Graph.Configuration.CodeQueryResolver"/>'s default
/// queries and path matching are part of the compile toolchain's identity boundary;
/// <c>MeshWeaver.Graph</c>'s <c>CodeNodeType</c> aliases these same constants for the mesh-side
/// registration, so the two can never drift.
/// </summary>
public static class CodeConventions
{
    /// <summary>The NodeType value used to identify code nodes.</summary>
    public const string CodeNodeType = "Code";

    /// <summary>
    /// The sub-namespace for source code files. Code nodes live under
    /// <c>{NodeTypePath}/Source/</c> alongside (not inside) their parent NodeType.
    /// </summary>
    public const string SourceSubNamespace = "Source";

    /// <summary>
    /// The sub-namespace for test code files. Tests live under <c>{NodeTypePath}/Test/</c>
    /// alongside (not inside) their parent NodeType.
    /// </summary>
    public const string TestSubNamespace = "Test";
}
