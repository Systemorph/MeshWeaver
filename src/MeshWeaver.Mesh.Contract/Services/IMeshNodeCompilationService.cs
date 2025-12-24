namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Service for on-demand compilation of dynamic MeshNode assemblies.
/// Compiles C# type definitions from DataModel and caches the resulting assemblies.
/// Implemented in MeshWeaver.Graph, consumed optionally by MeshWeaver.Hosting.Orleans.
/// </summary>
public interface IMeshNodeCompilationService
{
    /// <summary>
    /// Ensures the assembly for a node is compiled and returns its location.
    /// Uses cache if valid, otherwise compiles and caches.
    /// </summary>
    /// <param name="node">The MeshNode to ensure assembly for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assembly location (DLL path), or null if the node doesn't have a DataModel.</returns>
    Task<string?> GetAssemblyLocationAsync(MeshNode node, CancellationToken ct = default);
}
