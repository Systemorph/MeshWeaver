using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Authentication;

namespace Memex.Client.Services;

/// <summary>
/// OAuth 2.0 + PKCE authorization-code flow against a remote portal, driven by MAUI
/// <see cref="WebAuthenticator"/> (the system browser / ASWebAuthenticationSession). Discovers the
/// authorize/token endpoints from the portal's OAuth metadata, lets the user log in the normal way
/// (Microsoft / Google / … including MFA/SSO), and exchanges the code for a bearer token usable for the
/// SignalR connection — so the user never handles a token.
///
/// <para>🩹 NEEDS LIVE TESTING. WebAuthenticator works on iOS/Android (and packaged Windows); on
/// unpackaged Windows it's limited. The portal must allow the native redirect URI
/// (<see cref="CallbackUrl"/>) and a public/native client id (<see cref="ClientId"/>) — both may need a
/// portal-side registration. Discovery falls back across the OAuth-AS and OIDC well-known docs, then to
/// conventional <c>/authorize</c> + <c>/token</c>.</para>
/// </summary>
public sealed class MeshOAuthClient
{
    /// <summary>The native redirect URI. Must be registered in the platform manifests (iOS Info.plist
    /// CFBundleURLTypes, Android intent-filter) AND accepted by the portal as a redirect URI.</summary>
    public const string CallbackUrl = "memexclient://oauth";

    /// <summary>The OAuth client id the portal recognises for native apps. Configurable.</summary>
    public string ClientId { get; set; } = "memex-client";

    /// <summary>Requested scopes. <c>offline_access</c> for a refresh token; the rest per the portal.</summary>
    public string Scope { get; set; } = "openid profile offline_access";

    private readonly HttpClient _http = new();

    /// <summary>Runs the full OAuth flow and returns a bearer token, or null if the user cancelled.</summary>
    public async Task<string?> AuthenticateAsync(string portalUrl, CancellationToken ct = default)
    {
        var (authorize, tokenEndpoint) = await DiscoverAsync(portalUrl, ct).ConfigureAwait(false);

        // PKCE: a high-entropy verifier and its S256 challenge.
        var verifier = RandomUrlToken(64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomUrlToken(16);

        var authUrl =
            $"{authorize}?response_type=code" +
            $"&client_id={Uri.EscapeDataString(ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(CallbackUrl)}" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            $"&state={state}" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        var result = await WebAuthenticator.Default
            .AuthenticateAsync(new Uri(authUrl), new Uri(CallbackUrl))
            .ConfigureAwait(false);

        if (!result.Properties.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            return null;

        // Exchange the authorization code for a token (PKCE — public client, no secret).
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = CallbackUrl,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        };
        using var resp = await _http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
    }

    /// <summary>Discovers the authorize + token endpoints from the portal's OAuth/OIDC metadata.</summary>
    private async Task<(string Authorize, string Token)> DiscoverAsync(string portalUrl, CancellationToken ct)
    {
        var baseUrl = portalUrl.TrimEnd('/');
        foreach (var well in new[] { "/.well-known/oauth-authorization-server", "/.well-known/openid-configuration" })
        {
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(baseUrl + well, ct).ConfigureAwait(false));
                var a = doc.RootElement.TryGetProperty("authorization_endpoint", out var ae) ? ae.GetString() : null;
                var t = doc.RootElement.TryGetProperty("token_endpoint", out var te) ? te.GetString() : null;
                if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(t))
                    return (a!, t!);
            }
            catch (Exception)
            {
                // This well-known doc isn't served here — try the next, then the conventional fallback.
            }
        }
        return (baseUrl + "/authorize", baseUrl + "/token");
    }

    private static string RandomUrlToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
