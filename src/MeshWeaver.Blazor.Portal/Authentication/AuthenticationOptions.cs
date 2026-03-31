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

    /// <summary>
    /// Whether to enable developer login (shows user list for quick sign-in).
    /// When true, the /login page includes a "Developer Login" option.
    /// Can be used alongside external providers.
    /// </summary>
    public bool EnableDevLogin { get; set; }

    /// <summary>
    /// List of external authentication providers (Microsoft, Google, LinkedIn, Apple).
    /// When configured, the /login page shows buttons for each provider.
    /// </summary>
    public List<ExternalProviderConfig> Providers { get; set; } = new();
}

/// <summary>
/// Configuration for an external OAuth/OIDC authentication provider.
/// </summary>
public record ExternalProviderConfig
{
    /// <summary>Provider identifier (e.g., "Microsoft", "Google", "LinkedIn", "Apple").</summary>
    public string Name { get; init; } = "";

    /// <summary>Display name shown on the login button.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Icon identifier or CSS class for the provider button.</summary>
    public string? Icon { get; init; }

    /// <summary>OAuth client ID.</summary>
    public string ClientId { get; init; } = "";

    /// <summary>OAuth client secret.</summary>
    public string ClientSecret { get; init; } = "";

    /// <summary>Tenant ID (Microsoft-specific, defaults to "common").</summary>
    public string? TenantId { get; init; }

    /// <summary>Additional provider-specific settings.</summary>
    public Dictionary<string, string>? Extra { get; init; }
}

/// <summary>
/// Well-known authentication provider names.
/// </summary>
public static class AuthenticationProviders
{
    public const string Dev = "Dev";
    public const string MicrosoftIdentity = "MicrosoftIdentity";
    public const string Google = "Google";
    public const string LinkedIn = "LinkedIn";
    public const string Apple = "Apple";
    public const string Custom = "Custom";
}
