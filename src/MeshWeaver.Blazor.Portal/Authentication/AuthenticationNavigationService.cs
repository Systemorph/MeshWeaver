using Microsoft.Extensions.Options;

namespace MeshWeaver.Blazor.Portal.Authentication;

/// <summary>
/// Default implementation of IAuthenticationNavigationService that uses
/// configured options to determine login/logout paths.
/// </summary>
public class AuthenticationNavigationService : IAuthenticationNavigationService
{
    private readonly AuthenticationOptions _options;
    private readonly string _loginPath;
    private readonly string _logoutPath;

    public AuthenticationNavigationService(IOptions<AuthenticationOptions> options)
    {
        _options = options.Value;

        // When external providers are configured, route through the login chooser page
        if (_options.Providers.Count > 0)
        {
            _loginPath = "/login";
            _logoutPath = "/auth/logout";
        }
        else
        {
            // Determine paths based on provider or custom configuration
            (_loginPath, _logoutPath) = GetProviderPaths(_options.Provider);
        }

        // Override with custom paths if specified
        if (!string.IsNullOrEmpty(_options.LoginPath))
            _loginPath = _options.LoginPath;
        if (!string.IsNullOrEmpty(_options.LogoutPath))
            _logoutPath = _options.LogoutPath;
    }

    public string ProviderName => _options.Provider;

    /// <summary>
    /// Gets the configured external providers for use by the login page.
    /// </summary>
    public IReadOnlyList<ExternalProviderConfig> GetAvailableProviders() => _options.Providers;

    /// <summary>
    /// Whether dev login should be shown (no external providers configured and using Dev provider).
    /// </summary>
    public bool IsDevMode => _options.Providers.Count == 0 &&
                             _options.Provider == AuthenticationProviders.Dev;

    public string GetLoginUrl(string? returnUrl = null)
    {
        return BuildUrl(_loginPath, returnUrl);
    }

    public string GetLogoutUrl(string? returnUrl = null)
    {
        return BuildUrl(_logoutPath, returnUrl);
    }

    private string BuildUrl(string path, string? returnUrl)
    {
        if (!_options.IncludeReturnUrl || string.IsNullOrEmpty(returnUrl))
            return path;

        var separator = path.Contains('?') ? "&" : "?";
        return $"{path}{separator}{_options.ReturnUrlParameterName}={Uri.EscapeDataString(returnUrl)}";
    }

    private static (string loginPath, string logoutPath) GetProviderPaths(string provider)
    {
        return provider switch
        {
            AuthenticationProviders.Dev => ("/dev/login", "/dev/logout"),
            AuthenticationProviders.MicrosoftIdentity => ("/MicrosoftIdentity/Account/SignIn", "/MicrosoftIdentity/Account/SignOut"),
            AuthenticationProviders.Google => ("/signin-google", "/signout"),
            AuthenticationProviders.Custom => ("/login", "/logout"),
            _ => ("/login", "/logout")
        };
    }
}
