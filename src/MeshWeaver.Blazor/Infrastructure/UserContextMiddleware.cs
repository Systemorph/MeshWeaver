using System.Security.Claims;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Infrastructure;

public class UserContextMiddleware(RequestDelegate next)
{
    private const string UserContextKey = "UserContext";

    public async Task InvokeAsync(HttpContext context)
    {

        // Add user context to the message delivery properties
        var userContext = ExtractUserContext(context.User);
        if (userContext is not null)
        {
            var userService = context.RequestServices.GetRequiredService<PortalApplication>().Hub.ServiceProvider.GetRequiredService<UserService>();
            userService.SetContext(userContext);
        }
        // Continue the middleware pipeline
        await next(context);
    }


    private UserContext ExtractUserContext(ClaimsPrincipal user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return new UserContext
        {
            Name = user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue("name"),
            UserId = user.FindFirstValue("preferred_username"),
            Email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email"),
            Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList()
        };
    }
}

