using Microsoft.Extensions.Configuration;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// How many OAuth-issued tokens one <c>(user, client_id)</c> pair may hold live at once
/// (policy <c>oauth-bounded-live-credentials</c>). A fresh authorization keeps the newest this-many —
/// itself included — and evicts the older ones, logging every eviction.
///
/// <para>Not one, because one <c>client_id</c> is routinely shared by several processes: Claude Code
/// stores its registration once per machine, so every Claude Code session on that machine presents the
/// same <c>client_id</c>, and a one-per-client rule made each new sign-in revoke every sibling
/// session's credential (#5074). Still bounded, because an unbounded count is the accumulation of
/// year-long live keys #1493 removed.</para>
///
/// <para>Read from <see cref="IConfiguration"/> under <see cref="ConfigKey"/> (environment form
/// <c>Mcp__OAuth__MaxLiveCredentialsPerClient</c>). Absent, malformed or no configuration at all means
/// <see cref="Default"/>; a value below 1 is treated as 1, so an exchange never evicts the credential
/// it just handed out.</para>
/// </summary>
public static class OAuthCredentialBound
{
    /// <summary>The configuration key.</summary>
    public const string ConfigKey = "Mcp:OAuth:MaxLiveCredentialsPerClient";

    /// <summary>The bound when nothing is configured.</summary>
    public const int Default = 5;

    /// <summary>The effective bound for <paramref name="configuration"/> (null means the default).</summary>
    public static int From(IConfiguration? configuration) =>
        int.TryParse(configuration?[ConfigKey], out var configured)
            ? Math.Max(1, configured)
            : Default;
}
