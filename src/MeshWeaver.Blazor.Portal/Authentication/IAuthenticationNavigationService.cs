namespace MeshWeaver.Blazor.Portal.Authentication;

/// <summary>
/// Service for handling authentication navigation (login/logout URLs).
/// Implementations provide provider-specific paths.
/// </summary>
public interface IAuthenticationNavigationService
{
    /// <summary>
    /// Gets the URL to navigate to for login.
    /// </summary>
    /// <param name="returnUrl">Optional URL to return to after login.</param>
    /// <returns>The login URL.</returns>
    string GetLoginUrl(string? returnUrl = null);

    /// <summary>
    /// Gets the URL to navigate to for logout.
    /// </summary>
    /// <param name="returnUrl">Optional URL to return to after logout.</param>
    /// <returns>The logout URL.</returns>
    string GetLogoutUrl(string? returnUrl = null);

    /// <summary>
    /// Gets the name of the authentication provider.
    /// </summary>
    string ProviderName { get; }
}
