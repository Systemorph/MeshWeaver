namespace MeshWeaver.AI.Connect;

/// <summary>
/// The coordinates a co-hosted CLI needs to call the portal's MCP endpoint AS THE USER:
/// the fully-composed <c>{baseUrl}/mcp</c> URL and the Bearer token to present.
/// </summary>
public sealed record McpConnectionInfo(string McpUrl, string BearerToken);

/// <summary>
/// Automatically provisions the per-user MCP back-connection used by the co-hosted Claude Code /
/// GitHub Copilot CLIs so the mesh is their workspace. Creating the token + wiring is automatic —
/// there is NO manual step: the co-hosted chat clients call <see cref="EnsureForUser"/> at spawn
/// time, every execution, and inject the result as a per-spawn HTTP MCP server with a
/// <c>Authorization: Bearer</c> header (the token-based pattern for internal comms).
///
/// <para>The implementation lives in the portal (mints/reuses a MeshWeaver ApiToken via
/// <c>ApiTokenService</c>, stores it encrypted so it survives across replicas, and resolves the
/// portal's own base URL). It is consumed here in the AI layer through this interface so the
/// chat clients never reference the portal assembly.</para>
/// </summary>
public interface IMcpBackConnection
{
    /// <summary>
    /// Ensure a usable MCP Bearer token exists for <paramref name="userId"/> — mint one if missing
    /// or invalid, otherwise reuse the stored one — and return the <see cref="McpConnectionInfo"/>
    /// to inject for this spawn. Idempotent and cheap on the hot path (reuse is a decrypt, not a
    /// mint). Returns <c>null</c> when no back-connection can be established (e.g. the portal base
    /// URL is unknown); the CLI then runs without mesh access rather than failing the chat.
    /// </summary>
    IObservable<McpConnectionInfo?> EnsureForUser(string userId, string? userName = null, string? userEmail = null);
}
