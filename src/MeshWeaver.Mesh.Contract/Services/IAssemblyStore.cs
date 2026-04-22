using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Version-keyed store for compiled NodeType assemblies. Replaces the in-memory
/// compilation cache with a shared, durable lookup keyed by <c>(nodeTypePath, version)</c>.
///
/// The version is the NodeType MeshNode's <see cref="Mesh.MeshNode.Version"/>: every time
/// the NodeType (or its sources) change, the owning MeshNode's version bumps, the cache
/// misses, and a fresh compile runs. A hit means "this exact version's bytes have already
/// been produced and uploaded" — no coherence problem because the key names what it is,
/// not how it was built.
///
/// All members are reactive per <c>Doc/Architecture/AsynchronousCalls.md</c>: callers
/// must not <c>await</c>; compose with <c>SelectMany</c> and <c>Subscribe</c>. Implementations
/// that pull from remote storage (e.g. blob) download on first access into a process-local
/// cache and return that local path; subsequent calls for the same version are served
/// from the local cache.
/// </summary>
public interface IAssemblyStore
{
    /// <summary>
    /// Looks up a previously-compiled assembly. Emits a local filesystem path the caller
    /// can feed to <c>AssemblyLoadContext.LoadFromAssemblyPath</c> on hit, or <c>null</c>
    /// on miss. Always completes after exactly one emission.
    /// </summary>
    IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version);

    /// <summary>
    /// Persists a freshly-compiled assembly under the given key. Emits the local filesystem
    /// path where the caller can load from. Overwriting is a no-op when the bytes match
    /// (two replicas compiling the same version → same source inputs → same output), so
    /// Put is safe to call concurrently from multiple replicas on a cold miss.
    /// </summary>
    IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes);
}

/// <summary>
/// Fallback implementation used when no concrete store is registered — every
/// <see cref="TryGetAssemblyPath"/> is a miss and <see cref="Put"/> is a no-op. Keeps
/// the current "always re-compile in memory" behaviour so callers that haven't migrated
/// yet keep working unchanged.
/// </summary>
public sealed class NullAssemblyStore : IAssemblyStore
{
    public static readonly NullAssemblyStore Instance = new();

    public IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version) =>
        Observable.Return<string?>(null);

    public IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes) =>
        Observable.Return(string.Empty);
}
