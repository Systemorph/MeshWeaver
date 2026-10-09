using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The Entra ID rung of the approval step-up (<c>Doc/Architecture/ApprovalStepUp</c>): composes the
/// authorize request (<c>prompt=login</c>, <c>login_hint</c>, a nonce, and the <c>claims</c>
/// parameter requesting the declared Conditional Access authentication context in the ID token),
/// redeems the code, validates the returned <c>id_token</c>'s signature/audience/lifetime against
/// the tenant's published keys, and hands its claims to <see cref="EntraStepUpTokenCheck"/>.
///
/// <para>Reuses the portal's Microsoft sign-in app (<c>Authentication:Microsoft:ClientId</c> /
/// <c>ClientSecret</c>) — the same registration <see cref="EaGraphAuth"/> uses for delegated
/// consent; nothing new to register beyond the redirect URI <c>/auth/step-up/callback</c>.</para>
///
/// <para>All network I/O runs on the HTTP <see cref="IIoPool"/>; the surface is reactive, and the
/// one Task bridge lives in the MVC action.</para>
/// </summary>
/// <param name="configuration">Host configuration (read live).</param>
/// <param name="http">An HTTP client.</param>
/// <param name="pool">The HTTP I/O pool.</param>
/// <param name="logger">Logger.</param>
internal sealed class EntraStepUp(IConfiguration configuration, HttpClient http, IIoPool pool, ILogger logger)
{
    private static readonly ImmutableHashSet<string> MultiTenantAliases =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "common", "organizations", "consumers");

    private string? ClientId => configuration["Authentication:Microsoft:ClientId"];
    private string? ClientSecret => configuration["Authentication:Microsoft:ClientSecret"];

    /// <summary>The tenant step-up tokens must come from: the declared step-up tenant, else the sign-in tenant.</summary>
    public string Tenant => StepUpOptions.From(configuration).EntraTenantId
        ?? MicrosoftTenant.Resolve(configuration[MicrosoftTenant.ConfigurationKey]);

    /// <summary>True when the Microsoft sign-in app can run the flow.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret);

    /// <summary>True when <see cref="Tenant"/> names ONE tenant — a multi-tenant alias has no single issuer to pin.</summary>
    public bool TenantIsSpecific => !MultiTenantAliases.Contains(Tenant);

    /// <summary>The <c>claims</c> request parameter asking for <paramref name="context"/> in the ID token.</summary>
    /// <param name="context">The authentication context id.</param>
    /// <returns>The JSON.</returns>
    public static string ClaimsParameter(string context) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id_token"] = new Dictionary<string, object>
            {
                ["acrs"] = new Dictionary<string, object> { ["essential"] = true, ["value"] = context },
            },
        });

    /// <summary>The authorize URL for one step-up.</summary>
    /// <param name="state">Opaque state, echoed back.</param>
    /// <param name="nonce">The nonce the ID token must carry.</param>
    /// <param name="redirectUri">The callback.</param>
    /// <param name="loginHint">The signed-in account, so Entra re-authenticates THAT account.</param>
    /// <param name="context">The authentication context id.</param>
    /// <returns>The URL.</returns>
    public string AuthorizeUrl(string state, string nonce, string redirectUri, string? loginHint, string context) =>
        $"{MicrosoftTenant.Authority(Tenant, "oauth2/v2.0")}/authorize?client_id={Uri.EscapeDataString(ClientId ?? "")}"
        + "&response_type=code&response_mode=query"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + "&scope=" + Uri.EscapeDataString("openid profile email")
        + $"&state={Uri.EscapeDataString(state)}"
        + $"&nonce={Uri.EscapeDataString(nonce)}"
        + "&prompt=login"
        + (string.IsNullOrEmpty(loginHint) ? "" : $"&login_hint={Uri.EscapeDataString(loginHint)}")
        + $"&claims={Uri.EscapeDataString(ClaimsParameter(context))}";

    /// <summary>
    /// Redeems <paramref name="code"/> and checks the ID token. Cold, single emission, never faults:
    /// every failure is an <see cref="EntraStepUpCheck"/> naming the step that refused.
    /// </summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="redirectUri">The callback the code was issued to.</param>
    /// <param name="expectation">Builds the expectation once the tenant's issuer is known.</param>
    /// <returns>The verdict.</returns>
    public IObservable<EntraStepUpCheck> Redeem(string code, string redirectUri, Func<string, EntraStepUpExpectation> expectation) =>
        pool.Invoke<EntraStepUpCheck>(async ct =>
        {
            using var response = await http.PostAsync(
                $"{MicrosoftTenant.Authority(Tenant, "oauth2/v2.0")}/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId ?? "",
                    ["client_secret"] = ClientSecret ?? "",
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri,
                    ["scope"] = "openid profile email",
                }), ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Entra step-up: the token endpoint answered {Status}: {Body}",
                    (int)response.StatusCode, body.Length > 400 ? body[..400] : body);
                return EntraStepUpCheck.Fail("exchange");
            }

            string? idToken;
            using (var doc = JsonDocument.Parse(body))
                idToken = doc.RootElement.TryGetProperty("id_token", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(idToken))
                return EntraStepUpCheck.Fail("exchange");

            var metadata = new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{MicrosoftTenant.Authority(Tenant, "v2.0")}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(http) { RequireHttps = true });
            var config = await metadata.GetConfigurationAsync(ct).ConfigureAwait(false);

            var validated = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidateIssuer = false,           // pinned exactly by EntraStepUpTokenCheck against config.Issuer
                ValidateAudience = true,
                ValidAudience = ClientId,
                ValidateLifetime = true,
                IssuerSigningKeys = config.SigningKeys,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
            }).ConfigureAwait(false);
            if (!validated.IsValid || validated.SecurityToken is not JsonWebToken jwt)
            {
                logger.LogWarning(validated.Exception, "Entra step-up: the ID token did not validate");
                return EntraStepUpCheck.Fail("signature");
            }

            return EntraStepUpTokenCheck.Evaluate(jwt.Claims.ToList(), expectation(config.Issuer));
        });
}
