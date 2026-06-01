using System.Collections.Concurrent;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// Mesh-scoped resolver of named <see cref="IIoPool"/> instances. Registered as a
/// singleton in <c>MeshBuilder</c>, so its lifetime IS the mesh's: when the mesh
/// is disposed every pool (and its <see cref="SemaphoreSlim"/>) dies with it. No
/// static state — the backing dictionary is an instance field.
///
/// <para>Pools are created lazily on first use, so Wave-2/3 pool names
/// (<see cref="IoPoolNames.Http"/>, etc.) cost nothing until a leaf actually
/// touches that resource class.</para>
/// </summary>
public sealed class IoPoolRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, IoPool> _pools = new(StringComparer.Ordinal);
    private readonly IoPoolOptions _options;

    public IoPoolRegistry(IoPoolOptions? options = null)
    {
        _options = options ?? new IoPoolOptions();
    }

    /// <summary>
    /// Gets (creating on first use) the bounded pool for the given resource-class
    /// name. The cap comes from <see cref="IoPoolOptions.MaxConcurrencyFor"/>.
    /// </summary>
    public IIoPool Get(string name)
        => _pools.GetOrAdd(name, n => new IoPool(_options.MaxConcurrencyFor(n)));

    public void Dispose()
    {
        foreach (var pool in _pools.Values)
            pool.Dispose();
        _pools.Clear();
    }
}
