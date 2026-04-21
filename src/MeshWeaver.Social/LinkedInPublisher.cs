using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Social;

/// <summary>
/// LinkedIn implementation of <see cref="IPlatformPublisher"/>. Uses the UGC Posts API
/// for publishing (<c>POST /v2/ugcPosts</c>), the member-posts endpoint for history
/// (<c>GET /rest/memberSnapshotData</c> or <c>GET /v2/ugcPosts?q=authors</c>), and the
/// socialActions endpoint for stats.
///
/// Scope requirements (configured on the OAuth app):
///   r_member_social — read member's own posts, reactions, comments
///   w_member_social — create posts, comments on behalf of member
///
/// Token refresh uses the standard OAuth2 refresh_token grant against
/// https://www.linkedin.com/oauth/v2/accessToken. Publishers never persist the
/// refreshed token themselves — they return the new <see cref="PlatformCredential"/>
/// for the caller to store back into the mesh.
/// </summary>
public sealed class LinkedInPublisher : IPlatformPublisher
{
    public const string PlatformId = "LinkedIn";
    public string Platform => PlatformId;

    private static readonly Uri ApiBase = new("https://api.linkedin.com/");
    private static readonly Uri TokenEndpoint = new("https://www.linkedin.com/oauth/v2/accessToken");

    private readonly HttpClient _http;
    private readonly ILogger<LinkedInPublisher>? _logger;
    private readonly LinkedInOptions _options;

    public LinkedInPublisher(HttpClient http, LinkedInOptions options, ILogger<LinkedInPublisher>? logger = null)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<PublishResult> PublishAsync(PlatformPublishRequest request, CancellationToken ct)
    {
        var credential = await EnsureFreshAsync(request.Credential, ct);
        var authorUrn = credential.SubjectId.StartsWith("urn:")
            ? credential.SubjectId
            : $"urn:li:person:{credential.SubjectId}";

        // Upload media first (LinkedIn requires a registerUpload → PUT binary flow before referencing in ugcPosts).
        // For the first cut we only support text + external image URL rendered as ARTICLE.
        // Full binary upload is a follow-up once auth + simple posting is verified end-to-end.
        object media = request.MediaUrls.Count == 0
            ? new { shareMediaCategory = "NONE" }
            : new
            {
                shareMediaCategory = "ARTICLE",
                media = request.MediaUrls.Select(url => new
                {
                    status = "READY",
                    originalUrl = url
                }).ToArray()
            };

        var body = new
        {
            author = authorUrn,
            lifecycleState = "PUBLISHED",
            specificContent = new Dictionary<string, object>
            {
                ["com.linkedin.ugc.ShareContent"] = new
                {
                    shareCommentary = new { text = request.Text },
                    shareMediaCategory = ((dynamic)media).shareMediaCategory,
                    media = request.MediaUrls.Count == 0
                        ? null
                        : ((dynamic)media).media
                }
            },
            visibility = new Dictionary<string, object>
            {
                ["com.linkedin.ugc.MemberNetworkVisibility"] = "PUBLIC"
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, "v2/ugcPosts"))
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        req.Headers.Add("X-Restli-Protocol-Version", "2.0.0");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body2 = await resp.Content.ReadAsStringAsync(ct);
            _logger?.LogWarning("LinkedIn publish failed {Status}: {Body}", (int)resp.StatusCode, body2);
            return new PublishResult(null, null, DateTimeOffset.UtcNow,
                Error: $"LinkedIn {(int)resp.StatusCode}: {body2}");
        }

