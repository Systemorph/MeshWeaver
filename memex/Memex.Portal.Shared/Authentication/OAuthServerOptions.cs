namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Configuration of the portal's MCP OAuth authorization server
/// (<see cref="OAuthConnectController"/>), bound from <see cref="SectionName"/>
/// (environment form <c>Mcp__OAuth__MaxLiveCredentialsPerClient</c>).
/// </summary>
public class OAuthServerOptions
{
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "Mcp:OAuth";

    /// <summary>The default for <see cref="MaxLiveCredentialsPerClient"/>.</summary>
    public const int DefaultMaxLiveCredentialsPerClient = 5;

    /// <summary>
    /// How many OAuth-issued tokens one <c>(user, client_id)</c> pair may hold live at once. A fresh
    /// authorization keeps the newest this-many — itself included — and deletes the older ones,
    /// logging every eviction.
    ///
    /// <para>Not one, because one <c>client_id</c> is routinely shared by several processes: Claude
    /// Code stores its registration once per machine, so every Claude Code session on that machine
    /// presents the same <c>client_id</c>, and a one-per-client rule made each new sign-in revoke
    /// every sibling session's credential (#5074). Still bounded, because an unbounded count is the
    /// accumulation of year-long live keys #1493 removed.</para>
    ///
    /// <para>A value below 1 is treated as 1: the credential the exchange just handed out is never
    /// evicted by its own exchange.</para>
    /// </summary>
    public int MaxLiveCredentialsPerClient { get; set; } = DefaultMaxLiveCredentialsPerClient;
}
