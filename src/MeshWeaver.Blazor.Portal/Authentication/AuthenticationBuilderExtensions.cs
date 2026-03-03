using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace MeshWeaver.Blazor.Portal.Authentication;

/// <summary>
/// Fluent extension methods for adding OAuth/OIDC authentication providers.
/// Each method reads from IConfiguration and is a no-op if ClientId is not configured.
/// </summary>
public static class AuthenticationBuilderExtensions
{
    /// <summary>
    /// Adds Microsoft authentication via OpenID Connect.
    /// Reads from Authentication:Microsoft section: ClientId, ClientSecret, TenantId.
    /// </summary>
    public static AuthenticationBuilder AddMicrosoftAuthentication(
        this AuthenticationBuilder builder, IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication:Microsoft");
        var clientId = section["ClientId"];
        if (string.IsNullOrEmpty(clientId))
            return builder;

        var tenantId = section["TenantId"] ?? "common";
        builder.AddOpenIdConnect("Microsoft", options =>
        {
            options.ClientId = clientId;
            options.ClientSecret = section["ClientSecret"] ?? "";
            options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
            options.CallbackPath = "/signin-microsoft";
            options.ResponseType = "code";
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.TokenValidationParameters.NameClaimType = "name";
            // Multi-tenant: discovery doc has {tenantid} placeholder, actual token has real tenant ID
            options.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
            {
                if (Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
                    && uri.Host == "login.microsoftonline.com")
                    return issuer;
                throw new SecurityTokenInvalidIssuerException($"Invalid issuer: {issuer}");
            };
        });

        return builder;
    }

    /// <summary>
    /// Adds Google authentication via OAuth2.
    /// Reads from Authentication:Google section: ClientId, ClientSecret.
    /// </summary>
    public static AuthenticationBuilder AddGoogleAuthentication(
        this AuthenticationBuilder builder, IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication:Google");
        var clientId = section["ClientId"];
        if (string.IsNullOrEmpty(clientId))
            return builder;

        builder.AddOAuth("Google", options =>
        {
            options.ClientId = clientId;
            options.ClientSecret = section["ClientSecret"] ?? "";
            options.CallbackPath = "/signin-google";
            options.AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
            options.TokenEndpoint = "https://oauth2.googleapis.com/token";
            options.UserInformationEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "sub");
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Name, "name");
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Email, "email");
        });

        return builder;
    }

    /// <summary>
    /// Adds LinkedIn authentication via OAuth2.
    /// Reads from Authentication:LinkedIn section: ClientId, ClientSecret.
    /// </summary>
    public static AuthenticationBuilder AddLinkedInAuthentication(
        this AuthenticationBuilder builder, IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication:LinkedIn");
        var clientId = section["ClientId"];
        if (string.IsNullOrEmpty(clientId))
            return builder;

        builder.AddOAuth("LinkedIn", options =>
        {
            options.ClientId = clientId;
            options.ClientSecret = section["ClientSecret"] ?? "";
            options.CallbackPath = "/signin-linkedin";
            options.AuthorizationEndpoint = "https://www.linkedin.com/oauth/v2/authorization";
            options.TokenEndpoint = "https://www.linkedin.com/oauth/v2/accessToken";
            options.UserInformationEndpoint = "https://api.linkedin.com/v2/userinfo";
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "sub");
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Name, "name");
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Email, "email");
        });

        return builder;
    }

    /// <summary>
    /// Adds Apple authentication via OAuth2.
    /// Reads from Authentication:Apple section: ClientId, ClientSecret.
    /// </summary>
    public static AuthenticationBuilder AddAppleAuthentication(
        this AuthenticationBuilder builder, IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication:Apple");
        var clientId = section["ClientId"];
        if (string.IsNullOrEmpty(clientId))
            return builder;

        builder.AddOAuth("Apple", options =>
        {
            options.ClientId = clientId;
            options.ClientSecret = section["ClientSecret"] ?? "";
            options.CallbackPath = "/signin-apple";
            options.AuthorizationEndpoint = "https://appleid.apple.com/auth/authorize";
            options.TokenEndpoint = "https://appleid.apple.com/auth/token";
            options.Scope.Add("name");
            options.Scope.Add("email");
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "sub");
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Name, "name");
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Email, "email");
        });

        return builder;
    }

    /// <summary>
    /// Returns true if any external provider has a ClientId configured.
    /// </summary>
    public static bool HasExternalProviders(IConfiguration configuration)
    {
        foreach (var provider in new[] { "Microsoft", "Google", "LinkedIn", "Apple" })
        {
            var clientId = configuration[$"Authentication:{provider}:ClientId"];
            if (!string.IsNullOrEmpty(clientId))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Builds a list of ExternalProviderConfig from configuration for the login UI.
    /// Only includes providers with a ClientId configured.
    /// </summary>
    public static List<ExternalProviderConfig> GetConfiguredProviders(IConfiguration configuration)
    {
        var providers = new List<ExternalProviderConfig>();
        foreach (var (name, displayName) in new[]
        {
            ("Microsoft", "Microsoft"),
            ("Google", "Google"),
            ("LinkedIn", "LinkedIn"),
            ("Apple", "Apple")
        })
        {
            var section = configuration.GetSection($"Authentication:{name}");
            var clientId = section["ClientId"];
            if (!string.IsNullOrEmpty(clientId))
            {
                providers.Add(new ExternalProviderConfig
                {
                    Name = name,
                    DisplayName = displayName,
                    ClientId = clientId,
                    ClientSecret = section["ClientSecret"] ?? "",
                    TenantId = section["TenantId"]
                });
            }
        }
        return providers;
    }
}
