using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using MeshWeaver.Blazor.Infrastructure; // PortalApplication
using MeshWeaver.Messaging;             // AccessService / AccessContext
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

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
        serviceProvider.GetRequiredService<PortalApplication>()
            .Hub.ServiceProvider.GetRequiredService<AccessService>()
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

        // No await: pull IObservable up to the controller's return type. The
        // single bridge to Task happens at .ToTask(ct) — passing the
        // request's cancellation token so a client disconnect tears down the
        // reactive subscription instead of orphaning it.
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
            .ToTask(ct);
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

        return tokenService.GetTokensForUser(userId)
            .Select(tokens => (IActionResult)Ok(tokens))
            .FirstAsync()
            .ToTask(ct);
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

        return tokenService.GetTokensForUser(userId)
            .SelectMany(tokens => tokens.Any(t => t.NodePath == nodePath)
                ? tokenService.RevokeToken(nodePath)
                    .Select(success => success ? (IActionResult)Ok() : NotFound())
                : Observable.Return<IActionResult>(NotFound("Token not found or does not belong to you")))
            .FirstAsync()
            .ToTask(ct);
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
