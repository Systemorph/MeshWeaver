namespace MeshWeaver.GitSync;

/// <summary>
/// Which git hosting service a sync source's <see cref="GitHubSyncConfig.RepositoryUrl"/> names —
/// an OPEN vocabulary of string constants (policy <c>open-vocabulary-string-constants</c>), DERIVED
/// from the URL on every read and never persisted.
///
/// <para><b>Why derived and not a field (MeshWeaver#5248).</b> <see cref="GitHubSyncConfig"/> is
/// persisted by type name on every synced Space in the fleet, and in-mesh C# may name it. A
/// provider-neutral rename would be a public-surface change plus a migration of every persisted
/// <c>$type</c>; a stored <c>Provider</c> field could disagree with the URL beside it. The URL
/// already says which service it is, so the provider is read off it — no migration, and nothing
/// that can drift.</para>
/// </summary>
public static class GitRepositoryProvider
{
    /// <summary>A <c>github.com</c> repository: the full two-way sync, webhooks and pull requests.</summary>
    public const string GitHub = "GitHub";

    /// <summary>An Azure Repos repository (<c>dev.azure.com</c> or the legacy
    /// <c>{org}.visualstudio.com</c>): PUSH-ONLY in this version (policy
    /// <c>azure-repos-push-only</c>) — see <see cref="AzureReposPushPolicy"/>.</summary>
    public const string AzureRepos = "AzureRepos";

    /// <summary>Any other git remote (a GitHub Enterprise host, a local or file remote in tests):
    /// handled exactly as before this vocabulary existed.</summary>
    public const string Git = "Git";

    /// <summary>
    /// Classifies a repository URL. Null or blank answers <see cref="Git"/> — there is no
    /// repository to be anything else.
    /// </summary>
    public static string Classify(string? repositoryUrl)
    {
        if (AzureReposRepository.TryParse(repositoryUrl, out _))
            return AzureRepos;
        if (Uri.TryCreate(repositoryUrl?.Trim(), UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return GitHub;
        return Git;
    }

    /// <summary>True when the URL names an Azure Repos repository.</summary>
    public static bool IsAzureRepos(string? repositoryUrl) => Classify(repositoryUrl) == AzureRepos;
}

/// <summary>
/// An Azure Repos repository named by its organisation, project and repository. Parsed from the
/// two URL shapes Azure DevOps hands out:
/// <list type="bullet">
///   <item><c>https://dev.azure.com/{org}/{project}/_git/{repo}</c> (also with the
///     <c>https://{org}@dev.azure.com/…</c> user prefix the clone dialog adds);</item>
///   <item><c>https://{org}.visualstudio.com/[DefaultCollection/]{project}/_git/{repo}</c> (legacy).</item>
/// </list>
/// </summary>
/// <param name="Organization">The Azure DevOps organisation.</param>
/// <param name="Project">The project inside the organisation.</param>
/// <param name="Repository">The repository inside the project.</param>
public sealed record AzureReposRepository(string Organization, string Project, string Repository)
{
    /// <summary>The canonical clone URL, <c>https://dev.azure.com/{org}/{project}/_git/{repo}</c>.</summary>
    public string CloneUrl =>
        $"https://dev.azure.com/{Uri.EscapeDataString(Organization)}/{Uri.EscapeDataString(Project)}/_git/{Uri.EscapeDataString(Repository)}";

    /// <summary>Parses an Azure Repos URL; false for every other shape (GitHub, anything else).</summary>
    public static bool TryParse(string? repositoryUrl, out AzureReposRepository repository)
    {
        repository = null!;
        if (!Uri.TryCreate(repositoryUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var segments = uri.AbsolutePath.Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        var host = uri.Host;
        string organization;
        string[] rest;
        if (string.Equals(host, "dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length < 1)
                return false;
            organization = segments[0];
            rest = segments[1..];
        }
        else if (host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            organization = host[..^".visualstudio.com".Length];
            rest = segments.Length > 0 && string.Equals(segments[0], "DefaultCollection", StringComparison.OrdinalIgnoreCase)
                ? segments[1..]
                : segments;
        }
        else
        {
            return false;
        }

        // {project}/_git/{repo} — exactly; a trailing ".git" on the repo is tolerated.
        if (rest.Length != 3 || !string.Equals(rest[1], "_git", StringComparison.Ordinal))
            return false;
        var repo = rest[2].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? rest[2][..^4] : rest[2];
        if (organization.Length == 0 || rest[0].Length == 0 || repo.Length == 0)
            return false;
        repository = new AzureReposRepository(organization, rest[0], repo);
        return true;
    }
}
