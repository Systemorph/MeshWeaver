namespace MeshWeaver.Blazor.Portal.Authentication;

/// <summary>
/// Configuration options for authentication navigation.
/// Configure in appsettings.json under "Authentication" section.
/// </summary>
public class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// The authentication provider to use.
    /// Built-in providers: "Dev", "MicrosoftIdentity", "Google", "Custom"
    /// </summary>
    public string Provider { get; set; } = "Dev";

    /// <summary>
    /// Custom login path. If not set, uses provider default.
    /// </summary>
    public string? LoginPath { get; set; }

    /// <summary>
    /// Custom logout path. If not set, uses provider default.
    /// </summary>
    public string? LogoutPath { get; set; }

    /// <summary>
    /// Whether to include return URL in login/logout redirects.
    /// </summary>
    public bool IncludeReturnUrl { get; set; } = true;

    /// <summary>
    /// Query parameter name for return URL.
    /// </summary>
    public string ReturnUrlParameterName { get; set; } = "returnUrl";
}

/// <summary>
/// Well-known authentication provider names.
/// </summary>
public static class AuthenticationProviders
{
    public const string Dev = "Dev";
    public const string MicrosoftIdentity = "MicrosoftIdentity";
    public const string Google = "Google";
    public const string Custom = "Custom";
}
