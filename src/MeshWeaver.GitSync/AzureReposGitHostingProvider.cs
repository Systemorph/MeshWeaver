using System.Reactive.Linq;

namespace MeshWeaver.GitSync;

/// <summary>
/// Azure Repos as a <see cref="IGitHostingProvider"/> (policy <c>azure-repos-two-way</c>): claims
/// every <c>dev.azure.com</c> / <c>{org}.visualstudio.com</c> repository URL
/// (<see cref="AzureReposRepository.TryParse"/>), runs it on <see cref="AzureReposRepoClient"/>,
/// authenticates with the instance's DECLARED Azure DevOps identity
/// (<see cref="AzureReposTokenService"/>), and refuses pull requests and issues by name
/// (<see cref="AzureReposSyncPolicy"/>).
///
/// <para>🚨 <b>Transitional.</b> The platform registers this as a BUILT-IN provider only until the
/// Azure Repos provider ships as its own module in MeshWeaver.Plugins; a contributed provider of
/// kind <see cref="GitRepositoryProvider.AzureRepos"/> replaces it
/// (<see cref="GitHostingProviders"/>), and then this type leaves the platform with
/// <c>Azure.Identity</c> (Doc/Architecture/CarvingProjectsOutOfCore).</para>
/// </summary>
/// <param name="tokens">The declared identity; null refuses every token request by name
/// (<c>gitsync.azure.notRegistered</c>).</param>
/// <param name="client">The Azure DevOps REST client.</param>
public sealed class AzureReposGitHostingProvider(AzureReposTokenService? tokens, AzureReposRepoClient client)
    : IGitHostingProvider
{
    /// <inheritdoc />
    public string Kind => GitRepositoryProvider.AzureRepos;

    /// <inheritdoc />
    public bool Claims(string repositoryUrl) => AzureReposRepository.TryParse(repositoryUrl, out _);

    /// <inheritdoc />
    public IGitHubRepoClient RepoClient => client;

    /// <inheritdoc />
    public IObservable<string> GetAccessToken()
        => tokens is null
            ? Observable.Throw<string>(GitSyncRefusal.Of("gitsync.azure.notRegistered").ToException())
            : tokens.GetToken();

    /// <inheritdoc />
    public GitSyncRefusal? Refuse(string repositoryUrl, string operation)
        => operation is GitHostingOperation.PullRequest or GitHostingOperation.Issues
            ? GitSyncRefusal.Of("gitsync.azure.unsupported." + operation, ("url", repositoryUrl))
            : null;
}
