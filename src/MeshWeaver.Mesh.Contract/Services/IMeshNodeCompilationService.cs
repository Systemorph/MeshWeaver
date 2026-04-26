namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Result from compiling a MeshNode assembly.
/// </summary>
public record NodeCompilationResult(
    string? AssemblyLocation,
    IReadOnlyList<NodeTypeConfiguration> NodeTypeConfigurations);

/// <summary>
/// Service for on-demand compilation of dynamic MeshNode assemblies.
/// Compiles C# type definitions from DataModel and caches the resulting assemblies.
/// Implemented in MeshWeaver.Graph, consumed optionally by MeshWeaver.Hosting.Orleans.
///
/// <para>
/// 100% reactive — every method returns <see cref="IObservable{T}"/>. Compose with
/// <c>.Select</c> / <c>.SelectMany</c> / <c>.Subscribe</c>. NEVER bridge to <c>Task</c>
/// or <c>await</c> from hub-reachable code — that deadlocks the hub action block. See
/// <c>Doc/Architecture/AsynchronousCalls.md</c>. Tests bridge at their own edge with
/// <c>.FirstAsync().ToTask(ct)</c>.
/// </para>
/// </summary>
public interface IMeshNodeCompilationService
{
    /// <summary>
    /// Reactive: emits the assembly location (DLL path) for the node, or null if the
    /// node does not have a NodeType definition.
    /// </summary>
    IObservable<string?> GetAssemblyLocation(MeshNode node);

    /// <summary>
    /// Reactive: emits the full compilation result (assembly + extracted NodeType
    /// configurations) for the node.
    /// </summary>
    IObservable<NodeCompilationResult?> CompileAndGetConfigurations(MeshNode node);
}
