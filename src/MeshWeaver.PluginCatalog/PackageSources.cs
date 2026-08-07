using System.Reactive.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Builds the git-based <see cref="IPackageSource"/> for a repo path/ref — the ONE place that maps a
/// configured source (a URL → <see cref="GitHubPackageSource"/> via GitSync's client, a local path →
/// <see cref="GitPackageSource"/> via the git CLI) so the <c>PluginCatalog</c> node view and the
/// registry REST endpoints construct sources identically. Git-based end to end, no NuGet.
/// </summary>
public static class PackageSources
{
    /// <summary>
    /// Builds a package source for <paramref name="sourceRepoPath"/> (a URL or local path), or
    /// <c>null</c> when the path is empty / a URL source has no <see cref="IGitHubRepoClient"/>.
    /// <paramref name="nodeRepo"/> selects the format for a URL source: <c>true</c> (the default the
    /// registry uses) reads a node-native repo — <c>&lt;Plugin&gt;/index.json</c> Space roots, node-per-file
    /// (<see cref="NodeRepoPackageSource"/>); <c>false</c> reads a <c>package.json</c>-manifest repo
    /// (<see cref="GitHubPackageSource"/>). A local path always uses the git-CLI package.json source.
    /// </summary>
    public static IPackageSource? FromRepo(
        IMessageHub hub, string? sourceRepoPath, string? sourceSubdir, ILogger? logger = null, bool nodeRepo = false)
    {
        if (sourceRepoPath is not { Length: > 0 } src)
            return null;
        var subdir = sourceSubdir ?? "";
        if (IsUrl(src))
        {
            var client = hub.ServiceProvider.GetService<IGitHubRepoClient>();
            if (client is null)
            {
                logger?.LogWarning("Catalog source {Src} is a URL but no IGitHubRepoClient is registered.", src);
                return null;
            }
            // The registry HOLDS the credential: resolve the GitHub App INSTALLATION token FRESH
            // before each fetch — the same machine identity GitSync's ResolveAuth uses (its 1h token
            // is re-minted transparently). Passing token:"" (the old behavior) made
            // OctokitGitHubRepoClient.Client("") throw ArgumentException on Octokit's empty
            // Credentials, so any configured URL source 500'd the /api/plugins endpoint. When the App
            // is unconfigured (or absent) the provider yields an empty token → anonymous access to a
            // public repo (no throw, thanks to the Client("") anonymous fallback).
            var tokenProvider = AppInstallationTokenProvider(hub);
            return nodeRepo
                ? new NodeRepoPackageSource(client.Fetch, src, tokenProvider, logger)
                : new GitHubPackageSource(client.Fetch, src, tokenProvider, subdir, logger);
        }
        // A LOCAL path in node-repo format: read the checkout straight off disk. This is what lets
        // a registry serve plugins with NO GitHub credential at all — the local-dev / air-gapped
        // equivalent of the App identity, and the only way a local stack can serve the node-native
        // repo MeshWeaver.Plugins actually ships (before this, a local path silently fell through
        // to the package.json source below and listed nothing).
        if (nodeRepo)
            return new NodeRepoPackageSource(LocalDirectoryFetch(hub, logger), src, "", logger);

        var git = new GitCli(hub.ServiceProvider.GetRequiredService<IoPoolRegistry>());
        return new GitPackageSource(git, src, subdir, logger);
    }

    /// <summary>
    /// A fetch delegate that snapshots a local directory as if it were a repo checkout — the
    /// filesystem counterpart of <c>IGitHubRepoClient.Fetch</c>. Blocking file IO, so it runs on
    /// the FileSystem <see cref="IIoPool"/>: never inline on the caller's (hub) thread.
    ///
    /// <para><c>.git</c> and other dot-directories are skipped — a working checkout carries a large
    /// object store that is not repo CONTENT, and walking it would dwarf the actual files. The
    /// snapshot's "commit sha" is a content hash of the file set, so an unchanged directory yields
    /// a stable ref and the installer's checksum gate correctly reports "nothing to do".</para>
    /// </summary>
    private static Func<string, string, string?, string, IObservable<RepoSnapshot>> LocalDirectoryFetch(
        IMessageHub hub, ILogger? logger)
    {
        var pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
                   ?? IoPool.Unbounded;
        return (root, _, _, _) => pool.InvokeBlocking(_ =>
        {
            if (!Directory.Exists(root))
            {
                logger?.LogWarning("Local plugin source '{Root}' does not exist — serving nothing.", root);
                return new RepoSnapshot("local-missing", []);
            }

            var files = new List<RepoFile>();
            var hash = new System.Text.StringBuilder();
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                // Skip dot-directories (.git, .github, …) — infrastructure, not repo content.
                if (relative.Split('/').Any(seg => seg.StartsWith('.')))
                    continue;
                var info = new FileInfo(path);
                hash.Append(relative).Append(':').Append(info.Length).Append(';');
                files.Add(IsProbablyText(relative)
                    ? new RepoFile(relative, File.ReadAllText(path))
                    : new RepoFile(relative, "", File.ReadAllBytes(path)));
            }

            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(hash.ToString())))[..12].ToLowerInvariant();
            logger?.LogInformation(
                "Local plugin source '{Root}': {Count} file(s), content ref {Sha}", root, files.Count, sha);
            return new RepoSnapshot($"local-{sha}", files);
        });
    }

    // Binary files (icons, images) must travel as bytes; reading them as text corrupts them.
    private static bool IsProbablyText(string relativePath) =>
        Path.GetExtension(relativePath).ToLowerInvariant() is
            ".json" or ".md" or ".cs" or ".csx" or ".txt" or ".yml" or ".yaml" or ".xml"
            or ".js" or ".ts" or ".css" or ".html" or ".svg" or ".sql" or ".py" or ".lock" or "";

    /// <summary>
    /// A token provider that yields the GitHub App INSTALLATION token when the App identity is
    /// configured (<c>GitHub:App:ClientId</c> + <c>GitHub:App:PrivateKey</c>), else an empty string
    /// (anonymous access to a public repo). Resolved lazily on each call so a re-minted token is
    /// always current and an unconfigured/absent service degrades gracefully rather than throwing.
    /// </summary>
    private static Func<IObservable<string>> AppInstallationTokenProvider(IMessageHub hub)
    {
        var appTokens = hub.ServiceProvider.GetService<GitHubAppTokenService>();
        return appTokens is { IsConfigured: true }
            ? appTokens.GetInstallationToken
            : () => Observable.Return(string.Empty);
    }

    private static bool IsUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("git@", StringComparison.Ordinal);
}
