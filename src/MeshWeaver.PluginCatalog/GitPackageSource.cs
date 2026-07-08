using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.GitSync;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// An <see cref="IPackageSource"/> backed by a LOCAL git repository, read at a chosen commit/branch
/// through the <c>git</c> CLI (<see cref="GitCli"/> → the Process <c>IIoPool</c>). This is the
/// "pick a git commit + folder" primitive with ZERO NuGet dependency: it never clones or hits a
/// package feed — it runs <c>git ls-tree</c> / <c>git show</c> against an on-disk repo. A
/// GitHub-fetch source (reusing GitSync's repo client) can slot in behind <see cref="IPackageSource"/>
/// later without changing the installer.
/// </summary>
public sealed class GitPackageSource(GitCli git, string repoPath, string subdir = "", ILogger? logger = null)
    : IPackageSource
{
    private const string ManifestFile = "package.json";
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    // Package folders live at the repo root, or under `subdir/` when configured. git ls-tree emits
    // FULL repo-relative folder paths either way (e.g. "welcome-note" or "catalog/welcome-note"),
    // which become the package's SourceFolder — so FetchPackageFiles needs no special-casing.
    private string[] ListArgs(string gitRef) =>
        string.IsNullOrEmpty(subdir)
            ? ["ls-tree", "-d", "--name-only", gitRef]
            : ["ls-tree", "-d", "--name-only", gitRef, "--", $"{subdir.TrimEnd('/')}/"];

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
        git.Run(repoPath, ListArgs(gitRef))
            .SelectMany(res =>
            {
                if (res.ExitCode != 0)
                    return Observable.Throw<IReadOnlyList<PackageManifest>>(
                        new InvalidOperationException($"git ls-tree failed ({res.ExitCode}): {res.StdErr}"));
                var folders = SplitLines(res.StdOut);
                if (folders.Length == 0)
                    return Observable.Return<IReadOnlyList<PackageManifest>>([]);
                return folders
                    .Select(folder => ReadManifest(gitRef, folder))
                    .Merge()
                    .Where(m => m is not null).Select(m => m!)
                    .ToList()
                    .Select(list => (IReadOnlyList<PackageManifest>)
                        list.OrderBy(m => m.Id, StringComparer.Ordinal).ToList());
            });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
    {
        var folder = package.SourceFolder ?? package.Id;
        return git.Run(repoPath, ["ls-tree", "-r", "--name-only", gitRef, "--", $"{folder}/"])
            .SelectMany(res =>
            {
                if (res.ExitCode != 0)
                    return Observable.Throw<IReadOnlyList<PackageFile>>(
                        new InvalidOperationException($"git ls-tree failed ({res.ExitCode}): {res.StdErr}"));
                var paths = SplitLines(res.StdOut);
                if (paths.Length == 0)
                    return Observable.Return<IReadOnlyList<PackageFile>>([]);
                return paths
                    .Select(p => ShowFile(gitRef, p).Select(c => c is null ? null : new PackageFile(p, c)))
                    .Merge()
                    .Where(f => f is not null).Select(f => f!)
                    .ToList()
                    .Select(list => (IReadOnlyList<PackageFile>)list);
            });
    }

    // Reads {folder}/package.json at the ref and deserializes it; null when the folder has no
    // manifest (→ not an installable package) or the manifest is invalid.
    private IObservable<PackageManifest?> ReadManifest(string gitRef, string folder) =>
        ShowFile(gitRef, $"{folder}/{ManifestFile}")
            .Select(content =>
            {
                if (content is null) return null;
                try
                {
                    var manifest = JsonSerializer.Deserialize<PackageManifest>(content, ManifestJson);
                    if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id)) return null;
                    return manifest with { SourceFolder = folder };
                }
                catch (JsonException ex)
                {
                    logger?.LogWarning(ex, "Skipping folder {Folder}: invalid {Manifest}", folder, ManifestFile);
                    return null;
                }
            });

    // git show <ref>:<path> — the file's content at that ref, or null when the path is absent there.
    private IObservable<string?> ShowFile(string gitRef, string path) =>
        git.Run(repoPath, ["show", $"{gitRef}:{path}"])
            .Select(res => res.ExitCode == 0 ? res.StdOut : (string?)null);

    private static string[] SplitLines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
