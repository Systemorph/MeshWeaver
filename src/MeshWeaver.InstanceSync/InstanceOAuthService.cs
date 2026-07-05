using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// Server-side OAuth 2.0 + PKCE client for connecting instance sync to a REMOTE MeshWeaver
/// instance. Every MeshWeaver instance exposes its own OAuth authorization server (RFC 8414
/// <c>.well-known</c> + <c>/authorize</c> + <c>/token</c>, a public client secured by PKCE with no
/// pre-registration — see <c>OAuthConnectController</c>), so the user's browser is redirected to
/// the remote's own login (Microsoft / Google / …) and the returned <c>mw_</c> token is stored as
/// the party's <see cref="InstanceSyncConfig.RemoteToken"/> — the user never pastes a token.
///
/// <para>Mirrors the MAUI <c>MeshOAuthClient</c>, but reactive + <see cref="IIoPool"/>-bounded for
/// the portal: <c>InstanceConnectEndpoints</c> drives it from the <c>/connect/instance</c> flow.
/// 🚨 Reactive end-to-end — the HTTP leaves run on the Http I/O pool, never a bare
/// <c>Observable.FromAsync</c>.</para>
/// </summary>
public sealed class InstanceOAuthService
{
    /// <summary>The fixed public client id this portal presents to a remote's <c>/authorize</c> +
    /// <c>/token</c>. The remote accepts any client_id (PKCE-secured, no pre-registration required
    /// — see <c>OAuthConnectController.Authorize</c>), so no dynamic <c>/register</c> is needed.</summary>
    public const string ClientId = "meshweaver-instance-sync";

    private readonly IIoPool _httpPool;
    private readonly HttpClient _http;
    private readonly ILogger<InstanceOAuthService>? _logger;

    /// <summary>Creates the service, resolving the mesh-scoped Http I/O pool the HTTP leaves route through.</summary>
    public InstanceOAuthService(IMessageHub hub, ILogger<InstanceOAuthService>? logger = null)
    {
        _httpPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        _http = new HttpClient();
        _logger = logger;
    }

    /// <summary>The remote's authorize + token endpoints.</summary>
    public readonly record struct Endpoints(string Authorize, string Token);

    /// <summary>
    /// Discovers the remote's authorize + token endpoints from its RFC 8414 / OIDC metadata,
    /// falling back to the conventional <c>{base}/authorize</c> + <c>{base}/token</c> (every
    /// MeshWeaver instance serves those). One pooled GET; the fallback needs no HTTP.
    /// </summary>
    public IObservable<Endpoints> Discover(string remoteUrl)
    {
        var baseUrl = remoteUrl.TrimEnd('/');
        return _httpPool.Invoke(async ct =>
        {
            foreach (var well in new[] { "/.well-known/oauth-authorization-server", "/.well-known/openid-configuration" })
            {
                try
                {
                    var json = await _http.GetStringAsync(baseUrl + well, ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var a = doc.RootElement.TryGetProperty("authorization_endpoint", out var ae) ? ae.GetString() : null;
                    var t = doc.RootElement.TryGetProperty("token_endpoint", out var te) ? te.GetString() : null;
                    if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(t))
                        return new Endpoints(a!, t!);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "OAuth metadata {Well} not served by {Remote}; trying next / fallback", well, baseUrl);
                }
            }
            return new Endpoints(baseUrl + "/authorize", baseUrl + "/token");
        });
    }

    /// <summary>Builds the remote <c>/authorize</c> redirect (PKCE S256, our callback as redirect_uri).</summary>
    public static string BuildAuthorizeUrl(string authorizeEndpoint, string redirectUri, string codeChallenge, string state) =>
        $"{authorizeEndpoint}?response_type=code" +
        $"&client_id={Uri.EscapeDataString(ClientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&state={Uri.EscapeDataString(state)}" +
        $"&code_challenge={codeChallenge}&code_challenge_method=S256";

    /// <summary>
    /// Exchanges the authorization code at the remote's <c>/token</c> (PKCE public client, no
    /// secret) for the remote's <c>mw_</c> access token. Pooled POST; throws with the remote's body
    /// on failure so the callback surfaces the real reason instead of a silent bounce.
    /// </summary>
    public IObservable<string> ExchangeCode(string tokenEndpoint, string code, string redirectUri, string codeVerifier) =>
        _httpPool.Invoke(async ct =>
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = ClientId,
                ["code_verifier"] = codeVerifier,
            });
            using var resp = await _http.PostAsync(tokenEndpoint, form, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Remote token exchange failed ({(int)resp.StatusCode}): {body}");
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("access_token", out var at) && at.GetString() is { Length: > 0 } tok
                ? tok
                : throw new InvalidOperationException("Remote token response contained no access_token.");
        });

    // ── PKCE + CSRF helpers ──

    /// <summary>A high-entropy PKCE code verifier (64 random bytes, base64url).</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));

    /// <summary>The S256 challenge for a verifier.</summary>
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>A random CSRF state token (24 random bytes, base64url).</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
