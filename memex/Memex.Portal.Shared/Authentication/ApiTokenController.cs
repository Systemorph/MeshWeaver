using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Hosting.AspNetCore.Portal; // PortalApplication
using MeshWeaver.Messaging;             // AccessService / AccessContext
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// REST API for managing API tokens. All endpoints require cookie authentication
/// (users must be logged into the web UI to manage tokens).
/// </summary>
[ApiController]
[Route("api/tokens")]
[Authorize]
public class ApiTokenController(IServiceProvider serviceProvider) : ControllerBase
{
    private ApiTokenService tokenService => serviceProvider.GetRequiredService<ApiTokenService>();

    /// <summary>
    /// Where a fault that arrives AFTER the action's response has already settled goes. Every
    /// <see cref="ReactiveCompletion.ObserveCompletion{T}(System.IObservable{T}, System.Action{System.Exception}, System.Threading.CancellationToken)"/> bridge below needs one — a discarded
    /// late fault is half of what that method exists to remove.
    /// </summary>
    private ILogger LateFaultLogger => serviceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger(typeof(ApiTokenController));

    /// <summary>
    /// The mesh-resolved identity for this request, as stamped on the portal
    /// hub's <see cref="AccessService"/> by <c>UserContextMiddleware</c>.
    /// <para>
    /// 🚨 We deliberately do NOT read <c>preferred_username</c> off the claims
    /// principal here. Entra/OIDC fill that claim with the UPN, which is the
    /// user's <b>email</b> (e.g. <c>rbuergi@systemorph.com</c>). The mesh
    /// partition key is the User node's Id (e.g. <c>rbuergi</c>), and the
    /// middleware already does the email→User resolution + normalisation (and
    /// refuses email-shaped ids). Passing the raw email through as the token's
    /// userId routed the token node AND its <c>_Access</c> self-scope into a
    /// non-existent <c>{email}</c> partition — which 401'd every freshly-minted
    /// token once the router stopped lazy-creating schemas. Reading the
    /// already-resolved context guarantees the token lands in exactly the
    /// partition the user's other data lives in.
    /// </para>
    /// </summary>
    private AccessContext? CurrentUser =>
        // Portal hub when the Blazor shell registered one; the mesh root hub on a next-only
        // portal (Features:Gui:Blazor=false). AccessService is the mesh-wide singleton either
        // way — see UserContextMiddleware, which stamps the identity this reads.
        (serviceProvider.GetService<PortalApplication>()?.Hub
         ?? serviceProvider.GetRequiredService<IMessageHub>())
            .ServiceProvider.GetRequiredService<AccessService>()
            .Context;

    /// <summary>
    /// The mesh User.Id for the current request, or null if the request has no
    /// resolved (non-email) mesh identity — in which case token operations must
    /// be refused rather than routed to a parallel <c>{email}</c> partition.
    /// </summary>
    private static string? MeshUserId(AccessContext? user)
    {
        var id = user?.ObjectId;
        return string.IsNullOrEmpty(id) || id.Contains('@') ? null : id;
    }

    /// <summary>
    /// Creates a new API token. Returns the raw token once — it cannot be retrieved again.
    /// </summary>
    [HttpPost]
    public Task<IActionResult> CreateToken([FromBody] CreateTokenRequest request, CancellationToken ct)
    {
        var user = CurrentUser;
        var userId = MeshUserId(user);
        if (userId is null)
            return Task.FromResult<IActionResult>(Unauthorized("No user identity found"));

        var userName = user!.Name ?? "";
        var userEmail = user.Email ?? "";

        DateTimeOffset? expiresAt = request.ExpiresInDays > 0
            ? DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays.Value)
            : null;

        var label = request.Label ?? "API Token";

        // No await: pull IObservable up to the controller's return type. The single bridge to Task
        // happens at .ObserveCompletion(…, ct) — never Rx's .ToTask(), which resumes the awaiter
        // INLINE on the signalling thread (a mesh hub's action block here) and is forbidden since
        // 2026-08-30. The request's cancellation token is passed so a client disconnect stops the
        // WAIT. 🚨 It does not dispose the subscription — ObserveCompletion deliberately keeps its
        // error arm attached so a late fault is reported rather than lost, which means the mesh
        // work continues to completion after an abort. That is the right trade for a token
        // creation (the token is either created or not; abandoning it half-way would be worse),
        // but it is NOT teardown, and saying so here would be a promise the code does not keep.
        var lateFaultLogger = LateFaultLogger;
        return tokenService.CreateToken(userId, userName, userEmail, label, expiresAt)
            .Select(creation => (IActionResult)Ok(new CreateTokenResponse
            {
                RawToken = creation.RawToken,
                NodePath = creation.Node.Path,
                Label = label,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiresAt,
            }))
            .FirstAsync()
            .ObserveCompletion(
                ex => lateFaultLogger.LogWarning(ex,
                    "Minting an API token for {UserId} faulted after the response had already been sent",
                    userId),
                ct)!;
    }

    /// <summary>
    /// Lists all tokens for the current user. Never returns raw tokens.
    /// </summary>
    [HttpGet]
    public Task<IActionResult> ListTokens(CancellationToken ct)
    {
        var userId = MeshUserId(CurrentUser);
        if (userId is null)
            return Task.FromResult<IActionResult>(Unauthorized("No user identity found"));

        var lateFaultLogger = LateFaultLogger;
        return tokenService.GetTokensForUser(userId)
            .Select(tokens => (IActionResult)Ok(tokens))
            .FirstAsync()
            .ObserveCompletion(
                ex => lateFaultLogger.LogWarning(ex,
                    "Listing API tokens for {UserId} faulted after the response had already been sent",
                    userId),
                ct)!;
    }

    /// <summary>
    /// Revokes a token by its node path.
    /// </summary>
    [HttpDelete("{*nodePath}")]
    public Task<IActionResult> RevokeToken(string nodePath, CancellationToken ct)
    {
        var userId = MeshUserId(CurrentUser);
        if (userId is null)
            return Task.FromResult<IActionResult>(Unauthorized("No user identity found"));

        var lateFaultLogger = LateFaultLogger;
        return tokenService.GetTokensForUser(userId)
            .SelectMany(tokens => tokens.Any(t => t.NodePath == nodePath)
                ? tokenService.RevokeToken(nodePath)
                    .Select(success => success ? (IActionResult)Ok() : NotFound())
                : Observable.Return<IActionResult>(NotFound("Token not found or does not belong to you")))
            .FirstAsync()
            .ObserveCompletion(
                ex => lateFaultLogger.LogWarning(ex,
                    "Revoking API token '{NodePath}' faulted after the response had already been sent",
                    nodePath),
                ct)!;
    }
}

public record CreateTokenRequest
{
    public string? Label { get; init; }
    public int? ExpiresInDays { get; init; }
}

public record CreateTokenResponse
{
    public string RawToken { get; init; } = "";
    public string NodePath { get; init; } = "";
    public string Label { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}
