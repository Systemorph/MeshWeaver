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

    /// <summary>
    /// Initializes the service, resolving the login and logout paths from the supplied options
    /// (defaulting to <c>/login</c> and either <c>/auth/logout</c> or <c>/dev/logout</c>) and
    /// applying any custom path overrides.
    /// </summary>
    /// <param name="options">The authentication options used to determine login/logout paths and return-URL behavior.</param>
    public AuthenticationNavigationService(IOptions<AuthenticationOptions> options)
    {
        _options = options.Value;

        // Always route through the unified /login page
        _loginPath = "/login";
        _logoutPath = _options.Providers.Count > 0 ? "/auth/logout" : "/dev/logout";

        // Override with custom paths if specified
        if (!string.IsNullOrEmpty(_options.LoginPath))
            _loginPath = _options.LoginPath;
        if (!string.IsNullOrEmpty(_options.LogoutPath))
            _logoutPath = _options.LogoutPath;
    }

    /// <summary>
    /// Gets the name of the configured authentication provider.
    /// </summary>
    public string ProviderName => _options.Provider;

    /// <summary>
    /// Gets the configured external providers for use by the login page.
    /// </summary>
    public IReadOnlyList<ExternalProviderConfig> GetAvailableProviders() => _options.Providers;

    /// <summary>
    /// Whether dev login should be shown on the login page.
    /// </summary>
    public bool IsDevMode => _options.EnableDevLogin;

    /// <summary>
    /// Builds the login URL, optionally appending the return URL as a query parameter.
    /// </summary>
    /// <param name="returnUrl">The URL to return to after a successful login; ignored when return URLs are disabled or it is empty.</param>
    /// <returns>The login path, with the return URL appended when configured.</returns>
    public string GetLoginUrl(string? returnUrl = null)
    {
        return BuildUrl(_loginPath, returnUrl);
    }

    /// <summary>
    /// Builds the logout URL, optionally appending the return URL as a query parameter.
    /// </summary>
    /// <param name="returnUrl">The URL to return to after logout; ignored when return URLs are disabled or it is empty.</param>
    /// <returns>The logout path, with the return URL appended when configured.</returns>
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

}
