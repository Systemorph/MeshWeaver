using System.Security.Claims;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Infrastructure;

public class UserContextMiddleware(RequestDelegate next, ILogger<UserContextMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Add user context to the message delivery properties
        var userContext = ExtractUserContext(context.User);
        var userService = context.RequestServices.GetRequiredService<PortalApplication>().Hub.ServiceProvider.GetRequiredService<AccessService>();
        if (userContext is not null)
        {
            logger.LogDebug("UserContext: ObjectId={ObjectId}, Name={Name}, Email={Email}, Roles=[{Roles}]",
                userContext.ObjectId, userContext.Name, userContext.Email, string.Join(", ", userContext.Roles ?? []));

            userService.SetContext(userContext);
            // Set circuit context for SignalR calls
            userService.SetCircuitContext(userContext);
        }
        else
        {
            logger.LogDebug("No authenticated user context found, clearing access context");
            // Clear both contexts so GetEffectiveUserId falls back to WellKnownUsers.Anonymous
            userService.SetContext(null);
            userService.SetCircuitContext(null);
        }
        // Continue the middleware pipeline
        await next(context);
    }


    private AccessContext? ExtractUserContext(ClaimsPrincipal user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return new AccessContext
        {
            Name = user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue("name") ?? string.Empty,
            ObjectId = user.FindFirstValue("preferred_username") ?? string.Empty,
            Email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? string.Empty,
            Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList()
        };
    }
}
