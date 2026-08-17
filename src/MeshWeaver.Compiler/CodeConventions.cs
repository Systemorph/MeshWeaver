namespace MeshWeaver.Compiler;

/// <summary>
/// The Code-node naming conventions the compile toolchain keys on. Defined here — below the
/// graph — because <see cref="CodeQueryResolver"/>'s default
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

    /// <summary>
    /// The node path → assembly-name-safe token every dynamic NodeType compile is keyed by:
    /// the emitted assembly is <c>DynamicNode_{SanitizeNodeName(path)}</c> and the generated
    /// provider class is <c>{SanitizeNodeName(path)}MeshNodeProvider</c>.
    ///
    /// <para>🚨 It therefore SHAPES THE EMITTED BYTES and belongs inside the toolchain identity
    /// boundary (#1707). It lived only in <c>MeshWeaver.Graph</c>'s <c>CompilationCacheService</c>
    /// until #1763 needed it for the build-process baker; that method now delegates here, so a
    /// mesh-driven and a compiler-driven bake of the same node can never disagree about the
    /// assembly's name — a disagreement no consumer could see, since the bundle keys on the node
    /// path while the ASSEMBLY carries the name.</para>
    /// </summary>
    /// <param name="nodePath">The NodeType's mesh path.</param>
    public static string SanitizeNodeName(string nodePath)
    {
        // Replace path separators and invalid characters with underscores
        var sanitized = (nodePath ?? string.Empty)
            .Replace('/', '_')
            .Replace('\\', '_')
            .Replace(':', '_')
            .Replace('*', '_')
            .Replace('?', '_')
            .Replace('"', '_')
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace('|', '_')
            .Replace(' ', '_');

        // Remove leading/trailing underscores and collapse multiple underscores
        while (sanitized.Contains("__", StringComparison.Ordinal))
            sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);

        sanitized = sanitized.Trim('_');

        // Ensure it starts with a letter (for valid assembly names)
        if (sanitized.Length > 0 && !char.IsLetter(sanitized[0]))
            sanitized = "Node_" + sanitized;

        return sanitized;
    }
}
