using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            // The correlation + nonce cookies are SameSite=None (the OIDC callback is a
            // cross-site redirect), and browsers DROP a SameSite=None cookie that isn't
            // also Secure. The default SecurePolicy=SameAsRequest leaves them non-Secure
            // over plain HTTP (e.g. a local http://localhost port-forward), so the cookie
            // is never stored and the callback fails with "Correlation failed". Force
            // Secure: browsers make a localhost exception (Secure cookies are accepted over
            // http://localhost), and in prod the request is already HTTPS — so this is a
            // no-op there and the security-correct setting everywhere.
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
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
            // Surface OIDC failures as a redirect instead of a blank page / 500
            options.Events = new OpenIdConnectEvents
            {
                OnRemoteFailure = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("MicrosoftAuth");
                    logger.LogError(context.Failure, "Microsoft OIDC remote failure");
                    context.Response.Redirect("/login?error=auth_failed");
                    context.HandleResponse();
                    return Task.CompletedTask;
                }
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
            options.Events = new OAuthEvents
            {
                OnCreatingTicket = async context =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                    var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                    response.EnsureSuccessStatusCode();
                    var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    context.RunClaimActions(user.RootElement);
                }
            };
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
            options.Events = new OAuthEvents
            {
                OnCreatingTicket = async context =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                    var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                    response.EnsureSuccessStatusCode();
                    var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    context.RunClaimActions(user.RootElement);
                }
            };
        });

        return builder;
    }

    /// <summary>
    /// Adds Sign in with Apple via the AspNet.Security.OAuth.Apple handler.
    /// Reads from Authentication:Apple section: ClientId (the Services ID), TeamId, KeyId,
    /// PrivateKey (the Sign in with Apple .p8 key content).
    /// </summary>
    /// <remarks>
    /// Apple's protocol deviates from generic OAuth in two ways the plain OAuth handler cannot
    /// serve: requesting the name/email scopes REQUIRES response_mode=form_post (the callback
    /// arrives as a cross-site POST, not a GET), and there is no static client secret — the
    /// secret is an ES256-signed JWT minted from the Sign in with Apple key, valid at most six
    /// months. The Apple handler does both; this method only wires configuration into it.
    /// </remarks>
    public static AuthenticationBuilder AddAppleAuthentication(
        this AuthenticationBuilder builder, IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication:Apple");
        var clientId = section["ClientId"];
        if (string.IsNullOrEmpty(clientId))
            return builder;

        var privateKey = NormalizePrivateKey(section["PrivateKey"]);
        var teamId = section["TeamId"];
        var keyId = section["KeyId"];
        if (privateKey is not null && (string.IsNullOrEmpty(teamId) || string.IsNullOrEmpty(keyId)))
            throw new InvalidOperationException(
                "Authentication:Apple:PrivateKey is set but "
                + (string.IsNullOrEmpty(teamId) ? "Authentication:Apple:TeamId" : "Authentication:Apple:KeyId")
                + " is missing — the Apple client-secret JWT is signed with the key identified by TeamId + KeyId.");

        builder.AddApple(options =>
        {
            options.ClientId = clientId;
            // The Blazor catch-all excludes this literal path (NonfileRouteConstraint), so the
            // callback endpoint is part of the app's routing contract — pin it rather than
            // relying on the package default staying identical.
            options.CallbackPath = "/signin-apple";
            if (privateKey is not null)
            {
                options.GenerateClientSecret = true;
                options.TeamId = teamId!;
                options.KeyId = keyId;
                options.PrivateKey = (_, _) => Task.FromResult(privateKey.AsMemory());
            }
            else
            {
                // Externally minted client-secret JWT; expires within six months, so
                // PrivateKey-based generation is the intended configuration.
                options.ClientSecret = section["ClientSecret"] ?? "";
            }
            // The form_post callback is a cross-site POST; same correlation-cookie
            // rationale as the Microsoft handler above.
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Events.OnRemoteFailure = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("AppleAuth");
                logger.LogError(context.Failure, "Apple OIDC remote failure");
                context.Response.Redirect("/login?error=auth_failed");
                context.HandleResponse();
                return Task.CompletedTask;
            };
        });

        return builder;
    }

    private const string PemHeader = "-----BEGIN PRIVATE KEY-----";
    private const string PemFooter = "-----END PRIVATE KEY-----";

    /// <summary>
    /// Accepts the Sign in with Apple key in every shape an environment realistically delivers
    /// it: the .p8 PEM as-is, PEM with literal \n escapes (single-line env vars), base64 of the
    /// whole PEM file (kubectl-style), or the bare base64 PKCS#8 body. Returns a well-formed PEM
    /// for ECDsa.ImportFromPem, or null when no key is configured.
    /// </summary>
    internal static string? NormalizePrivateKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var key = value.Replace("\\n", "\n").Trim();
        if (key.Contains(PemHeader))
            return key;

        var compact = string.Concat(key.Where(c => !char.IsWhiteSpace(c)));
        try
        {
            var decoded = Convert.FromBase64String(compact);
            var text = System.Text.Encoding.UTF8.GetString(decoded);
            if (text.Contains(PemHeader))
                return text.Trim();
        }
        catch (FormatException)
        {
            // Not base64: pass the trimmed value through so ImportFromPem reports what's wrong.
            return key;
        }

        var body = string.Join('\n', compact.Chunk(64).Select(chunk => new string(chunk)));
        return $"{PemHeader}\n{body}\n{PemFooter}";
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
