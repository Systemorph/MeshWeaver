using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Null-object <see cref="IStorageAdapter"/> for in-memory partitions.
/// All reads return null/empty; writes/deletes are no-ops. The actual
/// node storage lives in the <see cref="InMemoryPersistenceService"/>
/// cache that wraps this adapter — the adapter exists only because
/// <see cref="RoutingPersistenceServiceCore"/> wraps every writable
/// partition in <c>new InMemoryPersistenceService(adapter, changeNotifier)</c>.
/// </summary>
internal sealed class InMemoryStorageAdapter : IStorageAdapter
{
    public Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        => Task.FromResult<MeshNode?>(null);

    public Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DeleteAsync(string path, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath, CancellationToken ct = default)
        => Task.FromResult<(IEnumerable<string>, IEnumerable<string>)>(([], []));

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(false);

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath, string? subPath, JsonSerializerOptions options, CancellationToken ct = default)
        => System.Linq.AsyncEnumerable.Empty<object>();

    public Task SavePartitionObjectsAsync(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath, string? subPath = null, CancellationToken ct = default)
        => Task.FromResult<DateTimeOffset?>(null);
}
