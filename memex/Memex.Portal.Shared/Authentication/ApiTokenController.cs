using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using System.Threading;
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
    /// Creates a new API token. Returns the raw token once — it cannot be retrieved again.
    /// </summary>
    [HttpPost]
    public Task<IActionResult> CreateToken([FromBody] CreateTokenRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue("preferred_username")
                     ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? "";
        var userName = User.FindFirstValue(ClaimTypes.Name)
                       ?? User.FindFirstValue("name")
                       ?? "";
        var userEmail = User.FindFirstValue(ClaimTypes.Email)
                        ?? User.FindFirstValue("email")
                        ?? "";

        if (string.IsNullOrEmpty(userId))
            return Task.FromResult<IActionResult>(Unauthorized("No user identity found"));

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
        var userId = User.FindFirstValue("preferred_username")
                     ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? "";

        if (string.IsNullOrEmpty(userId))
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
        var userId = User.FindFirstValue("preferred_username")
                     ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? "";

        if (string.IsNullOrEmpty(userId))
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
