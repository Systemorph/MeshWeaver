using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Social;

/// <summary>
/// X (Twitter) implementation of <see cref="IPlatformPublisher"/>. Uses the v2 API:
///   POST /2/tweets                      — publish
///   GET  /2/tweets/{id}?tweet.fields=public_metrics — stats
///   GET  /2/users/{id}/tweets           — history
///
/// Scope requirements (PKCE OAuth2 user context):
///   tweet.read tweet.write users.read offline.access
///
/// The v2 API uses a two-legged bearer token for app-only calls (limited metrics) and
/// OAuth2 user context for write + per-user read. <see cref="PlatformCredential"/>
/// carries the user-context token; the publisher refreshes via
/// https://api.twitter.com/2/oauth2/token.
/// </summary>
public sealed class XPublisher : IPlatformPublisher
{
    public const string PlatformId = "Twitter";
    public string Platform => PlatformId;

    private static readonly Uri ApiBase = new("https://api.twitter.com/");
    private static readonly Uri TokenEndpoint = new("https://api.twitter.com/2/oauth2/token");

    private readonly HttpClient _http;
    private readonly XOptions _options;
    private readonly ILogger<XPublisher>? _logger;

    public XPublisher(HttpClient http, XOptions options, ILogger<XPublisher>? logger = null)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<PublishResult> PublishAsync(PlatformPublishRequest request, CancellationToken ct)
    {
        var credential = await EnsureFreshAsync(request.Credential, ct);

        // Media upload on v2 is a multi-step process via the legacy v1.1 upload endpoint
        // (INIT/APPEND/FINALIZE) then passing media_ids into v2 tweets. First cut: text only.
        // Media support is follow-up once end-to-end text flow is verified.
        var body = new Dictionary<string, object> { ["text"] = request.Text };

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, "2/tweets"))
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var b = await resp.Content.ReadAsStringAsync(ct);
            _logger?.LogWarning("X publish failed {Status}: {Body}", (int)resp.StatusCode, b);
            return new PublishResult(null, null, DateTimeOffset.UtcNow, Error: $"X {(int)resp.StatusCode}: {b}");
        }

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var id = doc.RootElement.GetProperty("data").GetProperty("id").GetString();
        var handle = request.AuthorHandle.TrimStart('@');
        var url = id is null ? null : $"https://x.com/{Uri.EscapeDataString(handle)}/status/{id}";
        return new PublishResult(id, url, DateTimeOffset.UtcNow);
    }

    public async Task<PostStats> GetStatsAsync(string urn, PlatformCredential credential, CancellationToken ct)
    {
        credential = await EnsureFreshAsync(credential, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri(ApiBase, $"2/tweets/{Uri.EscapeDataString(urn)}?tweet.fields=public_metrics,non_public_metrics"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return new PostStats(0, 0, 0, 0, DateTimeOffset.UtcNow);

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return new PostStats(0, 0, 0, 0, DateTimeOffset.UtcNow);

        int impressions = 0, likes = 0, replies = 0, retweets = 0;
        if (data.TryGetProperty("public_metrics", out var pm))
        {
            if (pm.TryGetProperty("like_count", out var l)) likes = l.GetInt32();
            if (pm.TryGetProperty("reply_count", out var r)) replies = r.GetInt32();
            if (pm.TryGetProperty("retweet_count", out var rt)) retweets = rt.GetInt32();
        }
        if (data.TryGetProperty("non_public_metrics", out var npm) &&
            npm.TryGetProperty("impression_count", out var ic))
        {
            impressions = ic.GetInt32();
        }

        return new PostStats(impressions, likes, replies, retweets, DateTimeOffset.UtcNow);
    }

    public async IAsyncEnumerable<PastPost> ListPastPostsAsync(
        PlatformCredential credential,
        DateTimeOffset? sinceInclusive,
        int maxItems,
        [EnumeratorCancellation] CancellationToken ct)
    {
        credential = await EnsureFreshAsync(credential, ct);

        // /2/users/{id}/tweets pages via pagination_token. Returns newest first.
        string? pageToken = null;
        var yielded = 0;
        while (yielded < maxItems)
        {
            var url = $"2/users/{Uri.EscapeDataString(credential.SubjectId)}/tweets?max_results=100&tweet.fields=created_at,public_metrics";
            if (!string.IsNullOrEmpty(pageToken)) url += $"&pagination_token={Uri.EscapeDataString(pageToken)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, url));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger?.LogWarning("X list-posts failed {Status}", (int)resp.StatusCode);
                yield break;
            }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;

            foreach (var el in data.EnumerateArray())
            {
                var id = el.GetProperty("id").GetString() ?? "";
                var text = el.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                var createdAt = el.TryGetProperty("created_at", out var c) && DateTimeOffset.TryParse(c.GetString(), out var dt)
                    ? dt : DateTimeOffset.UtcNow;
                if (sinceInclusive is { } since && createdAt < since) yield break;

                PostStats? stats = null;
                if (el.TryGetProperty("public_metrics", out var pm))
                {
                    stats = new PostStats(
                        Impressions: 0,
                        Likes: pm.TryGetProperty("like_count", out var lk) ? lk.GetInt32() : 0,
                        Comments: pm.TryGetProperty("reply_count", out var rp) ? rp.GetInt32() : 0,
                        Shares: pm.TryGetProperty("retweet_count", out var rt) ? rt.GetInt32() : 0,
                        RetrievedAt: DateTimeOffset.UtcNow);
                }

                var handle = credential.SubjectId;
                yield return new PastPost(
                    Urn: id,
                    PostUrl: $"https://x.com/i/web/status/{id}",
                    Text: text,
                    MediaUrls: System.Array.Empty<string>(),
                    PublishedAt: createdAt,
                    Stats: stats);

                yielded++;
                if (yielded >= maxItems) yield break;
            }

            if (!doc.RootElement.TryGetProperty("meta", out var meta) ||
                !meta.TryGetProperty("next_token", out var nt))
                yield break;
            pageToken = nt.GetString();
            if (string.IsNullOrEmpty(pageToken)) yield break;
        }
    }

    public async IAsyncEnumerable<EngagementComment> ListCommentsAsync(
        string urn,
        PlatformCredential credential,
        int maxItems,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // X v2 user-context only exposes aggregate `reply_count` on a tweet, not the
        // replies themselves with author identities. To enumerate replies we'd need
        // to query /2/tweets/search/recent with conversation_id, which requires
        // Pro tier. Yield nothing for now so the UI degrades gracefully.
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<EngagementLike> ListLikesAsync(
        string urn,
        PlatformCredential credential,
        int maxItems,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Same story: /2/tweets/{id}/liking_users is on the Pro tier. Skip for v1.
        await Task.CompletedTask;
        yield break;
    }

    private async Task<PlatformCredential> EnsureFreshAsync(PlatformCredential credential, CancellationToken ct)
    {
        if (!credential.IsExpired || string.IsNullOrEmpty(credential.RefreshToken))
            return credential;

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credential.RefreshToken!,
            ["client_id"] = _options.ClientId
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = form };
        // X PKCE confidential-client refresh uses Basic auth with client_id:client_secret
        var basic = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger?.LogWarning("X token refresh failed {Status}", (int)resp.StatusCode);
            return credential;
        }

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 7200;
        var refreshed = doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : credential.RefreshToken;

        return credential with
        {
            AccessToken = accessToken,
            RefreshToken = refreshed,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            AcquiredAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// App-level X (Twitter) OAuth config. Populated from configuration
/// (<c>"Social:Twitter:ClientId"</c> etc.).
/// </summary>
public sealed record XOptions
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
}
