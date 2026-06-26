using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.GitSync;

/// <summary>
/// GitHub OAuth <b>authorization-code</b> flow helper. Builds the authorize URL the
/// browser is redirected to, and exchanges the returned <c>code</c> for a
/// long-standing access token. Used by the portal's <c>/connect/github</c> endpoints
/// (mirrors the LinkedIn connect); the callback stores the token via
/// <see cref="GitHubCredentialService"/>.
///
/// <para>🚨 Every HTTP leaf runs inside <see cref="IIoPool"/> (the
/// <see cref="IoPoolNames.Http"/> pool) and the public surface is
/// <see cref="IObservable{T}"/> — no <c>async</c>/<c>await</c>/<c>Task</c> escapes a
/// signature (<c>Doc/Architecture/ControlledIoPooling.md</c>).</para>
/// </summary>
public sealed class GitHubOAuthService
{
    private readonly HttpClient http;
    private readonly IoPoolRegistry ioPools;
    private readonly GitHubOAuthOptions options;
    private readonly ILogger? logger;

    /// <summary>Initializes a new instance of the <c>GitHubOAuthService</c> class.</summary>
    /// <param name="ioPools">Registry the HTTP I/O pool is resolved from so every HTTP leaf runs off the hub.</param>
    /// <param name="options">The bound <see cref="GitHubOAuthOptions"/> (client id/secret, endpoints, scopes).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="httpClient">Optional <see cref="HttpClient"/> to reuse; a default one is created when null.</param>
    public GitHubOAuthService(
        IoPoolRegistry ioPools,
        IOptions<GitHubOAuthOptions> options,
        ILogger<GitHubOAuthService>? logger = null,
        HttpClient? httpClient = null)
    {
        this.ioPools = ioPools;
        this.options = options.Value;
        this.logger = logger;
        http = httpClient ?? new HttpClient();
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshWeaver-GitSync");
    }

    /// <summary>True when the OAuth client id and secret are configured and the flow can run.</summary>
    public bool IsConfigured => options.IsConfigured;

    private IIoPool Http => ioPools.Get(IoPoolNames.Http);

    /// <summary>The GitHub authorize URL to redirect the browser to (start of the flow).</summary>
    public string BuildAuthorizeUrl(string redirectUri, string state) =>
        $"{options.AuthorizeUrl}?response_type=code"
        + $"&client_id={Uri.EscapeDataString(options.ClientId ?? "")}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&state={Uri.EscapeDataString(state)}"
        + $"&scope={Uri.EscapeDataString(options.Scopes)}";

    /// <summary>Exchanges the callback <c>code</c> for an access token (server-side, with the client secret).</summary>
    public IObservable<GitHubToken> ExchangeCode(string code, string redirectUri)
    {
        if (!options.IsConfigured)
            return Observable.Throw<GitHubToken>(new InvalidOperationException(
                "GitHub OAuth is not configured (set GitHub:OAuth:ClientId + ClientSecret)."));
        return Http.Invoke(ct => ExchangeCodeAsync(code, redirectUri, ct));
    }

    /// <summary>Resolves the authenticated user's GitHub login (for display + commit authoring).</summary>
    public IObservable<string?> GetLogin(string accessToken) =>
        Http.Invoke(ct => GetLoginAsync(accessToken, ct));

    // ── HTTP leaves (run inside the I/O pool) ────────────────────────────────

    private async Task<GitHubToken> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = options.ClientId!,
            ["client_secret"] = options.ClientSecret!,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, options.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String)
        {
            var error = GetStr(root, "error_description") ?? GetStr(root, "error") ?? "no access_token in response";
            throw new InvalidOperationException($"GitHub token exchange failed: {error}");
        }
        return new GitHubToken(
            at.GetString()!,
            GetStr(root, "refresh_token"),
            GetStr(root, "token_type") ?? "bearer",
            GetStr(root, "scope") ?? options.Scopes,
            GetInt(root, "expires_in"));
    }

    private async Task<string?> GetLoginAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, options.UserApiUrl);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return GetStr(doc.RootElement, "login");
    }

    private static string? GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
