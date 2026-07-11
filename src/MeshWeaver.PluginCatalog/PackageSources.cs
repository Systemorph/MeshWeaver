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
            return nodeRepo
                ? new NodeRepoPackageSource(client.Fetch, src, token: "", logger)
                : new GitHubPackageSource(client.Fetch, src, token: "", subdir, logger);
        }
        var git = new GitCli(hub.ServiceProvider.GetRequiredService<IoPoolRegistry>());
        return new GitPackageSource(git, src, subdir, logger);
    }

    private static bool IsUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("git@", StringComparison.Ordinal);
}
