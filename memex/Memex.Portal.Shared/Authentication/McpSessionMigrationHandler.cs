using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Makes stateful MCP sessions pod-independent across horizontally-scaled portal replicas, the
/// same way <see cref="OAuthCodeStore"/> made the OAuth code exchange replica-safe.
///
/// <para>
/// The MCP Streamable-HTTP transport keeps per-session state in the process that served the
/// <c>initialize</c> request. With more than one silo the MCP client — which carries no affinity
/// cookie (unlike the Blazor browser) — can send a follow-up request for an established
/// <c>Mcp-Session-Id</c> to a replica that never saw the session, and the SDK rejects it 404
/// ("Session not found"). This is the "works on one silo, breaks on two" bug.
/// </para>
///
/// <para>
/// The framework's <c>AddMeshMcp</c> calls <c>WithHttpTransport()</c> with a null configure
/// callback, so the SDK resolves an <see cref="ISessionMigrationHandler"/> from DI. On
/// <c>initialize</c> this one persists the session's <see cref="InitializeRequestParams"/> to a
/// mesh node via <see cref="McpSessionStore"/> (keyed by the id the client caches and echoes on
/// every request); when a request arrives on a replica that doesn't know the id, the SDK calls
/// <see cref="AllowSessionMigrationAsync"/>, which re-hydrates it here — owner-checked, so only
/// the original authenticated caller may re-bind the session. Registered as a singleton in
/// <c>ConfigureMemexServices</c> next to <see cref="OAuthCodeStore"/>.
/// </para>
/// </summary>
internal sealed class McpSessionMigrationHandler(
    McpSessionStore store,
    ILogger<McpSessionMigrationHandler> logger) : ISessionMigrationHandler
{
    public async ValueTask OnSessionInitializedAsync(
        HttpContext context,
        string sessionId,
        InitializeRequestParams initializeParams,
        CancellationToken cancellationToken)
    {
        var owner = ResolveOwner(context);
        try
        {
            var json = JsonSerializer.Serialize(initializeParams, McpJsonUtilities.DefaultOptions);
            await store.StoreSession(sessionId, owner, json).ToTask(cancellationToken);
            logger.LogInformation(
                "Persisted MCP session {SessionId} for cross-replica migration (owner {Owner})",
                sessionId, owner);
        }
        catch (Exception ex)
        {
            // Best-effort: a failed persist must not fail the initialize handshake. The only
            // consequence is that this session can't migrate — a follow-up on another replica
            // would 404 and the client re-initializes, i.e. the pre-fix behaviour.
            logger.LogWarning(ex,
                "Failed to persist MCP session {SessionId} for migration (owner {Owner})",
                sessionId, owner);
        }
    }

    public async ValueTask<InitializeRequestParams?> AllowSessionMigrationAsync(
        HttpContext context,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await store.ReadSession(sessionId).ToTask(cancellationToken);
            if (entry is null)
                return null; // unknown / expired → the SDK returns 404, which is correct

            var caller = ResolveOwner(context);
            if (!string.Equals(entry.Owner, caller, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Refused MCP session migration {SessionId}: caller {Caller} is not owner {Owner}",
                    sessionId, caller, entry.Owner);
                return null;
            }

            logger.LogInformation("Migrated MCP session {SessionId} to this replica for {Owner}", sessionId, caller);
            return JsonSerializer.Deserialize<InitializeRequestParams>(
                entry.InitializeParamsJson, McpJsonUtilities.DefaultOptions);
        }
        catch (Exception ex)
        {
            // A read/deserialize failure rejects the migration (→ 404, client re-initializes)
            // rather than throwing into the transport.
            logger.LogWarning(ex, "MCP session migration {SessionId} failed; rejecting", sessionId);
            return null;
        }
    }

    private static string ResolveOwner(HttpContext context) =>
        context.User.FindFirst("oid")?.Value
        ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? context.User.FindFirst(ClaimTypes.Email)?.Value
        ?? context.User.Identity?.Name
        ?? string.Empty;
}
