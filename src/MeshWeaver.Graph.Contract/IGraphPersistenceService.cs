namespace MeshWeaver.Graph;

/// <summary>
/// Persistence service for graph vertices.
/// Implementations can be injected via DI (in-memory, file-based, Cosmos DB, etc.)
/// </summary>
public interface IGraphPersistenceService
{
    // Organization operations

    /// <summary>
    /// Gets all organizations.
    /// </summary>
    Task<IEnumerable<Organization>> GetOrganizationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets an organization by name.
    /// </summary>
    Task<Organization?> GetOrganizationAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates an organization.
    /// </summary>
    Task<Organization> SaveOrganizationAsync(Organization organization, CancellationToken ct = default);

    /// <summary>
    /// Deletes an organization.
    /// </summary>
    Task DeleteOrganizationAsync(string name, CancellationToken ct = default);

    // Namespace operations

    /// <summary>
    /// Gets all namespaces for an organization.
    /// </summary>
    Task<IEnumerable<GraphNamespace>> GetNamespacesAsync(string organization, CancellationToken ct = default);

    // Vertex operations

    /// <summary>
    /// Gets all vertices in an organization/namespace.
    /// </summary>
    Task<IEnumerable<Vertex>> GetVerticesAsync(string organization, string @namespace, CancellationToken ct = default);

    /// <summary>
    /// Gets all vertices of a specific type in an organization/namespace.
    /// </summary>
    Task<IEnumerable<Vertex>> GetVerticesAsync(string organization, string @namespace, string type, CancellationToken ct = default);

    /// <summary>
    /// Gets a single vertex by organization, namespace, type, and id.
    /// </summary>
    Task<Vertex?> GetVertexAsync(string organization, string @namespace, string type, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Searches vertices by name or text content.
    /// </summary>
    Task<IEnumerable<Vertex>> SearchVerticesAsync(string organization, string @namespace, string query, CancellationToken ct = default);

    /// <summary>
    /// Creates a new vertex.
    /// </summary>
    Task<Vertex> CreateVertexAsync(Vertex vertex, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing vertex.
    /// </summary>
    Task<Vertex> UpdateVertexAsync(Vertex vertex, CancellationToken ct = default);

    /// <summary>
    /// Deletes a vertex.
    /// </summary>
    Task DeleteVertexAsync(string organization, string @namespace, string type, Guid id, CancellationToken ct = default);

    // Comment operations

    /// <summary>
    /// Gets all comments for a vertex.
    /// </summary>
    Task<IEnumerable<VertexComment>> GetCommentsAsync(Guid vertexId, CancellationToken ct = default);

    /// <summary>
    /// Adds a comment to a vertex.
    /// </summary>
    Task<VertexComment> AddCommentAsync(VertexComment comment, CancellationToken ct = default);

    /// <summary>
    /// Updates a comment.
    /// </summary>
    Task<VertexComment> UpdateCommentAsync(VertexComment comment, CancellationToken ct = default);

    /// <summary>
    /// Deletes a comment.
    /// </summary>
    Task DeleteCommentAsync(Guid commentId, CancellationToken ct = default);

    // Initialization

    /// <summary>
    /// Initializes the persistence service (e.g., loads data from storage).
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);
}
