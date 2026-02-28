namespace Memex.Portal.Shared.Admin;

/// <summary>
/// Static definitions for all supported OAuth providers.
/// Contains the constants (endpoints, scopes, claim mappings, registration URLs)
/// that never change — only App ID and KeyVault secret name are configurable.
/// </summary>
public static class OAuthProviderDefinitions
{
    public static readonly IReadOnlyDictionary<string, OAuthProviderDefinition> All =
        new Dictionary<string, OAuthProviderDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft"] = new()
            {
                Name = "Microsoft",
                DisplayName = "Microsoft",
                CallbackPath = "/signin-microsoft",
                AuthorizationEndpoint = "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
                TokenEndpoint = "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
                Scopes = ["openid", "profile", "email"],
                ClaimMappings = new() { ["sub"] = "preferred_username", ["name"] = "name", ["email"] = "email" },
                RegistrationUrl = "https://portal.azure.com/#blade/Microsoft_AAD_RegisteredApps/ApplicationsListBlade",
                RegistrationInstructions =
                    "1. Go to Azure Portal > App registrations > New registration\n" +
                    "2. Set redirect URI to: https://{your-domain}/signin-microsoft\n" +
                    "3. Under Certificates & secrets, create a new Client secret\n" +
                    "4. Copy the Application (client) ID as App ID\n" +
                    "5. Store the secret value in Azure KeyVault",
                IsMicrosoftIdentity = true,
                DefaultTenantId = "common",
                HasTenantId = true
            },
            ["Google"] = new()
            {
                Name = "Google",
                DisplayName = "Google",
                CallbackPath = "/signin-google",
                AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
                TokenEndpoint = "https://oauth2.googleapis.com/token",
                UserInformationEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo",
                Scopes = ["openid", "profile", "email"],
                ClaimMappings = new() { ["sub"] = "sub", ["name"] = "name", ["email"] = "email" },
                RegistrationUrl = "https://console.cloud.google.com/apis/credentials",
                RegistrationInstructions =
                    "1. Go to Google Cloud Console > APIs & Services > Credentials\n" +
                    "2. Create OAuth 2.0 Client ID (Web application)\n" +
                    "3. Add authorized redirect URI: https://{your-domain}/signin-google\n" +
                    "4. Copy the Client ID as App ID\n" +
                    "5. Store the Client secret in Azure KeyVault"
            },
            ["LinkedIn"] = new()
            {
                Name = "LinkedIn",
                DisplayName = "LinkedIn",
                CallbackPath = "/signin-linkedin",
                AuthorizationEndpoint = "https://www.linkedin.com/oauth/v2/authorization",
                TokenEndpoint = "https://www.linkedin.com/oauth/v2/accessToken",
                UserInformationEndpoint = "https://api.linkedin.com/v2/userinfo",
                Scopes = ["openid", "profile", "email"],
                ClaimMappings = new() { ["sub"] = "sub", ["name"] = "name", ["email"] = "email" },
                RegistrationUrl = "https://www.linkedin.com/developers/apps",
                RegistrationInstructions =
                    "1. Go to LinkedIn Developer Portal > Create App\n" +
                    "2. Under Auth tab, add redirect URL: https://{your-domain}/signin-linkedin\n" +
                    "3. Request 'Sign In with LinkedIn using OpenID Connect'\n" +
                    "4. Copy the Client ID as App ID\n" +
                    "5. Store the Client Secret in Azure KeyVault"
            },
            ["Apple"] = new()
            {
                Name = "Apple",
                DisplayName = "Apple",
                CallbackPath = "/signin-apple",
                AuthorizationEndpoint = "https://appleid.apple.com/auth/authorize",
                TokenEndpoint = "https://appleid.apple.com/auth/token",
                Scopes = ["name", "email"],
                ClaimMappings = new() { ["sub"] = "sub", ["name"] = "name", ["email"] = "email" },
                RegistrationUrl = "https://developer.apple.com/account/resources/identifiers/list/serviceId",
                RegistrationInstructions =
                    "1. Go to Apple Developer > Certificates, Identifiers & Profiles\n" +
                    "2. Register a Services ID\n" +
                    "3. Configure Sign in with Apple, set return URL: https://{your-domain}/signin-apple\n" +
                    "4. Create a key for Sign in with Apple\n" +
                    "5. Store the generated client secret in Azure KeyVault"
            }
        };
}

public record OAuthProviderDefinition
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string CallbackPath { get; init; } = "";
    public string? AuthorizationEndpoint { get; init; }
    public string? TokenEndpoint { get; init; }
    public string? UserInformationEndpoint { get; init; }
    public List<string> Scopes { get; init; } = [];
    public Dictionary<string, string> ClaimMappings { get; init; } = new();
    public string RegistrationUrl { get; init; } = "";
    public string RegistrationInstructions { get; init; } = "";
    public bool IsMicrosoftIdentity { get; init; }
    public string? DefaultTenantId { get; init; }
    public bool HasTenantId { get; init; }
}
