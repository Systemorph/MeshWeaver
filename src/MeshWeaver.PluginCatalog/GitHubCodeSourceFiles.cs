using System.Collections.Immutable;
using System.Net;
using System.Reactive.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// <see cref="ICodeSourceFiles"/> for GitHub: finds where a Code node's file lives from its
/// module's recorded provenance and reads it with the VIEWER's own connected GitHub credential.
///
/// <list type="number">
///   <item><b>Where</b> — the module is the node's partition. Its install record
///     (<c>Plugins/{id}</c>, <see cref="PackageManifest.TargetPartition"/> = the partition) names the
///     repository (<see cref="PackageManifest.RepositoryUrl"/>), the exact commit
///     (<see cref="PackageManifest.InstalledFromRef"/>) and every installed file
///     (<see cref="PackageManifest.InstalledFiles"/>, repo-relative); a <c>{Space}/_GitSync</c> entry
///     names the repository, the last synced commit and the module's folder. Read as SYSTEM: they
///     are platform records, and what is handed on is only the file's coordinate.</item>
///   <item><b>As whom</b> — the viewer's GitHub credential (<c>{user}/_Provider/GitHub</c>,
///     <see cref="GitHubCredentialService"/>) and NOTHING else: never the App installation token,
///     never a source's configured token. No credential ⇒ <see cref="CodeSourceOutcomes.SignInRequired"/>;
///     GitHub refusing it ⇒ <see cref="CodeSourceOutcomes.Refused"/>. The token is never logged and
///     never written anywhere.</item>
/// </list>
/// A node whose module records no GitHub origin answers <see cref="CodeSourceOutcomes.NoOrigin"/>,
/// and the page shows the mesh copy.
/// </summary>
public sealed class GitHubCodeSourceFiles(IMessageHub hub, ILogger<GitHubCodeSourceFiles>? logger = null)
    : ICodeSourceFiles
{
    private static readonly TimeSpan ProvenanceTimeout = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    public IObservable<CodeSourceRead> Read(string codeNodePath, AccessContext? viewer)
        => Observable.Defer(() =>
            {
                var path = codeNodePath.Trim('/');
                var partition = path.Split('/')[0];
                if (partition.Length == 0 || partition == path)
                    return Observable.Return(CodeSourceRead.WithoutOrigin);
                return OriginOf(partition, path).SelectMany(origin => origin is null
                    ? Observable.Return(CodeSourceRead.WithoutOrigin)
                    : ReadAs(origin, viewer));
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "[CodeSource] reading {Path} from its repository failed", codeNodePath);
                return Observable.Return(new CodeSourceRead(CodeSourceOutcomes.Failed));
            });

    // ── where the file lives ──────────────────────────────────────────────────────────────────

    private IObservable<CodeSourceOrigin?> OriginOf(string partition, string codeNodePath)
        => Observable.Zip(
                QueryAsSystem($"path:{PackageInstaller.InstalledPartition} scope:children "
                              + $"nodeType:{PackageInstaller.PackageNodeType}"),
                QueryAsSystem($"path:{partition}/{GitHubSyncService.ConfigId}"),
                (installs, syncs) =>
                {
                    var options = hub.JsonSerializerOptions;
                    var fromPackage = installs
                        .Select(n => n.ContentAs<PackageManifest>(options, logger))
                        .Where(m => m is not null
                                    && string.Equals(
                                        string.IsNullOrWhiteSpace(m.TargetPartition) ? m.Id : m.TargetPartition,
                                        partition, StringComparison.OrdinalIgnoreCase))
                        .Select(m => FromInstallRecord(m!, codeNodePath))
                        .FirstOrDefault(o => o is not null);
                    return fromPackage ?? syncs
                        .Select(n => n.ContentAs<GitHubSyncConfig>(options, logger))
                        .Where(c => c is not null)
                        .Select(c => FromSyncEntry(c!, partition, codeNodePath))
                        .FirstOrDefault(o => o is not null);
                });

    private IObservable<ImmutableList<MeshNode>> QueryAsSystem(string query)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query)))
            .Take(1)
            .Timeout(ProvenanceTimeout)
            .Select(change => change.Items.ToImmutableList());
    }

    /// <summary>
    /// The file's coordinate from an install record: the record's repository at the exact commit it
    /// installed, and the installed file whose path IS the node's path plus an extension (a C# source
    /// first). Null when the record names no GitHub repository or no such file. Pure.
    /// </summary>
    public static CodeSourceOrigin? FromInstallRecord(PackageManifest record, string codeNodePath)
    {
        if (!IsGitHub(record.RepositoryUrl) || string.IsNullOrWhiteSpace(record.InstalledFromRef)
            || record.InstalledFiles is not { Count: > 0 } files)
            return null;
        var file = files.Keys
            .Where(key => string.Equals(WithoutExtension(key), codeNodePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(key => key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(key => key, StringComparer.Ordinal)
            .FirstOrDefault();
        return file is null ? null : new CodeSourceOrigin(record.RepositoryUrl!, record.InstalledFromRef!, file);
    }

    /// <summary>
    /// The file's coordinate from a <c>{Space}/_GitSync</c> entry: the repository at the last synced
    /// commit (the branch when none is recorded), the node's file under the module's folder — a Code
    /// node is a <c>.cs</c> file (<see cref="NodeFileMapper.ToRepoPath"/>). Null for a non-GitHub
    /// repository. Pure.
    /// </summary>
    public static CodeSourceOrigin? FromSyncEntry(GitHubSyncConfig config, string partition, string codeNodePath)
    {
        if (!IsGitHub(config.RepositoryUrl))
            return null;
        var gitRef = string.IsNullOrWhiteSpace(config.LastSyncCommitSha) ? config.Branch : config.LastSyncCommitSha;
        if (string.IsNullOrWhiteSpace(gitRef))
            return null;
        var folder = config.Subdirectory?.Trim().Trim('/');
        var file = NodeFileMapper.ToRepoPath(codeNodePath, partition, ".cs", hasChildren: false);
        return new CodeSourceOrigin(
            config.RepositoryUrl!.Trim().TrimEnd('/'),
            gitRef!,
            string.IsNullOrEmpty(folder) ? file : $"{folder}/{file}");
    }

    private static bool IsGitHub(string? repositoryUrl)
        => !string.IsNullOrWhiteSpace(repositoryUrl)
           && GitRepositoryProvider.Classify(repositoryUrl) == GitRepositoryProvider.GitHub;

    private static string WithoutExtension(string path)
    {
        var slash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        return dot > slash ? path[..dot] : path;
    }

    // ── reading it as the viewer ──────────────────────────────────────────────────────────────

    private IObservable<CodeSourceRead> ReadAs(CodeSourceOrigin origin, AccessContext? viewer)
    {
        var credentials = hub.ServiceProvider.GetService<GitHubCredentialService>();
        var client = hub.ServiceProvider.GetService<IGitHubRepoClient>();
        if (credentials is null || client is null)
            return Observable.Return(CodeSourceRead.WithoutOrigin);
        if (viewer is null || viewer.IsVirtual || string.IsNullOrEmpty(viewer.ObjectId))
            return Observable.Return(new CodeSourceRead(CodeSourceOutcomes.SignInRequired, origin));

        // 🚨 AS THE VIEWER: the area renders this on a live emission, off the viewer's delivery,
        // where the ambient identity is nobody's — and the credential node is readable by its owner
        // only. The viewer is the one the layout host captured from the subscription
        // (LayoutAreaHost.ViewerContext), so this reads exactly their own credential.
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAs(viewer, () => credentials.Get(viewer.ObjectId)).SelectMany(credential =>
            credential?.AccessToken is not { Length: > 0 } token
                ? Observable.Return(new CodeSourceRead(CodeSourceOutcomes.SignInRequired, origin))
                : client.GetFileText(origin.RepositoryUrl, origin.FilePath, origin.Ref, token)
                    .Select(text => new CodeSourceRead(CodeSourceOutcomes.Read, origin, text))
                    .Catch((GitHubApiException ex) => Observable.Return(ex is GitHubRateLimitExceededException
                        ? new CodeSourceRead(CodeSourceOutcomes.Failed, origin, StatusCode: (int)ex.StatusCode)
                        : Classify(origin, ex.StatusCode))));
    }

    /// <summary>
    /// A GitHub refusal as an answer: 401 / 403 / 404 are about the viewer's ACCESS (GitHub answers
    /// a private repository the credential cannot see with 404), so the page offers a sign-in; a rate
    /// limit or a server error is not, and is reported as a failure. Pure.
    /// </summary>
    public static CodeSourceRead Classify(CodeSourceOrigin origin, HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound
               or HttpStatusCode.Forbidden
            ? new CodeSourceRead(CodeSourceOutcomes.Refused, origin, StatusCode: (int)status)
            : new CodeSourceRead(CodeSourceOutcomes.Failed, origin, StatusCode: (int)status);
}
