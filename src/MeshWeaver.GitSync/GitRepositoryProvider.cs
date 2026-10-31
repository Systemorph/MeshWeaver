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
    /// <summary>A <c>github.com</c> repository: the full two-way sync, webhooks, pull requests and issues.</summary>
    public const string GitHub = "GitHub";

    /// <summary>Any other git remote (a GitHub Enterprise host, a local or file remote in tests):
    /// handled exactly as before this vocabulary existed.</summary>
    public const string Git = "Git";

    /// <summary>
    /// Classifies a repository URL. Null or blank answers <see cref="Git"/> — there is no
    /// repository to be anything else.
    /// </summary>
    public static string Classify(string? repositoryUrl)
    {
        if (Uri.TryCreate(repositoryUrl?.Trim(), UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return GitHub;
        return Git;
    }
}
