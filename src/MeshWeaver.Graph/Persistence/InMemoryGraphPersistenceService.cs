using System.Collections.Concurrent;

namespace MeshWeaver.Graph.Persistence;

/// <summary>
/// In-memory implementation of IGraphPersistenceService.
/// Optionally loads initial data from an IGraphStorageProvider.
/// </summary>
public class InMemoryGraphPersistenceService : IGraphPersistenceService
{
    private readonly IGraphStorageProvider? _storageProvider;
    private readonly ConcurrentDictionary<string, Organization> _organizations = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Vertex>> _vertices = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, VertexComment>> _comments = new();
    private bool _initialized;

    public InMemoryGraphPersistenceService(IGraphStorageProvider? storageProvider = null)
    {
        _storageProvider = storageProvider;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        if (_storageProvider != null)
        {
            var organizations = await _storageProvider.LoadOrganizationsAsync(ct);
            foreach (var org in organizations)
            {
                _organizations[org.Name] = org;
            }

            var vertices = await _storageProvider.LoadVerticesAsync(ct);
            foreach (var vertex in vertices)
            {
                var key = GetVertexKey(vertex.Organization, vertex.Namespace, vertex.Type);
                var typeDict = _vertices.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, Vertex>());
                typeDict[vertex.Id] = vertex;
            }

            var comments = await _storageProvider.LoadCommentsAsync(ct);
            foreach (var comment in comments)
            {
                var vertexComments = _comments.GetOrAdd(comment.VertexId, _ => new ConcurrentDictionary<Guid, VertexComment>());
                vertexComments[comment.Id] = comment;
            }
        }

