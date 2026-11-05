using System.Collections.Immutable;
using System.Net;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
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
///     (<see cref="PackageManifest.InstalledFiles"/>), mapped to node paths exactly as the installer
///     maps them (<see cref="PackageInstaller.InstalledNodePathOf"/>); a <c>{Space}/_GitSync</c> entry
///     names the repository, the last synced commit and the module's folder, and the file is named the
///     way the exporter names it (<see cref="NodeFileMapper"/>: the node's own serializer extension,
///     <c>index.*</c> for a node with children). Read as SYSTEM through synced queries — whose first
///     answer is the current state, not an index's — because they are platform records; what is handed
///     on is only the file's coordinate.</item>
///   <item><b>As whom</b> — the viewer's GitHub credential (<c>{user}/_Provider/GitHub</c>,
///     <see cref="GitHubCredentialService"/>), read AS the viewer and FOLLOWED live, and NOTHING else:
///     never the App installation token, never a source's configured token. No credential ⇒
///     <see cref="CodeSourceOutcomes.SignInRequired"/>, and the answer is re-read the moment one is
///     connected (the OAuth return lands before or after the saved credential syncs in — either way
///     the page follows). GitHub refusing it ⇒ <see cref="CodeSourceOutcomes.Refused"/>. The token is
///     never logged and never written anywhere.</item>
/// </list>
/// A node whose module records no GitHub origin answers <see cref="CodeSourceOutcomes.NoOrigin"/>,
/// and the page shows the mesh copy.
/// </summary>
public sealed class GitHubCodeSourceFiles(IMessageHub hub, ILogger<GitHubCodeSourceFiles>? logger = null)
    : ICodeSourceFiles
{
    private static readonly TimeSpan ProvenanceTimeout = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    /// <remarks>Emits a new answer whenever the viewer's GitHub connection changes.</remarks>
    public IObservable<CodeSourceRead> Read(string codeNodePath, AccessContext? viewer)
        => Observable.Defer(() =>
            {
                var path = codeNodePath.Trim('/');
                var partition = path.Split('/')[0];
                if (partition.Length == 0 || partition == path)
                    return Observable.Return(CodeSourceRead.WithoutOrigin);
                return OriginOf(partition, path)
                    .Catch((Exception ex) =>
                    {
                        // Provenance unreadable: say so — the page renders a failure note above the
                        // mesh copy, never a silent "authored in the mesh".
                        logger?.LogWarning(ex, "[CodeSource] the provenance of {Path} could not be read", path);
                        return Observable.Throw<CodeSourceOrigin?>(new ProvenanceUnreadable(ex));
                    })
                    .SelectMany(origin => origin is null
                        ? Observable.Return(CodeSourceRead.WithoutOrigin)
                        : ReadAs(origin, viewer));
            })
            .Catch((ProvenanceUnreadable _) => Observable.Return(new CodeSourceRead(CodeSourceOutcomes.Failed)));

    private sealed class ProvenanceUnreadable(Exception inner) : Exception(inner.Message, inner);

    // ── where the file lives ──────────────────────────────────────────────────────────────────

    private IObservable<CodeSourceOrigin?> OriginOf(string partition, string codeNodePath)
        => Observable.Zip(
                SystemQuery($"codesrc-installs:{PackageInstaller.InstalledPartition}",
                    $"path:{PackageInstaller.InstalledPartition} scope:children "
                    + $"nodeType:{PackageInstaller.PackageNodeType} select:path,id,name,nodeType,content"),
                SystemQuery($"codesrc-sync:{partition}",
                    $"path:{partition}/{GitHubSyncService.ConfigId} select:path,id,name,nodeType,content"),
                (installs, syncs) => (installs, syncs))
            .SelectMany(found =>
            {
                var options = hub.JsonSerializerOptions;
                var parsers = Parsers();
                var fromPackage = found.installs
                    .Select(n => n.ContentAs<PackageManifest>(options, logger))
                    .Where(m => m is not null
                                && string.Equals(
                                    string.IsNullOrWhiteSpace(m.TargetPartition) ? m.Id : m.TargetPartition,
                                    partition, StringComparison.OrdinalIgnoreCase))
                    .Select(m => FromInstallRecord(m!, codeNodePath, parsers))
                    .FirstOrDefault(o => o is not null);
                if (fromPackage is not null)
                    return Observable.Return<CodeSourceOrigin?>(fromPackage);

                var config = found.syncs
                    .Select(n => n.ContentAs<GitHubSyncConfig>(options, logger))
                    .FirstOrDefault(c => c is not null);
                if (config is null || !IsGitHub(config.RepositoryUrl))
                    return Observable.Return<CodeSourceOrigin?>(null);
                // The exporter's file name needs the node itself (its serializer's extension) and
                // whether it has children (leaf `X.ext` vs `X/index.ext`).
                return Observable.Zip(
                    SystemQuery($"codesrc-node:{codeNodePath}",
                        $"path:{codeNodePath} select:path,id,name,nodeType,content"),
                    SystemQuery($"codesrc-children:{codeNodePath}",
                        $"namespace:{codeNodePath} select:path,id"),
                    (node, children) =>
                    {
                        var self = node.FirstOrDefault(n => string.Equals(n.Path, codeNodePath, StringComparison.OrdinalIgnoreCase));
                        var extension = self is null ? ".cs" : NodeFileMapper.Extension(self, parsers);
                        return FromSyncEntry(config, partition, codeNodePath, extension, hasChildren: children.Count > 0);
                    });
            });

    /// <summary>
    /// One synced query as SYSTEM: its first emission is gated on the store's Initial, so it is the
    /// current state — never an eventually-consistent index's lag. Cache ids carry the
    /// <c>codesrc-</c> prefix (the synced-query cache is keyed by id alone).
    /// </summary>
    private IObservable<ImmutableList<MeshNode>> SystemQuery(string id, string query)
        => hub.ServiceProvider.GetService<AccessService>()
            .RunAsSystem(() => hub.GetWorkspace().GetQuery(id, query))
            .Take(1)
            .Timeout(ProvenanceTimeout)
            .Select(nodes => nodes.Where(n => n is not null).ToImmutableList());

    private FileFormatParserRegistry Parsers()
        => new(hub.JsonSerializerOptions, hub.ServiceProvider.GetServices<IFileFormatParser>());

    /// <summary>
    /// The file's coordinate from an install record: the record's repository at the exact commit it
    /// installed, and the installed file the installer wrote AS this node
    /// (<see cref="PackageInstaller.InstalledNodePathOf"/> — <c>index.*</c>, a Content package's
    /// rebase under its target and all); a C# source first when several map to it. The repository
    /// request uses the file's own key. Null when the record names no GitHub repository or no such
    /// file. Pure.
    /// </summary>
    public static CodeSourceOrigin? FromInstallRecord(
        PackageManifest record, string codeNodePath, FileFormatParserRegistry parsers)
    {
        if (!IsGitHub(record.RepositoryUrl) || string.IsNullOrWhiteSpace(record.InstalledFromRef)
            || record.InstalledFiles is not { Count: > 0 } files)
            return null;
        var file = files.Keys
            .Where(key => string.Equals(
                PackageInstaller.InstalledNodePathOf(record, key, parsers), codeNodePath,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(key => key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(key => key, StringComparer.Ordinal)
            .FirstOrDefault();
        return file is null ? null : new CodeSourceOrigin(record.RepositoryUrl!, record.InstalledFromRef!, file);
    }

    /// <summary>
    /// The file's coordinate from a <c>{Space}/_GitSync</c> entry: the repository at the last synced
    /// commit (the branch when none is recorded), the node's file under the module's folder, named as
    /// the exporter names it (<see cref="NodeFileMapper.ToRepoPath"/>). Null for a non-GitHub
    /// repository. Pure.
    /// </summary>
    public static CodeSourceOrigin? FromSyncEntry(
        GitHubSyncConfig config, string partition, string codeNodePath, string extension, bool hasChildren)
    {
        if (!IsGitHub(config.RepositoryUrl))
            return null;
        var gitRef = string.IsNullOrWhiteSpace(config.LastSyncCommitSha) ? config.Branch : config.LastSyncCommitSha;
        if (string.IsNullOrWhiteSpace(gitRef))
            return null;
        var folder = config.Subdirectory?.Trim().Trim('/');
        var file = NodeFileMapper.ToRepoPath(codeNodePath, partition, extension, hasChildren);
        return new CodeSourceOrigin(
            config.RepositoryUrl!.Trim().TrimEnd('/'),
            gitRef!,
            string.IsNullOrEmpty(folder) ? file : $"{folder}/{file}");
    }

    private static bool IsGitHub(string? repositoryUrl)
        => !string.IsNullOrWhiteSpace(repositoryUrl)
           && GitRepositoryProvider.Classify(repositoryUrl) == GitRepositoryProvider.GitHub;

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
        // (LayoutAreaHost.ViewerContext), so this reads exactly their own credential. LIVE: the
        // OAuth return can render before the saved credential has synced in; the answer follows it.
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAs(viewer, () => credentials.GetStream(viewer.ObjectId))
            .Select(credential => credential?.AccessToken is { Length: > 0 } token ? token : null)
            .DistinctUntilChanged()
            .Select(token => token is null
                ? Observable.Return(new CodeSourceRead(CodeSourceOutcomes.SignInRequired, origin))
                : client.GetFileText(origin.RepositoryUrl, origin.FilePath, origin.Ref, token)
                    .Select(text => new CodeSourceRead(CodeSourceOutcomes.Read, origin, text))
                    .Catch((Exception ex) => Observable.Return(Classify(origin, ex))))
            .Switch()
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "[CodeSource] reading {File} as the viewer failed", origin.FilePath);
                return Observable.Return(new CodeSourceRead(CodeSourceOutcomes.Failed, origin));
            });
    }

    /// <summary>
    /// A failed read as an answer that KEEPS the origin: 401 / 403 / 404 are about the viewer's ACCESS
    /// (GitHub answers a private repository the credential cannot see with 404), so the page offers a
    /// sign-in; a rate limit, a server error, a transport fault or an undecodable answer is not, and is
    /// a failure. Pure.
    /// </summary>
    public static CodeSourceRead Classify(CodeSourceOrigin origin, Exception error)
        => error switch
        {
            GitHubRateLimitExceededException limit
                => new CodeSourceRead(CodeSourceOutcomes.Failed, origin, StatusCode: (int)limit.StatusCode),
            GitHubApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound } refused
                => new CodeSourceRead(CodeSourceOutcomes.Refused, origin, StatusCode: (int)refused.StatusCode),
            GitHubApiException other
                => new CodeSourceRead(CodeSourceOutcomes.Failed, origin, StatusCode: (int)other.StatusCode),
            _ => new CodeSourceRead(CodeSourceOutcomes.Failed, origin),
        };
}
