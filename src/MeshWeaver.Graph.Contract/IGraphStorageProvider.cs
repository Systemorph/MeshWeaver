namespace MeshWeaver.Graph;

/// <summary>
/// Storage provider for loading and saving graph data.
/// Used by InMemoryGraphPersistenceService for initialization and persistence.
/// </summary>
public interface IGraphStorageProvider
{
    /// <summary>
    /// Loads all organizations from storage.
    /// </summary>
    Task<IEnumerable<Organization>> LoadOrganizationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves organizations to storage.
    /// </summary>
    Task SaveOrganizationsAsync(IEnumerable<Organization> organizations, CancellationToken ct = default);

    /// <summary>
    /// Loads all vertices from storage.
    /// </summary>
    Task<IEnumerable<Vertex>> LoadVerticesAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves vertices to storage.
    /// </summary>
    Task SaveVerticesAsync(IEnumerable<Vertex> vertices, CancellationToken ct = default);

    /// <summary>
    /// Loads all comments from storage.
    /// </summary>
    Task<IEnumerable<VertexComment>> LoadCommentsAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves comments to storage.
    /// </summary>
    Task SaveCommentsAsync(IEnumerable<VertexComment> comments, CancellationToken ct = default);
}