        _initialized = true;
    }

    // Organization operations

    public Task<IEnumerable<Organization>> GetOrganizationsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IEnumerable<Organization>>(_organizations.Values.ToList());
    }

    public Task<Organization?> GetOrganizationAsync(string name, CancellationToken ct = default)
    {
        _organizations.TryGetValue(name, out var org);
        return Task.FromResult(org);
    }

    public async Task<Organization> SaveOrganizationAsync(Organization organization, CancellationToken ct = default)
    {
        _organizations[organization.Name] = organization;
        await PersistAsync(ct);
        return organization;
    }

    public async Task DeleteOrganizationAsync(string name, CancellationToken ct = default)
    {
        _organizations.TryRemove(name, out _);
        await PersistAsync(ct);
    }

    // Namespace operations

    public Task<IEnumerable<GraphNamespace>> GetNamespacesAsync(string organization, CancellationToken ct = default)
    {
        if (_organizations.TryGetValue(organization, out var org))
        {
            return Task.FromResult<IEnumerable<GraphNamespace>>(org.Namespaces);
        }
        return Task.FromResult<IEnumerable<GraphNamespace>>([]);
    }

    // Vertex operations

    public Task<IEnumerable<Vertex>> GetVerticesAsync(string organization, string @namespace, CancellationToken ct = default)
    {
        var prefix = $"{organization}/{@namespace}/";
        var results = _vertices
            .Where(kv => kv.Key.StartsWith(prefix))
            .SelectMany(kv => kv.Value.Values)
            .ToList();

        return Task.FromResult<IEnumerable<Vertex>>(results);
    }

    public Task<IEnumerable<Vertex>> GetVerticesAsync(string organization, string @namespace, string type, CancellationToken ct = default)
    {
        var key = GetVertexKey(organization, @namespace, type);
        if (_vertices.TryGetValue(key, out var typeDict))
        {
            return Task.FromResult<IEnumerable<Vertex>>(typeDict.Values.ToList());
        }

        return Task.FromResult<IEnumerable<Vertex>>([]);
    }

    public Task<Vertex?> GetVertexAsync(string organization, string @namespace, string type, Guid id, CancellationToken ct = default)
    {
        var key = GetVertexKey(organization, @namespace, type);
        if (_vertices.TryGetValue(key, out var typeDict) && typeDict.TryGetValue(id, out var vertex))
        {
            return Task.FromResult<Vertex?>(vertex);
        }

        return Task.FromResult<Vertex?>(null);
    }

    public Task<IEnumerable<Vertex>> SearchVerticesAsync(string organization, string @namespace, string query, CancellationToken ct = default)
    {
        var prefix = $"{organization}/{@namespace}/";
        var queryLower = query.ToLowerInvariant();

        var results = _vertices
            .Where(kv => kv.Key.StartsWith(prefix))
            .SelectMany(kv => kv.Value.Values)
            .Where(v => v.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        (v.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        return Task.FromResult<IEnumerable<Vertex>>(results);
    }

    public async Task<Vertex> CreateVertexAsync(Vertex vertex, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var newVertex = vertex with
        {
            CreatedAt = now,
            ModifiedAt = now
        };

        var key = GetVertexKey(vertex.Organization, vertex.Namespace, vertex.Type);
        var typeDict = _vertices.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, Vertex>());

        if (!typeDict.TryAdd(newVertex.Id, newVertex))
        {
            throw new InvalidOperationException($"Vertex with id {newVertex.Id} already exists.");
        }

        await PersistAsync(ct);
        return newVertex;
    }

    public async Task<Vertex> UpdateVertexAsync(Vertex vertex, CancellationToken ct = default)
    {
        var key = GetVertexKey(vertex.Organization, vertex.Namespace, vertex.Type);

        if (!_vertices.TryGetValue(key, out var typeDict))
        {
            throw new InvalidOperationException($"Vertex with id {vertex.Id} not found.");
        }

        var updatedVertex = vertex with { ModifiedAt = DateTimeOffset.UtcNow };

        if (!typeDict.TryGetValue(vertex.Id, out _))
        {
            throw new InvalidOperationException($"Vertex with id {vertex.Id} not found.");
        }

        typeDict[vertex.Id] = updatedVertex;
        await PersistAsync(ct);
        return updatedVertex;
    }

    public async Task DeleteVertexAsync(string organization, string @namespace, string type, Guid id, CancellationToken ct = default)
    {
        var key = GetVertexKey(organization, @namespace, type);

        if (_vertices.TryGetValue(key, out var typeDict))
        {
            typeDict.TryRemove(id, out _);
        }

        // Also delete all comments for this vertex
        _comments.TryRemove(id, out _);

        await PersistAsync(ct);
    }

    // Comment operations

    public Task<IEnumerable<VertexComment>> GetCommentsAsync(Guid vertexId, CancellationToken ct = default)
    {
        if (_comments.TryGetValue(vertexId, out var vertexComments))
        {
            return Task.FromResult<IEnumerable<VertexComment>>(
                vertexComments.Values.OrderBy(c => c.CreatedAt).ToList());
        }

        return Task.FromResult<IEnumerable<VertexComment>>([]);
    }

    public async Task<VertexComment> AddCommentAsync(VertexComment comment, CancellationToken ct = default)
    {
        var newComment = comment with { CreatedAt = DateTimeOffset.UtcNow };
        var vertexComments = _comments.GetOrAdd(comment.VertexId, _ => new ConcurrentDictionary<Guid, VertexComment>());

        if (!vertexComments.TryAdd(newComment.Id, newComment))
        {
            throw new InvalidOperationException($"Comment with id {newComment.Id} already exists.");
        }

        await PersistAsync(ct);
        return newComment;
    }

    public async Task<VertexComment> UpdateCommentAsync(VertexComment comment, CancellationToken ct = default)
    {
        if (!_comments.TryGetValue(comment.VertexId, out var vertexComments))
        {
            throw new InvalidOperationException($"Comment with id {comment.Id} not found.");
        }

        if (!vertexComments.ContainsKey(comment.Id))
        {
            throw new InvalidOperationException($"Comment with id {comment.Id} not found.");
        }

        var updatedComment = comment with { ModifiedAt = DateTimeOffset.UtcNow };
        vertexComments[comment.Id] = updatedComment;
        await PersistAsync(ct);
        return updatedComment;
    }

    public async Task DeleteCommentAsync(Guid commentId, CancellationToken ct = default)
    {
        foreach (var vertexComments in _comments.Values)
        {
            if (vertexComments.TryRemove(commentId, out _))
            {
                await PersistAsync(ct);
                return;
            }
        }
    }

    private static string GetVertexKey(string organization, string @namespace, string type)
        => $"{organization}/{@namespace}/{type}";

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_storageProvider != null)
        {
            var allVertices = _vertices.Values.SelectMany(d => d.Values);
            var allComments = _comments.Values.SelectMany(d => d.Values);
            await _storageProvider.SaveOrganizationsAsync(_organizations.Values, ct);
            await _storageProvider.SaveVerticesAsync(allVertices, ct);
            await _storageProvider.SaveCommentsAsync(allComments, ct);
        }
    }
}
