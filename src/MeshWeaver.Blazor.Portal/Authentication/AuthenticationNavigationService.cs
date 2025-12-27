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

        // Determine paths based on provider or custom configuration
        (_loginPath, _logoutPath) = GetProviderPaths(_options.Provider);

        // Override with custom paths if specified
        if (!string.IsNullOrEmpty(_options.LoginPath))
            _loginPath = _options.LoginPath;
        if (!string.IsNullOrEmpty(_options.LogoutPath))
            _logoutPath = _options.LogoutPath;
    }

    public string ProviderName => _options.Provider;

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
