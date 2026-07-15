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
    /// <see cref="GitHubAppTokenService.GetInstallationToken"/> (re-minted transparently, never
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

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
        tokenProvider().SelectMany(token => fetch(repoUrl, gitRef, NullIfEmpty(subdir), token))
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
    public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
        tokenProvider().SelectMany(token =>
                fetch(repoUrl, gitRef, NullIfEmpty(package.SourceFolder ?? package.Id), token))
            .Select(snapshot => (IReadOnlyList<PackageFile>)snapshot.Files
                .Select(f => new PackageFile(f.Path, f.Content)).ToList());

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
