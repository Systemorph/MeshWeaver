using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeshWeaver.Mesh;
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
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    public TeamsClient(TeamsOptions options, HttpClient http, ILogger<TeamsClient>? logger = null)
    {
        _options = options;
        _http = http;
        _logger = logger;
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

    public async Task<bool> SendMessageAsync(string serviceUrl, string conversationId, string text, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrEmpty(serviceUrl) || string.IsNullOrEmpty(conversationId)) return false;
        var token = await GetConnectorTokenAsync(ct);
        if (token is null) return false;
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

    private async Task<string?> GetConnectorTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-2))
            return _cachedToken;
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-2))
                return _cachedToken;
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.AppId!,
                ["client_secret"] = _options.AppPassword!,
                ["scope"] = ConnectorScope
            };
            using var resp = await _http.PostAsync(BotLoginTokenUrl, new FormUrlEncodedContent(form), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) { _logger?.LogWarning("Teams: connector token {Status}", (int)resp.StatusCode); return null; }
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            var expires = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            _cachedToken = token;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expires);
            return token;
        }
        finally { _tokenGate.Release(); }
    }
}
