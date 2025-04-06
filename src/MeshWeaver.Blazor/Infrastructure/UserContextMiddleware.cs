using System.Security.Claims;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Infrastructure;

public class UserContextMiddleware(RequestDelegate next)
{
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


    private AccessContext ExtractUserContext(ClaimsPrincipal user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return new AccessContext
        {
            Name = user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue("name"),
            ObjectId = user.FindFirstValue("preferred_username"),
            Email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email"),
            Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList()
        };
    }
}

