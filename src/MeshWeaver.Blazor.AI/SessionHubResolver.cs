using System.Security.Claims;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.AI;

/// <summary>
/// Resolves a per-caller hosted hub at <c>portal/{prefix}-{sessionId}</c> for
/// transports that expose mesh operations to external clients (MCP, REST).
///
/// <para>
/// Both transports must share this helper so their routing semantics stay
/// identical — same address shape, same <see cref="DataExtensions.AddData(MessageHubConfiguration)"/>
/// + <see cref="IRoutingService.RegisterStream(IMessageHub)"/> wiring, same fallback to the
/// root hub when no session is resolvable.
/// </para>
/// </summary>
public static class SessionHubResolver
{
    /// <summary>
    /// Materialises (or reuses) a hosted hub for the calling user × protocol session
    /// at address <c>portal/{prefix}-{sessionId}</c>. Falls back to <paramref name="rootHub"/>
    /// if no caller / session can be derived from <paramref name="ctx"/>.
    /// </summary>
    /// <param name="rootHub">Portal-level hub from which to host the child.</param>
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
            logger.LogWarning(
                "No {Prefix} session id resolvable from request — falling back to root hub. "
                + "Some routing rules (kernel dispatch, etc.) will not fire.",
                prefix);
            return rootHub;
        }

        var routingService = rootHub.ServiceProvider.GetRequiredService<IRoutingService>();
        var address = AddressExtensions.CreatePortalAddress($"{prefix}-{sessionId}");
        logger.LogInformation("Materialising {Prefix} session hub at {Address}", prefix, address);

        // AddData() ensures the session hub has its own IWorkspace so MeshOperations.Compile
        // can subscribe to the NodeType MeshNode stream and Update its compilationStatus
        // through the canonical write path. RegisterStream wires routing so every response
        // (Get / Search / Patch / ExecuteScript / …) lands back here.
        return rootHub.GetHostedHub(
            address,
            sessionConfig => sessionConfig
                .AddData()
                .WithInitialization(hub =>
                    hub.RegisterForDisposal(routingService.RegisterStream(hub))),
            HostedHubCreation.Always)
            ?? throw new InvalidOperationException(
                $"Failed to materialise {prefix} session hub at {address}.");
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
        // identifies the session.
        var protocolSession = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault();

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
    public static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray();
        return new string(chars);
    }
}
