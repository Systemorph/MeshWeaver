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
        string.IsNullOrEmpty(token) ? null : new Dictionary<string, string> { ["GW_TOKEN"] = token };
}
