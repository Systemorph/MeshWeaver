using System.Security.Claims;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Infrastructure;

public class UserContextMiddleware(RequestDelegate next, ILogger<UserContextMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var hub = context.RequestServices.GetRequiredService<PortalApplication>().Hub;
        var userService = hub.ServiceProvider.GetRequiredService<AccessService>();

        // Try OAuth first (browser sessions), then Bearer token (MCP / API clients)
        var userContext = ExtractUserContext(context.User)
                          ?? await ExtractFromBearerTokenAsync(context.Request, hub);

        if (userContext is not null)
        {
            // If the circuit already has a context for this same user, reuse it
            // (avoids mesh lookup on every request — only needed once per circuit).
            var existing = userService.Context;
            if (existing is not null && existing.Email == userContext.Email)
            {
                userService.SetContext(existing);
                await next(context);
                return;
            }

            // First request for this user in this circuit — look up the mesh User node
            // to get the system name (overrides the claim name).
            if (!string.IsNullOrEmpty(userContext.Email))
            {
                var meshUser = await TryLoadMeshUserAsync(userContext.Email, hub);
                if (meshUser is not null && !string.IsNullOrEmpty(meshUser.Name))
                {
                    userContext = userContext with { Name = meshUser.Name };
                }
            }

            logger.LogDebug("UserContext: ObjectId={ObjectId}, Name={Name}, Email={Email}, Roles=[{Roles}]",
                userContext.ObjectId, userContext.Name, userContext.Email, string.Join(", ", userContext.Roles ?? []));

            userService.SetContext(userContext);
            userService.SetCircuitContext(userContext);
        }
        else
        {
            logger.LogDebug("No authenticated user context found, clearing access context");
            userService.SetContext(null);
            userService.SetCircuitContext(null);
        }

        await next(context);
    }

    /// <summary>
    /// Validates a Bearer token by sending a ValidateTokenRequest to the token's hub address.
    /// The ApiToken node type handler validates hash/expiry/revocation and returns user info.
    /// This gives the token holder the exact same access rights as the user who created the token.
    /// </summary>
    private async Task<AccessContext?> ExtractFromBearerTokenAsync(HttpRequest request, IMessageHub hub)
    {
        var authHeader = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var rawToken = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(ValidateTokenRequest.TokenPrefix))
            return null;

        var response = await ValidateTokenViaHubAsync(rawToken, hub);
        if (response is not { Success: true })
            return null;

        return new AccessContext
        {
            ObjectId = response.UserEmail!,
            Name = response.UserName ?? "",
            Email = response.UserEmail!,
        };
    }

    /// <summary>
    /// Sends a ValidateTokenRequest to the ApiToken node's hub and returns the response.
    /// The request is routed to ApiToken/{hashPrefix} where the handler validates the token.
    /// Public so tests can use the same flow.
    /// </summary>
    public static async Task<ValidateTokenResponse?> ValidateTokenViaHubAsync(string rawToken, IMessageHub hub)
    {
        try
        {
            var hash = ValidateTokenRequest.HashToken(rawToken);
            var hashPrefix = hash[..12];
            var tokenAddress = new Address("ApiToken", hashPrefix);

            var response = await hub.AwaitResponse(
                new ValidateTokenRequest(rawToken),
                o => o.WithTarget(tokenAddress));
            return response.Message;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Queries the mesh for a User node whose email matches the authenticated user's email.
    /// Uses ImpersonateAsHub scope since the user context hasn't been set yet at this point.
    /// Returns the MeshNode if found, so we can use its Name (from the system) instead of the claim.
    /// </summary>
    private ValueTask<MeshNode?> TryLoadMeshUserAsync(string email, IMessageHub hub)
    {
        try
        {
            var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
            using (accessService.ImpersonateAsHub(hub))
            {
                var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
                return meshService.QueryAsync<MeshNode>(
                    $"nodeType:User namespace:User content.email:\"{email}\" limit:1").FirstOrDefaultAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load mesh user for email {Email}", email);
            return default;
        }
    }

    private AccessContext? ExtractUserContext(ClaimsPrincipal user)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        var email = user.FindFirstValue(ClaimTypes.Email)
                    ?? user.FindFirstValue("email")
                    ?? user.FindFirstValue("preferred_username")
                    ?? string.Empty;

        return new AccessContext
        {
            Name = user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue("name") ?? string.Empty,
            ObjectId = email,
            Email = email,
            Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList()
        };
    }
}
