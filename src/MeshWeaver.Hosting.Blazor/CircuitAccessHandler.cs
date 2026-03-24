using System.Security.Claims;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Scoped CircuitHandler that restores per-circuit user identity on each inbound activity.
/// Resolves the user from AuthenticationStateProvider on circuit open, stores the context,
/// and sets the AsyncLocal circuitContext on AccessService for every inbound event.
/// This ensures each Blazor circuit sees the correct user without cross-circuit contamination.
/// </summary>
public class CircuitAccessHandler : CircuitHandler
{
    private readonly IMessageHub _hub;
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly ILogger _logger;
    private AccessContext? _userContext;

    public CircuitAccessHandler(
        IMessageHub hub,
        AuthenticationStateProvider authStateProvider,
        ILoggerFactory loggerFactory)
    {
        _hub = hub;
        _authStateProvider = authStateProvider;
        _logger = loggerFactory.CreateLogger("MeshWeaver.AccessContext");
    }

    /// <summary>
    /// Gets the resolved user context for this circuit.
    /// Can be used by Blazor components that need to update the context (e.g., onboarding).
    /// </summary>
    public AccessContext? UserContext => _userContext;

    /// <summary>
    /// Updates the stored circuit context (e.g., after onboarding resolves the username).
    /// Also sets the AsyncLocal circuitContext for immediate use in the current scope.
    /// </summary>
    public void UpdateUserContext(AccessContext context)
    {
        _userContext = context;
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        accessService?.SetCircuitContext(context);
        _logger.LogDebug("Circuit context updated: user={UserId}", context.ObjectId);
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        _userContext = await ResolveUserContextAsync();

        if (_userContext != null)
        {
            // Set immediately for any code that runs during circuit initialization
            var accessService = _hub.ServiceProvider.GetService<AccessService>();
            accessService?.SetCircuitContext(_userContext);
        }

        _logger.LogDebug("Circuit opened: id={CircuitId}, user={UserId}",
            circuit.Id, _userContext?.ObjectId ?? "(anonymous)");
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async activityContext =>
        {
            var accessService = _hub.ServiceProvider.GetService<AccessService>();
            if (_userContext != null)
                accessService?.SetCircuitContext(_userContext);
            try
            {
                await next(activityContext);
            }
            finally
            {
                accessService?.SetCircuitContext(null);
            }
        };
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        _logger.LogDebug("Circuit closed: id={CircuitId}, user={UserId}",
            circuit.Id, _userContext?.ObjectId ?? "(anonymous)");
        // Clear the persistent fallback to prevent stale context
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        accessService?.ClearPersistentCircuitContext();
        _userContext = null;
        return Task.CompletedTask;
    }

    private async Task<AccessContext?> ResolveUserContextAsync()
    {
        try
        {
            var authState = await _authStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            if (user?.Identity?.IsAuthenticated != true)
                return null;

            var email = user.FindFirstValue(ClaimTypes.Email)
                        ?? user.FindFirstValue("email")
                        ?? user.FindFirstValue("preferred_username")
                        ?? string.Empty;

            var context = new AccessContext
            {
                Name = user.FindFirstValue(ClaimTypes.Name)
                       ?? user.FindFirstValue("name")
                       ?? string.Empty,
                ObjectId = email,
                Email = email,
                Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList()
            };

            // Resolve email -> ObjectId via mesh User node lookup
            if (!string.IsNullOrEmpty(email))
            {
                var meshUser = await TryLoadMeshUserAsync(email);
                if (meshUser != null)
                {
                    context = context with
                    {
                        ObjectId = meshUser.Id,
                        Name = meshUser.Name ?? meshUser.Id
                    };
                }
            }

            return context;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve user context from AuthenticationState");
            return null;
        }
    }

    private async ValueTask<MeshNode?> TryLoadMeshUserAsync(string email)
    {
        try
        {
            var accessService = _hub.ServiceProvider.GetRequiredService<AccessService>();
            using (accessService.ImpersonateAsHub(_hub))
            {
                var meshService = _hub.ServiceProvider.GetRequiredService<IMeshService>();
                return await meshService.QueryAsync<MeshNode>(
                    $"nodeType:User namespace:User content.email:{email} limit:1").FirstOrDefaultAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load mesh user for email {Email}", email);
            return null;
        }
    }
}
