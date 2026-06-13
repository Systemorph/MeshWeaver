using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.GitSync;

/// <summary>
/// Drives the GitHub OAuth <b>device flow</b> to obtain a long-standing access
/// token for the current user. Mirrors the device-flow Connect state machine used
/// for the AI CLIs, but talks to GitHub's OAuth endpoints directly over HTTP — and
/// every HTTP leaf runs inside <see cref="IIoPool"/> (the <see cref="IoPoolNames.Http"/>
/// pool); polling is reactive via <see cref="Observable.Interval"/>, never
/// <c>Task.Delay</c>.
///
/// <list type="number">
///   <item><see cref="StartConnect"/> → POST <c>login/device/code</c>; show the
///     <see cref="DeviceChallenge.UserCode"/> + <see cref="DeviceChallenge.VerificationUri"/>.</item>
///   <item><see cref="Poll"/> → POST <c>login/oauth/access_token</c> on an interval
///     until the user authorizes (or the code expires).</item>
/// </list>
/// </summary>
public sealed class GitHubOAuthService
{
    private readonly HttpClient http;
    private readonly IoPoolRegistry ioPools;
    private readonly GitHubOAuthOptions options;
    private readonly ILogger? logger;

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

    public bool IsConfigured => options.IsConfigured;

    private IIoPool Http => ioPools.Get(IoPoolNames.Http);

    /// <summary>Requests a device code; emits the challenge to show the user.</summary>
    public IObservable<DeviceChallenge> StartConnect()
    {
        if (!options.IsConfigured)
            return Observable.Throw<DeviceChallenge>(new InvalidOperationException(
                "GitHub OAuth is not configured (set GitHub:OAuth:ClientId)."));
        return Http.Invoke(ct => RequestDeviceCodeAsync(ct));
    }

    /// <summary>
    /// Polls the token endpoint on the device-flow interval until the user
    /// authorizes (emits the token), the request is denied/expired (errors), or the
    /// challenge's overall lifetime elapses (times out).
    /// </summary>
    public IObservable<GitHubToken> Poll(DeviceChallenge challenge)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(challenge.IntervalSeconds, 5) + 1);
        return Observable.Interval(interval)
            .SelectMany(_ => Http.Invoke(ct => ExchangeAsync(challenge.DeviceCode, ct)))
            .Where(o => o.IsTerminal)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(Math.Max(challenge.ExpiresInSeconds, 60)))
            .SelectMany(o => o.Token is { } token
                ? Observable.Return(token)
                : Observable.Throw<GitHubToken>(new InvalidOperationException(
                    $"GitHub authorization failed: {o.Error ?? "unknown error"}")));
    }

    /// <summary>Convenience: start + poll as a single observable that emits the token once authorized.</summary>
    public IObservable<GitHubToken> Connect() => StartConnect().SelectMany(Poll);

    /// <summary>Resolves the authenticated user's GitHub login for the given token (for display + commit authoring).</summary>
    public IObservable<string?> GetLogin(string accessToken) =>
        Http.Invoke(ct => GetLoginAsync(accessToken, ct));

    // ── HTTP leaves (run inside the I/O pool) ────────────────────────────────

    private async Task<DeviceChallenge> RequestDeviceCodeAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId!,
            ["scope"] = options.Scopes,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, options.DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new DeviceChallenge(
            r.GetProperty("device_code").GetString()!,
            r.GetProperty("user_code").GetString()!,
            r.GetProperty("verification_uri").GetString()!,
            GetInt(r, "interval") ?? 5,
            GetInt(r, "expires_in") ?? 900);
    }

    private async Task<PollOutcome> ExchangeAsync(string deviceCode, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId!,
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
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
        if (root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String)
            return new PollOutcome(
                new GitHubToken(
                    at.GetString()!,
                    GetStr(root, "refresh_token"),
                    GetStr(root, "token_type") ?? "bearer",
                    GetStr(root, "scope") ?? options.Scopes,
                    GetInt(root, "expires_in")),
                null, IsTerminal: true);

        var error = GetStr(root, "error");
        // authorization_pending / slow_down → keep polling (not terminal).
        if (error is "authorization_pending" or "slow_down")
            return new PollOutcome(null, error, IsTerminal: false);
        return new PollOutcome(null, error ?? "unknown_error", IsTerminal: true);
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

    private record PollOutcome(GitHubToken? Token, string? Error, bool IsTerminal);
}
