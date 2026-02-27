using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// REST API for managing API tokens. All endpoints require cookie authentication
/// (users must be logged into the web UI to manage tokens).
/// </summary>
[ApiController]
[Route("api/tokens")]
[Authorize]
public class ApiTokenController(ApiTokenService tokenService) : ControllerBase
{
    /// <summary>
    /// Creates a new API token. Returns the raw token once — it cannot be retrieved again.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateToken([FromBody] CreateTokenRequest request)
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
            return Unauthorized("No user identity found");

        DateTimeOffset? expiresAt = request.ExpiresInDays > 0
            ? DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays.Value)
            : null;

        var (rawToken, node) = await tokenService.CreateTokenAsync(
            userId, userName, userEmail, request.Label ?? "API Token", expiresAt);

        return Ok(new CreateTokenResponse
        {
            RawToken = rawToken,
            NodePath = node.Path,
            Label = request.Label ?? "API Token",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
        });
    }

    /// <summary>
    /// Lists all tokens for the current user. Never returns raw tokens.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListTokens()
    {
        var userId = User.FindFirstValue("preferred_username")
                     ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? "";

        if (string.IsNullOrEmpty(userId))
            return Unauthorized("No user identity found");

        var tokens = await tokenService.GetTokensForUserAsync(userId);
        return Ok(tokens);
    }

    /// <summary>
    /// Revokes a token by its node path.
    /// </summary>
    [HttpDelete("{*nodePath}")]
    public async Task<IActionResult> RevokeToken(string nodePath)
    {
        // Verify the token belongs to the current user
        var userId = User.FindFirstValue("preferred_username")
                     ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? "";

        if (string.IsNullOrEmpty(userId))
            return Unauthorized("No user identity found");

        var tokens = await tokenService.GetTokensForUserAsync(userId);
        if (!tokens.Any(t => t.NodePath == nodePath))
            return NotFound("Token not found or does not belong to you");

        var success = await tokenService.RevokeTokenAsync(nodePath);
        return success ? Ok() : NotFound();
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
