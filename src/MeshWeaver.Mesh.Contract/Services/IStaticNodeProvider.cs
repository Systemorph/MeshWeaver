namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Provides static MeshNodes that should appear in query results
/// regardless of scope (e.g., built-in roles).
/// Implementations are registered as singletons via DI.
/// </summary>
public interface IStaticNodeProvider
{
    /// <summary>
    /// Returns the static nodes to merge into query results.
    /// </summary>
    IEnumerable<MeshNode> GetStaticNodes();
}
