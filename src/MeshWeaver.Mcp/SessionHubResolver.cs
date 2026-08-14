using System.Security.Claims;
using MeshWeaver.Hosting;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mcp;

/// <summary>
/// Resolves a per-caller hosted hub at <c>portal/{prefix}-{sessionId}</c> for
/// transports that expose mesh operations to external clients (MCP, REST).
///
/// <para>
/// Both transports must share this helper so their routing semantics stay
/// identical — same address shape, same wiring. This class is the HTTP half:
/// it derives the session id from the request and hands off to
/// <see cref="SessionHubFactory"/>, which owns the hub materialisation and is
/// shared with the headless surfaces that have no <see cref="HttpContext"/>.
/// </para>
///
/// <para>
/// 🚨 This helper NEVER returns the root <c>mesh/{id}</c> hub. That hub is transient
/// routing infrastructure, not a call target: request-shaped work issued on it never
/// answers — the caller's queue stays empty at <c>RunLevel=Started</c> while the pending
/// callback runs out its timeout, which reads misleadingly like "the target never
/// replied". Every API surface (MCP, REST, gRPC, CLI) issues on a portal hub.
/// </para>
/// </summary>
public static class SessionHubResolver
{
    /// <summary>
    /// Materialises (or reuses) a hosted hub for the calling user × protocol session
    /// at address <c>portal/{prefix}-{sessionId}-{instance}</c>. When no caller / session
    /// can be derived from <paramref name="ctx"/>, falls back to the process-stable shared
    /// anonymous portal hub — NEVER to <paramref name="rootHub"/>.
    /// </summary>
    /// <param name="rootHub">
    /// The hub that HOSTS the returned child (typically the root mesh hub). Used only as the
    /// hosting parent and as a service-provider handle — never as the hub a call is issued on.
    /// </param>
    /// <param name="ctx">Current HTTP context (claims + Mcp-Session-Id header).</param>
    /// <param name="prefix">Transport label used in the address segment: <c>"mcp"</c>, <c>"api"</c>, …</param>
    /// <param name="logger">Diagnostic sink.</param>
    public static IMessageHub ResolveSessionHub(
        IMessageHub rootHub,
        HttpContext? ctx,
        string prefix,
        ILogger logger)
    {
        var sessionId = ResolveSessionId(ctx);
        if (sessionId is null)
        {
            // 🚨 NEVER return rootHub here. It is the root mesh/{id} hub, on which
            // request-shaped work never answers. Anonymous callers share one stable
            // portal hub instead: it has the routing + lifetime mesh operations need.
            sessionId = SessionHubFactory.AnonymousSession;
            logger.LogWarning(
                "No {Prefix} session id resolvable from request — using the shared anonymous "
                + "portal hub. Callers that authenticate (or send an Mcp-Session-Id header) "
                + "get their own isolated session hub.",
                prefix);
        }

        return SessionHubFactory.Resolve(rootHub, prefix, sessionId, logger);
    }

    /// <summary>
    /// Derives a stable session identifier from <paramref name="ctx"/> by combining
    /// the authenticated caller id with the optional <c>Mcp-Session-Id</c> header.
    /// Returns <c>null</c> when neither is present.
    /// </summary>
    public static string? ResolveSessionId(HttpContext? ctx)
    {
        if (ctx is null) return null;

        // Prefer the standard MCP protocol header. REST callers can set it too
        // for stable per-connection session scoping; otherwise the caller id alone
        // identifies the session. Long values (e.g. the self-contained ids some
        // stateless transports mint) are hashed to a short stable hex so the
        // resulting hub address / grain key stays compact and well-formed.
        var protocolSession = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        if (protocolSession is { Length: > 48 })
            protocolSession = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(protocolSession)))[..16]
                .ToLowerInvariant();

        var callerId = ctx.User?.FindFirst("oid")?.Value
                    ?? ctx.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? ctx.User?.FindFirst(ClaimTypes.Email)?.Value
                    ?? ctx.User?.Identity?.Name;

        if (!string.IsNullOrEmpty(callerId) && !string.IsNullOrEmpty(protocolSession))
            return $"{Sanitize(callerId)}-{Sanitize(protocolSession)}";
        if (!string.IsNullOrEmpty(callerId))
            return Sanitize(callerId);
        if (!string.IsNullOrEmpty(protocolSession))
            return $"anon-{Sanitize(protocolSession)}";
        return null;
    }

    /// <summary>
    /// Sanitises a free-form id into a safe address segment: letters, digits, '-', '_'.
    /// Everything else is replaced with '-' so hosted-hub grain-key lookup stays well-formed.
    /// </summary>
    public static string Sanitize(string s) => SessionHubFactory.Sanitize(s);
}
