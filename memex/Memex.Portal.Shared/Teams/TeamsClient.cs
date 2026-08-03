using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Memex.Portal.Shared.Teams;

/// <summary>
/// Real <see cref="ITeamsClient"/> over the Bot Framework REST connector. Inbound activities are
/// authenticated by validating the bearer JWT against the Bot Framework's published OpenID metadata
/// (issuer <c>https://api.botframework.com</c>, audience = the bot's app id); outbound replies use an
/// app-only connector token (client credentials at the <c>botframework.com</c> tenant) POSTed to the
/// activity's <c>serviceUrl</c>. Token + signing-key metadata are cached on this (instance) singleton.
/// </summary>
public sealed class TeamsClient : ITeamsClient
{
    private const string BotLoginTokenUrl = "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token";
    private const string ConnectorScope = "https://api.botframework.com/.default";
    private const string OpenIdMetadataUrl = "https://login.botframework.com/v1/.well-known/openidconfiguration";
    private const string ExpectedIssuer = "https://api.botframework.com";

    private readonly TeamsOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<TeamsClient>? _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _openIdConfig;

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry;

    // Token refresh is a promise-cached one-shot on the HTTP pool, NOT a SemaphoreSlim(1,1) gate.
    // A gate here parks the awaiting thread — on a hub action block or a grain turn that is a
    // deadlock, and it bounds nothing that the pool does not already bound. Keyed by the expiry
    // being replaced, so every caller that sees the SAME stale token shares ONE fetch and replays
    // its completion; a successful refresh moves the expiry, which is itself the next key.
    private readonly IIoPool _httpPool;
    private readonly ConcurrentDictionary<long, IObservable<string?>> _tokenFetch = new();

    public TeamsClient(TeamsOptions options, HttpClient http, ILogger<TeamsClient>? logger = null,
        IoPoolRegistry? ioPoolRegistry = null)
    {
        _options = options;
        _http = http;
        _logger = logger;
        _httpPool = ioPoolRegistry?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        _openIdConfig = new ConfigurationManager<OpenIdConnectConfiguration>(
            OpenIdMetadataUrl, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever());
    }

    public bool IsConfigured =>
        _options.Enabled && !string.IsNullOrEmpty(_options.AppId) && !string.IsNullOrEmpty(_options.AppPassword);

    public async Task<bool> ValidateInboundAsync(string? authorizationHeader, CancellationToken ct)
    {
        if (!IsConfigured) return false;
        if (string.IsNullOrEmpty(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;
        var token = authorizationHeader["Bearer ".Length..].Trim();
        try
        {
            var config = await _openIdConfig.GetConfigurationAsync(ct);
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = ExpectedIssuer,
                ValidateAudience = true,
                ValidAudience = _options.AppId,
                ValidateLifetime = true,
                IssuerSigningKeys = config.SigningKeys
            };
            new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Teams: inbound token validation failed");
            return false;
        }
    }

    /// <summary>Outbound reply. Composed reactively — the token fetch and the POST are both leaves
    /// on the HTTP pool, so every <c>await</c> lives inside a pool body. The single <c>ToTask</c> is
    /// the boundary adapter for the <see cref="ITeamsClient"/> surface, nothing more.</summary>
    public Task<bool> SendMessageAsync(string serviceUrl, string conversationId, string text, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrEmpty(serviceUrl) || string.IsNullOrEmpty(conversationId))
            return Task.FromResult(false);
        return GetConnectorToken()
            .SelectMany(token => token is null
                ? Observable.Return(false)
                : _httpPool.Invoke(poolCt => PostActivityAsync(token, serviceUrl, conversationId, text, poolCt)))
            .FirstAsync()
            .ToTask(ct);
    }

    private async Task<bool> PostActivityAsync(string token, string serviceUrl, string conversationId,
        string text, CancellationToken ct)
    {
        try
        {
            var url = $"{serviceUrl.TrimEnd('/')}/v3/conversations/{Uri.EscapeDataString(conversationId)}/activities";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { type = "message", text })
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return true;
            _logger?.LogWarning("Teams: send reply returned {Status}", (int)resp.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Teams: send reply failed");
            return false;
        }
    }

    /// <summary>The cached connector token, refreshed at most once per expiry. No gate: racing
    /// callers all key on the expiry they are replacing, so they share ONE pooled fetch and replay
    /// its result.</summary>
    private IObservable<string?> GetConnectorToken()
    {
        var cached = _cachedToken;
        if (cached is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-2))
            return Observable.Return(cached);
        var replacing = _tokenExpiry.Ticks;
        return _tokenFetch.GetOrAdd(replacing, key => _httpPool.Run(poolCt => FetchConnectorTokenAsync(key, poolCt)));
    }

    private async Task<string?> FetchConnectorTokenAsync(long replacing, CancellationToken ct)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.AppId!,
                ["client_secret"] = _options.AppPassword!,
                ["scope"] = ConnectorScope
            };
            // Single-tenant bots acquire the connector token from their OWN tenant authority; the legacy
            // botframework.com authority is for (now-deprecated) multi-tenant bots.
            var tokenUrl = string.IsNullOrEmpty(_options.TenantId)
                ? BotLoginTokenUrl
                : $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token";
            using var resp = await _http.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) { _logger?.LogWarning("Teams: connector token {Status}", (int)resp.StatusCode); return null; }
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            var expires = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            _cachedToken = token;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expires);
            return token;
        }
        finally
        {
            // Drop this generation's promise: the refreshed expiry is the next key, so the entry
            // can never be reused and the cache cannot grow without bound.
            _tokenFetch.TryRemove(replacing, out _);
        }
    }
}
