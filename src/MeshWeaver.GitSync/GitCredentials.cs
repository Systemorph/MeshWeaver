namespace MeshWeaver.GitSync;

/// <summary>
/// The ONE way a GitHub token reaches the <c>git</c> CLI: an inline credential helper that reads
/// it from the <c>GW_TOKEN</c> environment variable — the secret never appears in argv (visible
/// to <c>ps</c>) and never persists in <c>.git/config</c>. Shared by every git-protocol caller
/// (<see cref="GitWorkingTreeService"/>, <see cref="GitProtocolRepoClient"/>).
/// </summary>
internal static class GitCredentials
{
    /// <summary>The credential-helper config args that read the token from <c>$GW_TOKEN</c>.
    /// Empty when there is no token (anonymous access to a public remote).</summary>
    public static IReadOnlyList<string> AuthArgs(string? token) => string.IsNullOrEmpty(token)
        ? []
        : [
            // Clear any inherited helper (system credential store), then install ours.
            "-c", "credential.helper=",
            "-c", "credential.helper=!f() { test \"$1\" = get && printf 'username=x-access-token\\npassword=%s\\n' \"$GW_TOKEN\"; }; f",
          ];

    /// <summary>The environment carrying the token for <see cref="AuthArgs"/>'s helper.</summary>
    public static IReadOnlyDictionary<string, string>? AuthEnv(string? token) =>
        string.IsNullOrEmpty(token)
            ? null
            : System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .Add("GW_TOKEN", token);

    /// <summary>
    /// The git args + environment that authenticate against <paramref name="remoteUrl"/>.
    /// <list type="bullet">
    ///   <item>Azure Repos (MeshWeaver#5248): the Entra token travels as an
    ///     <c>Authorization: Bearer</c> header — the form Azure DevOps documents for Entra tokens —
    ///     set through git's <c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_n</c>/<c>GIT_CONFIG_VALUE_n</c>
    ///     environment, so it is neither in argv nor written to <c>.git/config</c>. The inherited
    ///     credential helper is cleared so git never falls back to a stored credential.</item>
    ///   <item>every other remote: the existing <c>$GW_TOKEN</c> credential helper, unchanged.</item>
    /// </list>
    /// </summary>
    public static (IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env) ForRemote(
        string remoteUrl, string? token)
    {
        if (string.IsNullOrEmpty(token) || !GitRepositoryProvider.IsAzureRepos(remoteUrl))
            return (AuthArgs(token), AuthEnv(token));
        return (
            ["-c", "credential.helper="],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .Add("GIT_CONFIG_COUNT", "1")
                .Add("GIT_CONFIG_KEY_0", "http.extraHeader")
                .Add("GIT_CONFIG_VALUE_0", "Authorization: Bearer " + token));
    }
}
