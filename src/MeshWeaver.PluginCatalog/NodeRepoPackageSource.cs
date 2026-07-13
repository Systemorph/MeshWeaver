using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.GitSync;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// An <see cref="IPackageSource"/> for NODE-NATIVE plugin repos — the shape the
/// <c>MeshWeaver.Plugins</c> repo ships (node-per-file, "the node is the manifest"). Each
/// <c>&lt;Plugin&gt;/</c> folder is one plugin — a mesh partition: <c>&lt;Plugin&gt;/index.json</c>
/// is its Space root carrying a <c>PluginManifest</c> (the partition root lives INSIDE the folder,
/// the same <c>NodeFileMapper</c> mapping GitSync uses), and the rest of the folder holds the
/// <c>NodeType</c> nodes, their <c>Source/*.cs</c> and docs — every file already a MeshNode at its
/// CANONICAL path (no per-partition rebase). Reuses GitSync's fetch so a deployed portal reads the
/// repo over HTTP.
///
/// <para>A plugin's <see cref="PackageManifest.Version"/> is the repo commit sha, so a new commit
/// surfaces as an available update; the installer then writes only the nodes that actually changed.</para>
/// </summary>
public sealed class NodeRepoPackageSource : IPackageSource
{
    private const string SpaceNodeType = "Space";

    private readonly Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch;
    private readonly string repoUrl;
    private readonly Func<IObservable<string>> tokenProvider;
    private readonly ILogger? logger;

    /// <summary>
    /// Creates a node-repo source that resolves its access token FRESH before each fetch via
    /// <paramref name="tokenProvider"/> — so the registry hands in
    /// <see cref="GitHubAppTokenService.GetInstallationToken"/>, whose short-lived (1h) installation
    /// token is re-minted transparently and never captured stale. The provider may emit an empty
    /// string for anonymous (public-repo) access.
    /// </summary>
    public NodeRepoPackageSource(
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch,
        string repoUrl,
        Func<IObservable<string>> tokenProvider,
        ILogger? logger = null)
    {
        this.fetch = fetch;
        this.repoUrl = repoUrl;
        this.tokenProvider = tokenProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Creates a node-repo source with a FIXED token (default empty = anonymous). Convenience for
    /// tests and public repos; the registry uses the token-provider overload so the App installation
    /// token stays fresh.
    /// </summary>
    public NodeRepoPackageSource(
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch,
        string repoUrl,
        string token = "",
        ILogger? logger = null)
        : this(fetch, repoUrl, () => Observable.Return(token), logger)
    {
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
        tokenProvider().SelectMany(token => fetch(repoUrl, gitRef, null, token))
            .Select(snapshot =>
            {
                var manifests = new List<PackageManifest>();
                foreach (var file in snapshot.Files)
                {
                    // A plugin root is `<Plugin>/index.json` — the partition root INSIDE its folder
                    // (the folder is the unit of import; NodeFileMapper maps root ↔ index.json) —
                    // whose node is a Space. Checked allocation-free: exactly one '/' and an
                    // `index.json` tail; only a match slices out the id.
                    var slash = file.Path.IndexOf('/');
                    if (slash <= 0
                        || file.Path.IndexOf('/', slash + 1) >= 0
                        || !file.Path.AsSpan(slash + 1).Equals("index.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var (nodeType, name, description) = Peek(file.Content, file.Path);
                    if (!string.Equals(nodeType, SpaceNodeType, StringComparison.Ordinal))
                        continue;
                    var id = file.Path[..slash];
                    manifests.Add(new PackageManifest
                    {
                        Id = id,
                        Name = name ?? id,
                        Description = description,
                        Kind = PackageKind.NodeRepo,
                        TargetPartition = id,
                        SourceFolder = id,
                        Version = snapshot.CommitSha,
                    });
                }
                return (IReadOnlyList<PackageManifest>)manifests
                    .OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
            });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
        tokenProvider().SelectMany(token => fetch(repoUrl, gitRef, null, token))
            .Select(snapshot =>
            {
                // The whole plugin — root included — lives under `<Plugin>/`.
                var folderPrefix = package.Id + "/";
                return (IReadOnlyList<PackageFile>)snapshot.Files
                    .Where(f => f.Path.StartsWith(folderPrefix, StringComparison.Ordinal))
                    .Select(f => new PackageFile(f.Path, f.Content))
                    .ToList();
            });

    // Reads the node's type/name/description straight from the JSON — no MeshNode deserialization, so
    // the source needs no hub serializer options and unregistered content types (PluginManifest) don't
    // matter for listing.
    private (string? NodeType, string? Name, string? Description) Peek(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return (
                r.TryGetProperty("nodeType", out var nt) ? nt.GetString() : null,
                r.TryGetProperty("name", out var n) ? n.GetString() : null,
                r.TryGetProperty("description", out var d) ? d.GetString() : null);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Node-repo catalog: {Path} is not valid JSON; skipped.", path);
            return (null, null, null);
        }
    }
}
