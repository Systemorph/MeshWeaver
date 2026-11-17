using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.GitSync;

/// <summary>
/// 🚨 The branch a sync source reads on THIS instance's platform line (policy
/// <c>major-lines-run-separately</c>, Doc/Architecture/PluginRegistryServesEachLine → "GitSync syncs
/// each line's ref"). Implemented where the declaration lives — PluginCatalog reads the record's
/// <c>pluginRepos[].lineRefs</c> as <c>PluginCatalog:LineRefs</c>, the very declaration the registry
/// serves each line by — so the registry and GitSync can never pick different refs for one line.
///
/// <para>A mesh that registers none syncs every source at its stored branch, exactly as before.</para>
/// </summary>
public interface IGitSyncLineRefs
{
    /// <summary>
    /// The branch this instance syncs for a source stored as <paramref name="repositoryUrl"/> @
    /// <paramref name="storedBranch"/>: the ref the repository declares for this instance's line, when
    /// <paramref name="storedBranch"/> is one of the refs that declaration names (its configured ref or
    /// any per-line ref). Null when the repository declares nothing, or when the stored branch is one
    /// the declaration does not name (a branch somebody chose on purpose is never moved).
    /// </summary>
    /// <param name="repositoryUrl">The source's repository URL.</param>
    /// <param name="storedBranch">The branch the source's config stores (blank already read as <c>main</c>).</param>
    /// <returns>The line's branch, or null to keep <paramref name="storedBranch"/>.</returns>
    string? LineBranch(string repositoryUrl, string storedBranch);
}

/// <summary>
/// The ONE reading of the branch a <see cref="GitHubSyncConfig"/> syncs: its stored branch (<c>main</c>
/// when blank), moved to this instance's line by <see cref="IGitSyncLineRefs"/> when one is registered.
/// Every import, push match, branch reconcile, export and pull request reads the branch through here, so
/// a v3 instance syncs <c>main3</c> and a v4 instance <c>main</c> from the same stored config, and a roll
/// from 3 to 4 switches the source without anybody editing it.
/// </summary>
public static class GitSyncLine
{
    /// <summary>The branch a config that names none syncs.</summary>
    public const string DefaultBranch = "main";

    /// <summary>The branch as STORED on the config: trimmed, <see cref="DefaultBranch"/> when blank.
    /// Pure.</summary>
    /// <param name="config">The stored config.</param>
    public static string StoredBranchOf(GitHubSyncConfig config) =>
        string.IsNullOrWhiteSpace(config.Branch) ? DefaultBranch : config.Branch.Trim();

    /// <summary>The branch <paramref name="config"/> syncs on this instance's line.</summary>
    /// <param name="services">The services carrying <see cref="IGitSyncLineRefs"/>, or null.</param>
    /// <param name="config">The stored config.</param>
    public static string BranchOf(IServiceProvider? services, GitHubSyncConfig config)
    {
        var stored = StoredBranchOf(config);
        return config.RepositoryUrl is { Length: > 0 } repo
               && services?.GetService<IGitSyncLineRefs>()?.LineBranch(repo, stored) is { Length: > 0 } line
            ? line
            : stored;
    }

    /// <summary>
    /// <paramref name="config"/> as this instance syncs it — its <see cref="GitHubSyncConfig.Branch"/>
    /// set to <see cref="BranchOf"/>. Read-side only: a config WRITE starts from the stored node, never
    /// from this, so the stored branch is never rewritten by the line.
    /// </summary>
    /// <param name="services">The services carrying <see cref="IGitSyncLineRefs"/>, or null.</param>
    /// <param name="config">The stored config, or null.</param>
    public static GitHubSyncConfig? Effective(IServiceProvider? services, GitHubSyncConfig? config)
    {
        if (config is null)
            return null;
        var branch = BranchOf(services, config);
        return string.Equals(branch, config.Branch, StringComparison.Ordinal) ? config : config with { Branch = branch };
    }

    /// <summary>
    /// The pure rule behind <see cref="IGitSyncLineRefs.LineBranch"/>: a stored branch that is the
    /// declaration's configured ref or one of its per-line refs moves to the ref declared for
    /// <paramref name="line"/>, else to the configured ref (a line nobody declared reads the configured
    /// ref, as the registry serves it). Any other stored branch is not the declaration's and answers
    /// null. A declaration with no per-line refs answers null — nothing to move between.
    /// </summary>
    /// <param name="storedBranch">The stored branch.</param>
    /// <param name="configuredRef">The source's configured ref; blank or <c>HEAD</c> reads as <see cref="DefaultBranch"/>.</param>
    /// <param name="lineRefs">Line (major) → ref.</param>
    /// <param name="line">This instance's line, or null when unknown.</param>
    public static string? Resolve(
        string storedBranch, string? configuredRef, IReadOnlyDictionary<string, string> lineRefs, string? line)
    {
        if (lineRefs.Count == 0 || string.IsNullOrWhiteSpace(storedBranch))
            return null;
        var configured = string.IsNullOrWhiteSpace(configuredRef)
                         || string.Equals(configuredRef.Trim(), "HEAD", StringComparison.OrdinalIgnoreCase)
            ? DefaultBranch
            : configuredRef.Trim();
        var stored = storedBranch.Trim();
        var named = string.Equals(stored, configured, StringComparison.OrdinalIgnoreCase)
                    || lineRefs.Values.Any(r => string.Equals(r?.Trim(), stored, StringComparison.OrdinalIgnoreCase));
        if (!named)
            return null;
        return line is not null && lineRefs.TryGetValue(line, out var lineRef) && !string.IsNullOrWhiteSpace(lineRef)
            ? lineRef.Trim()
            : configured;
    }
}
