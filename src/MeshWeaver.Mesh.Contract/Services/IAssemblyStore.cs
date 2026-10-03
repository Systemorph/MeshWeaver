using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Cross-silo durable coordinates for a stored compiled assembly. <see cref="LocalPath"/>
/// is the process-local path the caller can hand to <c>AssemblyLoadContext.LoadFromAssemblyPath</c>
/// <em>right now</em>; <see cref="Collection"/> + <see cref="ContentPath"/> are the
/// persisted reference every other silo can use to fetch the same bytes (denormalised
/// onto <c>NodeTypeDefinition.LatestAssembly{Collection,Path}</c> and onto
/// <c>NodeTypeRelease.{AssemblyCollection,AssemblyContentPath}</c>).
/// </summary>
public sealed record AssemblyStoreLocation(string LocalPath, string Collection, string ContentPath);

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
    /// 🚨 Looks up the build a NodeType record NAMES — its content path and the MVID of the bytes
    /// it published — rather than whatever file currently answers for its version. A version key
    /// is not an identity: a prebuilt bundle adopted at the version a compile already used, a
    /// recompile that did not move the version, or a rollback all put several builds under one
    /// key, and "the newest file for the version" then re-binds the OLD build after a
    /// <c>DisposeRequest</c>. Activation resolves through this member so that the next activation
    /// binds the build the record names (maintainer, 2026-10-03: <i>"after disposerequest, new
    /// version must be loaded"</i>).
    ///
    /// <para>The default implementation answers the version-only lookup, so a store that keeps one
    /// build per key keeps its behaviour; a content-addressed store overrides it. Either way the
    /// caller still checks the bound bytes' MVID against the record before binding.</para>
    /// </summary>
    /// <param name="nodeTypePath">The NodeType path.</param>
    /// <param name="version">The store version the record names (<c>LastCompiledVersion</c>).</param>
    /// <param name="contentPath">The record's content path inside the store; null when unknown.</param>
    /// <param name="assemblyMvid">The record's published MVID; null when unknown (a legacy record).</param>
    IObservable<string?> TryGetBuildPath(string nodeTypePath, long version, string? contentPath, string? assemblyMvid)
        => TryGetAssemblyPath(nodeTypePath, version);

    /// <summary>
    /// Persists a freshly-compiled assembly under the given key. Emits the local filesystem
    /// path where the caller can load from. Overwriting is a no-op when the bytes match
    /// (two replicas compiling the same version → same source inputs → same output), so
    /// Put is safe to call concurrently from multiple replicas on a cold miss.
    /// </summary>
    IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes);

    /// <summary>
    /// Persists a freshly-compiled assembly AND returns the cross-silo durable coordinates.
    /// <see cref="AssemblyStoreLocation.LocalPath"/> is the same value as <see cref="Put"/>
    /// returns; <see cref="AssemblyStoreLocation.Collection"/> + <c>ContentPath</c> are the
    /// remote reference every other silo can hydrate from. The default implementation calls
    /// <see cref="Put"/> and synthesises empty Collection/ContentPath — concrete stores
    /// (Blob, FileSystem) should override to surface the real coordinates.
    /// </summary>
    IObservable<AssemblyStoreLocation> PutWithLocation(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
        => Put(nodeTypePath, version, assemblyBytes, pdbBytes)
            .Select(localPath => new AssemblyStoreLocation(localPath, string.Empty, string.Empty));
}

/// <summary>
/// Fallback implementation used when no concrete store is registered — every
/// <see cref="TryGetAssemblyPath"/> is a miss and <see cref="Put"/> is a no-op. Keeps
/// the current "always re-compile in memory" behaviour so callers that haven't migrated
/// yet keep working unchanged.
/// </summary>
public sealed class NullAssemblyStore : IAssemblyStore
{
    /// <summary>Shared singleton — stateless so there is no reason to instantiate.</summary>
    public static readonly NullAssemblyStore Instance = new();

    /// <inheritdoc />
    public IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version) =>
        Observable.Return<string?>(null);

    /// <inheritdoc />
    public IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes) =>
        Observable.Return(string.Empty);

    /// <inheritdoc />
    public IObservable<AssemblyStoreLocation> PutWithLocation(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes) =>
        Observable.Return(new AssemblyStoreLocation(string.Empty, string.Empty, string.Empty));
}