        // LinkedIn returns the URN in the "x-restli-id" header or response body "id" field.
        string? urn = null;
        if (resp.Headers.TryGetValues("x-restli-id", out var ids))
            urn = System.Linq.Enumerable.FirstOrDefault(ids);
        if (urn is null)
        {
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("id", out var idEl))
                urn = idEl.GetString();
        }

        var url = urn is null ? null : $"https://www.linkedin.com/feed/update/{urn}/";
        return new PublishResult(urn, url, DateTimeOffset.UtcNow);
    }

    public async Task<PostStats> GetStatsAsync(string urn, PlatformCredential credential, CancellationToken ct)
    {
        credential = await EnsureFreshAsync(credential, ct);

        // /v2/socialActions/{urn} returns likes + comments counts for any UGC post the caller can read.
        // Impressions are NOT available via the member-scoped API — only page/organizational posts expose
        // organizationalEntityShareStatistics. For member posts, impressions stay 0 until the user also
        // grants an analytics scope; we record that gap in the result rather than throw.
        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri(ApiBase, $"v2/socialActions/{Uri.EscapeDataString(urn)}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        req.Headers.Add("X-Restli-Protocol-Version", "2.0.0");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger?.LogWarning("LinkedIn stats fetch failed {Status} for {Urn}", (int)resp.StatusCode, urn);
            return new PostStats(0, 0, 0, 0, DateTimeOffset.UtcNow);
        }

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var likes = doc.RootElement.TryGetProperty("likesSummary", out var l) && l.TryGetProperty("totalLikes", out var lt)
            ? lt.GetInt32() : 0;
        var comments = doc.RootElement.TryGetProperty("commentsSummary", out var c) && c.TryGetProperty("aggregatedTotalComments", out var ct1)
            ? ct1.GetInt32() : 0;

        return new PostStats(Impressions: 0, Likes: likes, Comments: comments, Shares: 0, RetrievedAt: DateTimeOffset.UtcNow);
    }

    public async IAsyncEnumerable<PastPost> ListPastPostsAsync(
        PlatformCredential credential,
        DateTimeOffset? sinceInclusive,
        int maxItems,
        [EnumeratorCancellation] CancellationToken ct)
    {
        credential = await EnsureFreshAsync(credential, ct);

        var authorUrn = credential.SubjectId.StartsWith("urn:")
            ? credential.SubjectId
            : $"urn:li:person:{credential.SubjectId}";

        // /v2/ugcPosts?q=authors&authors[0]={urn}&sortBy=CREATED&count=50
        // Paged via start=N; loop until count < pageSize or maxItems reached.
        var pageSize = 50;
        var start = 0;
        var yielded = 0;
        while (yielded < maxItems)
        {
            var url = $"v2/ugcPosts?q=authors&authors=List({Uri.EscapeDataString(authorUrn)})&sortBy=CREATED&count={pageSize}&start={start}";
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, url));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            req.Headers.Add("X-Restli-Protocol-Version", "2.0.0");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger?.LogWarning("LinkedIn list-posts failed {Status}", (int)resp.StatusCode);
                yield break;
            }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("elements", out var elems) || elems.GetArrayLength() == 0)
                yield break;

            var count = 0;
            foreach (var el in elems.EnumerateArray())
            {
                count++;
                if (!el.TryGetProperty("id", out var idEl)) continue;
                var urn = idEl.GetString();
                if (urn is null) continue;

                var createdAt = el.TryGetProperty("created", out var cEl) && cEl.TryGetProperty("time", out var cT)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(cT.GetInt64()) : DateTimeOffset.UtcNow;

                if (sinceInclusive is { } since && createdAt < since) yield break;

                var text = "";
                if (el.TryGetProperty("specificContent", out var sc) &&
                    sc.TryGetProperty("com.linkedin.ugc.ShareContent", out var share) &&
                    share.TryGetProperty("shareCommentary", out var sComm) &&
                    sComm.TryGetProperty("text", out var tEl))
                {
                    text = tEl.GetString() ?? "";
                }

                var mediaUrls = new List<string>();
                if (el.TryGetProperty("specificContent", out var sc2) &&
                    sc2.TryGetProperty("com.linkedin.ugc.ShareContent", out var share2) &&
                    share2.TryGetProperty("media", out var mEl) && mEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mEl.EnumerateArray())
                    {
                        if (m.TryGetProperty("originalUrl", out var ou) && ou.ValueKind == JsonValueKind.String)
                            mediaUrls.Add(ou.GetString()!);
                    }
                }

                yield return new PastPost(
                    Urn: urn,
                    PostUrl: $"https://www.linkedin.com/feed/update/{urn}/",
                    Text: text,
                    MediaUrls: mediaUrls,
                    PublishedAt: createdAt,
                    Stats: null);

                yielded++;
                if (yielded >= maxItems) yield break;
            }

            if (count < pageSize) yield break;
            start += pageSize;
        }
    }

    private async Task<PlatformCredential> EnsureFreshAsync(PlatformCredential credential, CancellationToken ct)
    {
        if (!credential.IsExpired || string.IsNullOrEmpty(credential.RefreshToken))
            return credential;

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credential.RefreshToken!,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = form };
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var b = await resp.Content.ReadAsStringAsync(ct);
            _logger?.LogWarning("LinkedIn token refresh failed {Status}: {Body}", (int)resp.StatusCode, b);
            return credential; // caller will see 401 on next API call and surface the error
        }

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var eIn) ? eIn.GetInt32() : 3600;
        var refreshed = doc.RootElement.TryGetProperty("refresh_token", out var rtEl) ? rtEl.GetString() : credential.RefreshToken;

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
/// App-level LinkedIn OAuth config. Populated from configuration
/// (e.g. <c>"Social:LinkedIn:ClientId"</c>). Shared between the OAuth middleware
/// (for sign-in) and the publisher (for refresh).
/// </summary>
public sealed record LinkedInOptions
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
}
