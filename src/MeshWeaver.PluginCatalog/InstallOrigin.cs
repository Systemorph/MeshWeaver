using System.Collections.Immutable;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// WHERE an install came from — the repository (and subdirectory) or the registry whose source
/// performed it — as one canonical string, and the rule that binds an unattended update to it
/// (MeshWeaver#6526).
///
/// <para>🚨 <b>The origin is the identity of the SOURCE, never a field a repository writes.</b> A
/// manifest listed straight off a repository carries whatever its <c>package.json</c> authors, so
/// an origin read from the manifest would let any repository claim to be any other. The origin is
/// therefore carried by the <see cref="IPackageSource"/> the install ran through — attached where
/// that source is BUILT from its configured repository path and subdirectory
/// (<see cref="PackageSources.FromRepo"/>), or by the registry client itself — and stamped onto the
/// install record by the one install funnel (<see cref="CatalogLayoutAreas.InstallOrUpdate"/>),
/// which overwrites whatever the manifest carried. The same reasoning as MeshWeaver#6512, where the
/// repair lane stopped trusting an authored <see cref="PackageManifest.Source"/>.</para>
///
/// <para>Canonical form: a repository URL loses its scheme, a trailing <c>/</c> and <c>.git</c>, and
/// is lower-cased (a hosted repository's owner and name are case-insensitive); a local path keeps
/// its case. A non-empty subdirectory is appended after <c>#</c>. A registry is
/// <c>registry:</c> + its base URL, lower-cased and without a trailing <c>/</c>.</para>
/// </summary>
internal static class InstallOrigin
{
    /// <summary>The origin of a git source configured at <paramref name="repoPath"/> /
    /// <paramref name="subdir"/> — the SAME string whichever surface builds the source (a catalog
    /// node, the configured source list, the update watcher), so they can be compared.</summary>
    /// <param name="repoPath">The repository URL or local path.</param>
    /// <param name="subdir">The subdirectory packages live under, or empty.</param>
    /// <returns>The canonical origin, or null when no repository is named.</returns>
    public static string? ForRepo(string? repoPath, string? subdir)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            return null;
        var repo = repoPath.Trim().TrimEnd('/');
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];
        if (PackageSources.IsUrl(repo))
        {
            var scheme = repo.IndexOf("://", StringComparison.Ordinal);
            repo = (scheme >= 0 ? repo[(scheme + 3)..] : repo).ToLowerInvariant();
        }
        var folder = (subdir ?? "").Trim().Trim('/');
        return folder.Length == 0 ? repo : $"{repo}#{folder}";
    }

    /// <summary>The origin of a registry client reading <paramref name="registryUrl"/>.</summary>
    /// <param name="registryUrl">The registry's base URL.</param>
    public static string ForRegistry(string registryUrl) =>
        "registry:" + (registryUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>The origin <paramref name="source"/> carries, or null for a source built without
    /// one (a test double, a source constructed outside the factories).</summary>
    /// <param name="source">The source an install runs through.</param>
    public static string? Of(IPackageSource? source) => (source as IOriginatedPackageSource)?.Origin;

    /// <summary>
    /// <paramref name="source"/>, carrying <paramref name="origin"/>. Everything the source does is
    /// forwarded untouched — the origin is an identity, not a behaviour.
    /// </summary>
    /// <param name="source">The source to label.</param>
    /// <param name="origin">Its canonical origin (<see cref="ForRepo"/>); null returns the source as is.</param>
    public static IPackageSource Bind(IPackageSource source, string? origin) =>
        string.IsNullOrEmpty(origin) ? source : new OriginatedPackageSource(source, origin);

    /// <summary>
    /// The binding decision for one candidate (pure — MeshWeaver#6526).
    /// <list type="bullet">
    ///   <item>The record names an origin: <see cref="OriginVerdict.Bound"/> when it is the
    ///     candidate's, otherwise <see cref="OriginVerdict.Foreign"/> — the candidate is ignored.</item>
    ///   <item>The record names none (it predates the field): <see cref="OriginVerdict.Adopt"/> when
    ///     the candidate's origin is the ONLY watched origin listing the package,
    ///     <see cref="OriginVerdict.Ambiguous"/> when two or more list it, and
    ///     <see cref="OriginVerdict.Undetermined"/> when a watched origin could not be read — an
    ///     adoption over an incomplete census could adopt the impostor, so it fails closed.</item>
    /// </list>
    /// </summary>
    /// <param name="recordOrigin">The install record's origin, or null.</param>
    /// <param name="candidateOrigin">The origin the candidate was listed from.</param>
    /// <param name="census">Which watched origins list the package (read only when the record names
    /// no origin; null otherwise).</param>
    public static OriginVerdict Decide(string? recordOrigin, string candidateOrigin, OriginCensus? census)
    {
        if (!string.IsNullOrEmpty(recordOrigin))
            return string.Equals(recordOrigin, candidateOrigin, StringComparison.Ordinal)
                ? OriginVerdict.Bound
                : OriginVerdict.Foreign;
        if (census is null || census.Unread.Count > 0)
            return OriginVerdict.Undetermined;
        return census.Listing.Add(candidateOrigin).Count == 1 ? OriginVerdict.Adopt : OriginVerdict.Ambiguous;
    }

    private sealed class OriginatedPackageSource(IPackageSource inner, string origin)
        : IPackageSource, IOriginatedPackageSource
    {
        public string Origin => origin;

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            inner.ListPackages(gitRef);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            inner.FetchPackageFiles(package, gitRef);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths) =>
            inner.FetchPackageFiles(package, gitRef, paths);
    }
}

/// <summary>A package source that knows its own <see cref="InstallOrigin"/>.</summary>
internal interface IOriginatedPackageSource
{
    /// <summary>The canonical origin of everything this source serves.</summary>
    string Origin { get; }
}

/// <summary>The outcome of <see cref="InstallOrigin.Decide"/>. Never persisted or serialised.</summary>
internal enum OriginVerdict
{
    /// <summary>The record names this candidate's origin — reconcile as before.</summary>
    Bound,

    /// <summary>The record names a different origin — the candidate is ignored.</summary>
    Foreign,

    /// <summary>No origin recorded and exactly one watched origin lists the package — record it.</summary>
    Adopt,

    /// <summary>No origin recorded and two or more watched origins list the package — refuse, by name.</summary>
    Ambiguous,

    /// <summary>No origin recorded and the census is incomplete — neither adopt nor refuse this pass.</summary>
    Undetermined,
}

/// <summary>Which watched origins list a package id.</summary>
/// <param name="Listing">The origins whose listing contains the id.</param>
/// <param name="Unread">The watched origins whose listing could not be read this pass.</param>
internal sealed record OriginCensus(ImmutableSortedSet<string> Listing, ImmutableSortedSet<string> Unread);

/// <summary>
/// What binds a reconcile pass to the install records' origins: the origin its candidates were
/// listed from, and how to learn which watched origins list a given package id (asked only for a
/// record that names no origin yet).
/// </summary>
/// <param name="Origin">The candidates' origin.</param>
/// <param name="Census">Which watched origins list a package id.</param>
internal sealed record InstallOriginBinding(string Origin, Func<string, IObservable<OriginCensus>> Census);
