using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.GitSync;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// An <see cref="IPackageSource"/> that reads a REMOTE git repo at a commit/branch by reusing
/// GitSync's fetch (<see cref="IGitHubRepoClient.Fetch(string, string, string?, string)"/>) — so a DEPLOYED portal, which has no
/// source tree in its container, can browse/install from the plugins repo on GitHub. Still git-based
/// and NuGet-free: it fetches the repo tree at a ref, groups the folders that carry a
/// <c>package.json</c>, and returns each folder's files.
///
/// <para>Depends on a narrow <c>fetch</c> delegate rather than the whole <see cref="IGitHubRepoClient"/>
/// so it needs no test double for that large interface — production passes <c>client.Fetch</c>, tests
/// pass a stub. A public repo reads anonymously (empty token); a private one needs a token.</para>
/// </summary>
public sealed class GitHubPackageSource : IPackageSource
{
    private const string ManifestFile = "package.json";
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    private readonly Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch;
    private readonly string repoUrl;
    private readonly Func<IObservable<string>> tokenProvider;
    private readonly string subdir;
    private readonly ILogger? logger;

    /// <summary>
    /// Creates a package.json-repo source that resolves its access token FRESH before each fetch via
    /// <paramref name="tokenProvider"/> — so the registry hands in
    /// <see cref="GitHubAppTokenService.GetInstallationToken()"/> (re-minted transparently, never
    /// captured stale). The provider may emit an empty string for anonymous (public-repo) access.
    /// </summary>
    public GitHubPackageSource(
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch,
        string repoUrl,
        Func<IObservable<string>> tokenProvider,
        string subdir = "",
        ILogger? logger = null)
    {
        this.fetch = fetch;
        this.repoUrl = repoUrl;
        this.tokenProvider = tokenProvider;
        this.subdir = subdir;
        this.logger = logger;
    }

    /// <summary>
    /// Creates a package.json-repo source with a FIXED token (default empty = anonymous). Convenience
    /// for tests and public repos; the registry uses the token-provider overload so the App
    /// installation token stays fresh.
    /// </summary>
    public GitHubPackageSource(
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch,
        string repoUrl,
        string token = "",
        string subdir = "",
        ILogger? logger = null)
        : this(fetch, repoUrl, () => Observable.Return(token), subdir, logger)
    {
    }

    /// <summary>
    /// The FILTERED fetch, when the caller has one — used by <see cref="ListPackages"/> so the
    /// listing transfers only the <c>package.json</c> manifests it parses instead of the whole
    /// repository (#4222).
    ///
    /// <para>🚨 The LISTING narrows to the manifests; <see cref="FetchPackageFiles"/> narrows to the
    /// package's whole folder (MeshWeaver#5826) — never to the listing's predicate, which would
    /// install an empty package. Absent ⇒ the plain fetch, i.e. exactly the behaviour before this
    /// existed.</para>
    /// </summary>
    public Func<string, string, string?, string, Func<string, bool>, IObservable<RepoSnapshot>>?
        NarrowFetch { get; init; }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
        tokenProvider().SelectMany(token => NarrowFetch is { } narrow
                ? narrow(repoUrl, gitRef, NullIfEmpty(subdir), token, IsManifest)
                : fetch(repoUrl, gitRef, NullIfEmpty(subdir), token))
            .Select(snapshot =>
            {
                var manifests = new List<PackageManifest>();
                foreach (var file in snapshot.Files)
                {
                    if (!IsManifest(file.Path))
                        continue;
                    PackageManifest? manifest;
                    try
                    {
                        manifest = JsonSerializer.Deserialize<PackageManifest>(file.Content, ManifestJson);
                    }
                    catch (JsonException ex)
                    {
                        logger?.LogWarning(ex, "Skipping {Path}: invalid {Manifest}", file.Path, ManifestFile);
                        continue;
                    }
                    if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                        continue;
                    manifests.Add(manifest with { SourceFolder = FolderOf(file.Path) });
                }
                return (IReadOnlyList<PackageManifest>)manifests
                    .OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
            });

    /// <inheritdoc />
    /// <remarks>🚨 Through the narrow fetch when this source has one (MeshWeaver#5826): the folder is
    /// the subdirectory, and a keep-everything predicate under it moves that folder's blobs and no
    /// others — the plain fetch transferred the whole repository and read the folder out of it.
    /// The answer is identical either way.</remarks>
    public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
        FetchFolder(package, gitRef).Select(fetched => fetched.Files);

    /// <inheritdoc />
    /// <remarks>The commit is the snapshot's own, so an install records the exact commit it
    /// materialised (MeshWeaver#3848).</remarks>
    IObservable<PackageFetch> IPackageSource.FetchPackage(
        PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths) =>
        FetchFolder(package, gitRef).Select(fetched => paths is null
            ? fetched
            : fetched with
            {
                Files = fetched.Files
                    .Where(f => paths.Contains(f.RelativePath, StringComparer.Ordinal))
                    .ToImmutableList(),
            });

    /// <summary>
    /// The ancestry test behind <see cref="IPackageSource.PublishesCommit"/> — the transport's
    /// <see cref="IGitHubRepoClient.IsAncestor"/> (repository url, ancestor sha, descendant ref,
    /// token). Absent ⇒ ancestry is never established and a registry over this source serves only
    /// its configured ref (MeshWeaver#6718).
    /// </summary>
    public Func<string, string, string, string, IObservable<bool>>? IsAncestor { get; init; }

    /// <inheritdoc />
    IObservable<bool> IPackageSource.PublishesCommit(string commit, string gitRef) =>
        !PackageFetch.IsCommitSha(commit) || IsAncestor is not { } isAncestor
            ? Observable.Return(false)
            : tokenProvider().SelectMany(token => isAncestor(repoUrl, commit, gitRef, token));

    private IObservable<PackageFetch> FetchFolder(PackageManifest package, string gitRef) =>
        tokenProvider().SelectMany(token => NarrowFetch is { } narrow
                ? narrow(repoUrl, gitRef, NullIfEmpty(package.SourceFolder ?? package.Id), token, static _ => true)
                : fetch(repoUrl, gitRef, NullIfEmpty(package.SourceFolder ?? package.Id), token))
            // Carry `Binary` too — a non-UTF-8 blob's `Content` is EMPTY by design (the bytes live
            // on `RepoFile.Binary`), so projecting only `Content` publishes an empty file. See
            // PackageFile / issue #848.
            .Select(snapshot => new PackageFetch(
                snapshot.Files.Select(f => new PackageFile(f.Path, f.Content, f.Binary)).ToImmutableList(),
                snapshot.CommitSha));

    private static bool IsManifest(string path) =>
        path.EndsWith("/" + ManifestFile, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, ManifestFile, StringComparison.OrdinalIgnoreCase);

    // The repo-relative folder that directly contains the manifest (e.g. "catalog/welcome-note").
    private static string FolderOf(string manifestPath)
    {
        var idx = manifestPath.LastIndexOf('/');
        return idx < 0 ? "" : manifestPath[..idx];
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
