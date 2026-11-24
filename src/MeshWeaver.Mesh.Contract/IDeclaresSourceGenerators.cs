namespace MeshWeaver.Mesh;

/// <summary>
/// A NodeType definition that CONTRIBUTES Roslyn source generators to the compiles that pull its
/// sources (policy <c>declared-source-generators</c>). Implemented by <c>NodeTypeDefinition</c>;
/// declared here, beside <see cref="MeshNode"/>, so the toolchain (<c>MeshWeaver.Compiler</c>),
/// which does not reference the Graph contract, can read the declaration off a node set's typed
/// content in exactly the shape the runtime reads it.
/// </summary>
public interface IDeclaresSourceGenerators
{
    /// <summary>
    /// The generator source files, each a Code node path RELATIVE to the declaring NodeType.
    /// Null or empty declares none.
    /// </summary>
    IReadOnlyList<string>? Generators { get; }
}
