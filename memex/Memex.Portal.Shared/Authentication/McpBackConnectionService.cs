using MeshWeaver.AI.Connect;   // IMcpBackConnection/McpConnectionInfo keep their ORIGINAL namespace in MeshWeaver.Mesh.Contract (#2398 forwarders)
using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Hosting.AspNetCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Automatic, token-based MCP back-connection provisioning — the portal-side implementation of
/// <see cref="IMcpBackConnection"/>. The co-hosted Claude Code / GitHub Copilot CLIs call
/// <see cref="EnsureForUser"/> at spawn time (every execution); on a cache miss this mints a
/// long-lived per-user MeshWeaver <c>ApiToken</c> via <see cref="ApiTokenService"/> — with NO manual
/// step — and returns the composed <c>{baseUrl}/api/mcp</c> URL (the PRIMARY endpoint path — see
/// <see cref="McpEndpointRoutes"/>; safe to use here because the back-connection targets THIS
/// portal, whose image carries the alias) plus the raw <c>mw_…</c> token to present
/// as <c>Authorization: Bearer</c>. Internal portal↔CLI↔MCP comms are therefore token-based and
/// scoped to the user's own permissions (the ApiToken carries the user's roles).
///
/// <para>The per-user token is cached on this singleton's instance dictionary (NEVER static — it
/// dies with the host) for the process lifetime; a fresh replica mints its own (the prior token
/// stays valid).</para>
///
/// <para>🚨 <b>A cached token is VALIDATED before it is reused</b>, and that is the only thing that
/// makes a revocation take effect. This paragraph used to claim "a revoked token surfaces as a 401
/// on the next MCP call, which the auth-on-exception path turns into a re-mint" — there was no such
/// path (#2302 finding 6). Neither CLI client reports a subsequent <c>/mcp</c> 401 back to this
/// service, so nothing evicted the entry and mesh access stayed broken until the process restarted.
/// An <c>Invalidate(userId)</c> contract would not have helped for the same reason: there is no
/// caller to invoke it. Validation reads the token node fresh, so asking here is meaningful.</para>
///
/// <para>Only an <b>Invalid</b> verdict evicts. <b>Unavailable</b> deliberately does not: "we could
/// not find out" is not "it was revoked" (#637), and minting on a storage blip would fill the
/// user's token list with orphans exactly when the mesh is already unwell.</para>
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

    /// <summary>
    /// Drops a cached token the store has judged INVALID and mints a replacement, so a revocation
    /// takes effect without a portal restart.
    /// </summary>
    private IObservable<McpConnectionInfo?> EvictAndMint(
        string userId, string? userName, string? userEmail, string mcpUrl, string? reason)
    {
        tokensByUser.TryRemove(userId, out _);
        logger.LogInformation(
            "Cached MCP back-connection token for user {UserId} is no longer valid ({Reason}) — "
            + "evicted and re-minting.", userId, reason ?? "no reason given");
        return Mint(userId, userName, userEmail, mcpUrl);
    }

    public IObservable<McpConnectionInfo?> EnsureForUser(string userId, string? userName = null, string? userEmail = null)
    {
        var baseUrl = mcpConfig.Value?.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(userId))
            return Observable.Return<McpConnectionInfo?>(null);

        var mcpUrl = $"{baseUrl!.TrimEnd('/')}{McpEndpointRoutes.PrimaryEndpoint}";

        // Reuse the cached token — but VALIDATE it first (#2302 finding 6). The cache had no
        // eviction path at all: once a token was cached it was returned for the life of the
        // process, so REVOKING it did not take effect. Neither CLI client reports a subsequent
        // /mcp 401 back here, so an `Invalidate(userId)` contract would have been a method nobody
        // ever calls — the revocation would still never land. Validation reads the token node
        // fresh (no cache), which is what makes checking it here meaningful rather than circular.
        if (tokensByUser.TryGetValue(userId, out var cached))
            return tokenService.Validate(cached)
                .SelectMany(verdict => verdict.Status switch
                {
                    // 🚨 Only INVALID evicts. Unavailable must NOT: "we could not find out" is not
                    // "it was revoked" (#637), and treating a storage blip as revocation would mint
                    // a fresh token on every transient failure — filling the user's token list with
                    // orphans and doing it precisely when the mesh is already unwell. A cached token
                    // that is actually revoked simply fails at /mcp on that attempt, which is the
                    // same outcome as today and is recoverable on the next call.
                    TokenValidationStatus.Invalid => EvictAndMint(userId, userName, userEmail, mcpUrl, verdict.Reason),
                    _ => Observable.Return<McpConnectionInfo?>(new McpConnectionInfo(mcpUrl, cached)),
                })
                .Catch((Exception ex) =>
                {
                    // Validation itself faulting is an availability failure, not a verdict: keep
                    // serving the cached token rather than minting on every fault.
                    logger.LogWarning(ex,
                        "Could not validate the cached MCP back-connection token for user {UserId}; "
                        + "reusing it. If it has been revoked the /mcp call will refuse it.", userId);
                    return Observable.Return<McpConnectionInfo?>(new McpConnectionInfo(mcpUrl, cached));
                });

        // Cache miss → mint automatically.
        return Mint(userId, userName, userEmail, mcpUrl);
    }

    /// <summary>
    /// Mints a back-connection token and caches it. CreateToken self-elevates for the global index
    /// write; the user-scoped node is created under the calling user's AccessContext (active at
    /// spawn).
    /// </summary>
    private IObservable<McpConnectionInfo?> Mint(
        string userId, string? userName, string? userEmail, string mcpUrl)
    {
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
