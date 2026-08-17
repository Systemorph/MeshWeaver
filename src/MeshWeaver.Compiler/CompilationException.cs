// Moved verbatim from MeshNodeCompilationService.cs in MeshWeaver.Graph (#1707). The namespace
// stays MeshWeaver.Graph.Configuration and MeshWeaver.Graph type-forwards it, so modules compiled
// against earlier releases keep binding.
namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Exception thrown when compilation fails.
/// </summary>
public class CompilationException : Exception
{
    /// <summary>The mesh path of the node whose compilation failed.</summary>
    public string NodePath { get; }

    /// <summary>
    /// Initializes a new instance of the exception for a failed compilation.
    /// </summary>
    /// <param name="nodePath">The mesh path of the node whose compilation failed.</param>
    /// <param name="message">The error message describing the failure.</param>
    public CompilationException(string nodePath, string message)
        : base(message)
    {
        NodePath = nodePath;
    }

    /// <summary>
    /// Initializes a new instance of the exception for a failed compilation, wrapping an
    /// underlying cause.
    /// </summary>
    /// <param name="nodePath">The mesh path of the node whose compilation failed.</param>
    /// <param name="message">The error message describing the failure.</param>
    /// <param name="innerException">The underlying exception that caused the failure.</param>
    public CompilationException(string nodePath, string message, Exception innerException)
        : base(message, innerException)
    {
        NodePath = nodePath;
    }
}
