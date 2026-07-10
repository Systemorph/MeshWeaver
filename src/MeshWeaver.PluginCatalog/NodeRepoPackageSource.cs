using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.GitSync;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// An <see cref="IPackageSource"/> for NODE-NATIVE plugin repos — the shape the
/// <c>MeshWeaver.Plugins</c> repo ships (node-per-file, "the node is the manifest"). Each top-level
/// <c>&lt;Plugin&gt;.json</c> is a Space root carrying a <c>PluginManifest</c>, and its sibling
/// <c>&lt;Plugin&gt;/</c> folder holds the <c>NodeType</c> nodes, their <c>Source/*.cs</c> and docs —
/// every file already a MeshNode at its CANONICAL path (no per-partition rebase). Reuses GitSync's
/// fetch so a deployed portal reads the repo over HTTP.
///
/// <para>A plugin's <see cref="PackageManifest.Version"/> is the repo commit sha, so a new commit
/// surfaces as an available update; the installer then writes only the nodes that actually changed.</para>
/// </summary>
public sealed class NodeRepoPackageSource(
    Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch,
    string repoUrl,
    string token = "",
    ILogger? logger = null) : IPackageSource
{
    private const string SpaceNodeType = "Space";

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
        fetch(repoUrl, gitRef, null, token)
            .Select(snapshot =>
            {
                var manifests = new List<PackageManifest>();
                foreach (var file in snapshot.Files)
                {
                    // A plugin root is a TOP-LEVEL `<Plugin>.json` (no '/') whose node is a Space.
                    if (file.Path.Contains('/', StringComparison.Ordinal)
                        || !file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var (nodeType, name, description) = Peek(file.Content, file.Path);
                    if (!string.Equals(nodeType, SpaceNodeType, StringComparison.Ordinal))
                        continue;
                    var id = file.Path[..^".json".Length];
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
        fetch(repoUrl, gitRef, null, token)
            .Select(snapshot =>
            {
                var id = package.Id;
                var rootFile = id + ".json";
                var folderPrefix = id + "/";
                return (IReadOnlyList<PackageFile>)snapshot.Files
                    .Where(f => string.Equals(f.Path, rootFile, StringComparison.Ordinal)
                        || f.Path.StartsWith(folderPrefix, StringComparison.Ordinal))
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
