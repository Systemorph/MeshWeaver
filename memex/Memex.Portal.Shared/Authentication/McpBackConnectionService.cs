using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.AI.Connect;
using MeshWeaver.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Automatic, token-based MCP back-connection provisioning — the portal-side implementation of
/// <see cref="IMcpBackConnection"/>. The co-hosted Claude Code / GitHub Copilot CLIs call
/// <see cref="EnsureForUser"/> at spawn time (every execution); on a cache miss this mints a
/// long-lived per-user MeshWeaver <c>ApiToken</c> via <see cref="ApiTokenService"/> — with NO manual
/// step — and returns the composed <c>{baseUrl}/mcp</c> URL plus the raw <c>mw_…</c> token to present
/// as <c>Authorization: Bearer</c>. Internal portal↔CLI↔/mcp comms are therefore token-based and
/// scoped to the user's own permissions (the ApiToken carries the user's roles).
///
/// <para>The per-user token is cached on this singleton's instance dictionary (NEVER static — it
/// dies with the host) for the process lifetime; a fresh replica mints its own (the prior token
/// stays valid). A revoked token surfaces as a 401 on the next <c>/mcp</c> call, which the
/// auth-on-exception path turns into a re-mint.</para>
/// </summary>
internal sealed class McpBackConnectionService : IMcpBackConnection
{
    private readonly ApiTokenService tokenService;
    private readonly IOptions<McpConfiguration> mcpConfig;
    private readonly ILogger<McpBackConnectionService> logger;

    // Instance (not static) — lifetime == the portal host. userId → raw mw_ token.
    private readonly ConcurrentDictionary<string, string> tokensByUser = new(StringComparer.Ordinal);

    public McpBackConnectionService(
        ApiTokenService tokenService,
        IOptions<McpConfiguration> mcpConfig,
        ILogger<McpBackConnectionService> logger)
    {
        this.tokenService = tokenService;
        this.mcpConfig = mcpConfig;
        this.logger = logger;
    }

    public IObservable<McpConnectionInfo?> EnsureForUser(string userId, string? userName = null, string? userEmail = null)
    {
        var baseUrl = mcpConfig.Value?.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(userId))
            return Observable.Return<McpConnectionInfo?>(null);

        var mcpUrl = $"{baseUrl!.TrimEnd('/')}/mcp";

        // Reuse the cached token (long-lived, no expiry) — cheap hot path, no mint.
        if (tokensByUser.TryGetValue(userId, out var cached))
            return Observable.Return<McpConnectionInfo?>(new McpConnectionInfo(mcpUrl, cached));

        // Cache miss → mint automatically. CreateToken self-elevates for the global index write;
        // the user-scoped node is created under the calling user's AccessContext (active at spawn).
        return tokenService
            .CreateToken(userId, userName ?? userId, userEmail ?? string.Empty,
                label: "MCP back-connection (auto)", expiresAt: null)
            .Select(result =>
            {
                tokensByUser[userId] = result.RawToken;
                logger.LogInformation("Auto-minted MCP back-connection token for user {UserId}", userId);
                return (McpConnectionInfo?)new McpConnectionInfo(mcpUrl, result.RawToken);
            })
            .Catch((Exception ex) =>
            {
                // Fail soft: the CLI runs without mesh access rather than failing the chat.
                logger.LogWarning(ex,
                    "Could not provision MCP back-connection for user {UserId}; co-hosted CLI will run without mesh access.", userId);
                return Observable.Return<McpConnectionInfo?>(null);
            });
    }
}
