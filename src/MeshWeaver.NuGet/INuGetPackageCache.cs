namespace MeshWeaver.NuGet;

/// <summary>
/// Optional persistent cache for NuGet packages, sitting between the in-process resolver
/// and the local NuGet global-packages folder. On cold start the resolver hydrates the local
/// folder from the cache; on successful restore it saves back. The default implementation is
/// a no-op — the resolver then always downloads from the feed if the local folder is empty.
/// </summary>
public interface INuGetPackageCache
{
    /// <summary>
    /// If the cache has a copy of the given package version, materialize it at
    /// <paramref name="targetPackageFolder"/> and return true. Otherwise return false.
    /// The target folder is guaranteed to be absent or empty when this method is called.
    /// </summary>
    Task<bool> TryHydrateAsync(string packageId, string version, string targetPackageFolder, CancellationToken ct);

    /// <summary>
    /// Save the contents of <paramref name="sourcePackageFolder"/> into the cache under
    /// <paramref name="packageId"/>/<paramref name="version"/>.
    /// </summary>
    Task SaveAsync(string packageId, string version, string sourcePackageFolder, CancellationToken ct);
}

internal sealed class NullNuGetPackageCache : INuGetPackageCache
{
    public static readonly NullNuGetPackageCache Instance = new();
    public Task<bool> TryHydrateAsync(string packageId, string version, string targetPackageFolder, CancellationToken ct)
        => Task.FromResult(false);
    public Task SaveAsync(string packageId, string version, string sourcePackageFolder, CancellationToken ct)
        => Task.CompletedTask;
}
