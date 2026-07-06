using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Social;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// LinkedIn <b>Community Management API</b> sync for company Pages the signed-in user
/// administers. Unlike the personal connect flow (identity only — <c>r_member_social</c>
/// is closed to apps), an <i>organization administrator</i> CAN read a Page's posts and
/// their engagement statistics over REST — but only if the LinkedIn app is approved for
/// the "Community Management API" product and the admin grants the org scopes.
///
/// <code>
///   GET /connect/linkedin/org?page={path}       start org OAuth (r_organization_social + rw_organization_admin)
///   GET /connect/linkedin/org/callback?code=…   store the org credential + discover the org URN
///   GET /connect/linkedin/sync?page={path}      pull posts + organizationalEntityShareStatistics -> Post nodes
/// </code>
///
/// The <c>/rest/</c> shapes below follow LinkedIn's documented Community Management API and
/// use the versioned header (<c>LinkedIn-Version</c>). They cannot be exercised until the
/// product is approved for the app, so every failure surfaces the upstream status + body
/// verbatim rather than swallowing — verify + adjust <see cref="ApiVersion"/> and the
/// response field names against the live product before relying on the numbers. This is
/// the deliberate design: fail loud, never silently produce zeros.
/// </summary>
public static class LinkedInPageSyncEndpoints
{
    private const string OrgStateCookie = "lnkd_org_state";
    private const string OrgCallbackPath = "/connect/linkedin/org/callback";

    /// <summary>
    /// LinkedIn versioned-API month header value (<c>LinkedIn-Version: YYYYMM</c>). LinkedIn
    /// pins each versioned endpoint to a month; bump this to a currently-supported version
    /// once the Community Management product is live for the app.
    /// </summary>
    private const string ApiVersion = "202406";

    // Read the org's posts + stats. rw_organization_admin lets us enumerate the admin's
    // Pages via organizationAcls so the operator never hand-looks-up a numeric org id.
    private const string OrgScopes = "r_organization_social rw_organization_admin";

    private static readonly Uri ApiBase = new("https://api.linkedin.com/");

    /// <summary>Registers the org-connect + Page-sync endpoints. Call alongside <c>MapLinkedInConnect()</c>.</summary>
    public static IEndpointRouteBuilder MapLinkedInPageSync(this IEndpointRouteBuilder endpoints)
    {
        // 1) Start the org OAuth flow for a specific Page node.
        endpoints.MapGet("/connect/linkedin/org", (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string page,
            IConfiguration config) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString });

            var clientId = config["Social:LinkedIn:ClientId"];
            if (string.IsNullOrEmpty(clientId))
                return Results.Problem("LinkedIn client id is not configured (Social:LinkedIn:ClientId).", statusCode: 500);
            if (string.IsNullOrWhiteSpace(page))
                return Results.BadRequest("page query parameter is required.");

            var state = GenerateState();
            http.Response.Cookies.Append(OrgStateCookie,
                WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes($"{state}|{page}")),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.FromMinutes(10)
                });

            var redirectUri = $"{http.Request.Scheme}://{http.Request.Host}{OrgCallbackPath}";
            var url = "https://www.linkedin.com/oauth/v2/authorization?response_type=code"
                + $"&client_id={Uri.EscapeDataString(clientId!)}"
                + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                + $"&state={Uri.EscapeDataString(state)}"
                + "&scope=" + Uri.EscapeDataString(OrgScopes);

            return Results.Redirect(url);
        }).RequireAuthorization();

        // 2) Callback — exchange the code, store the org credential, discover the org URN.
        endpoints.MapGet(OrgCallbackPath, async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? code,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? state,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? error,
            IConfiguration config,
            IHttpClientFactory httpFactory,
            IMeshService mesh,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("LinkedInPageSync");

            if (!string.IsNullOrEmpty(error))
                return Results.Redirect($"/?connect=linkedin-org-error&reason={Uri.EscapeDataString(error)}");
            if (!http.Request.Cookies.TryGetValue(OrgStateCookie, out var cookieValue) || string.IsNullOrEmpty(cookieValue))
                return Results.BadRequest("Missing org connect state cookie (CSRF).");
            http.Response.Cookies.Delete(OrgStateCookie);

            string cookieState, pagePath;
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cookieValue));
                var parts = decoded.Split('|', 2);
                cookieState = parts[0];
                pagePath = parts[1];
            }
            catch
            {
                return Results.BadRequest("Bad state cookie.");
            }

            if (!string.Equals(cookieState, state, StringComparison.Ordinal))
                return Results.BadRequest("State mismatch (CSRF).");
            if (string.IsNullOrEmpty(code))
                return Results.BadRequest("No authorization code.");

            var clientId = config["Social:LinkedIn:ClientId"]!;
            var clientSecret = config["Social:LinkedIn:ClientSecret"] ?? "";
            var client = httpFactory.CreateClient();
            var redirectUri = $"{http.Request.Scheme}://{http.Request.Host}{OrgCallbackPath}";

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            });

            using var tokenResp = await client.PostAsync("https://www.linkedin.com/oauth/v2/accessToken", form, http.RequestAborted);
            if (!tokenResp.IsSuccessStatusCode)
            {
                var body = await tokenResp.Content.ReadAsStringAsync(http.RequestAborted);
                logger.LogWarning("LinkedIn org token exchange failed {Status}: {Body}", (int)tokenResp.StatusCode, body);
                return Results.Redirect($"/{pagePath}?connect=linkedin-org-error&stage=token&reason={Uri.EscapeDataString(ShortReason(body))}");
            }

            using var doc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync(http.RequestAborted));
            var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var scope = doc.RootElement.TryGetProperty("scope", out var sc) ? sc.GetString() : OrgScopes;

            // Best-effort: discover the first Page this admin controls so the sync knows
            // which org to pull. If discovery fails (product not yet approved), we still
            // store the credential — the sync surfaces the org-resolution error later.
            var orgUrn = await TryDiscoverFirstOrgAsync(client, accessToken, http.RequestAborted, logger);

            var credential = new PlatformCredential
            {
                Platform = LinkedInPublisher.PlatformId,
                SubjectId = orgUrn ?? "urn:li:organization:unknown",
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                Scope = scope,
                AcquiredAt = DateTimeOffset.UtcNow,
            };

            var credentialNode = new MeshNode("linkedin", pagePath + "/_ApiCredentials")
            {
                Name = "LinkedIn credential",
                NodeType = ApiCredentialNodeType.NodeType,
                Content = credential,
                State = MeshNodeState.Active,
            };

            var result = await mesh.CreateOrUpdateNode(credentialNode)
                .Select(_ => Results.Redirect($"/{pagePath}?connect=linkedin-org-ok"))
                .Catch<IResult, Exception>(ex =>
                {
                    logger.LogWarning(ex, "Org credential persist failed at {Path}", credentialNode.Path);
                    return Observable.Return(Results.Redirect($"/{pagePath}?connect=linkedin-org-error&stage=credential&reason=persist-failed"));
                })
                .FirstAsync()
                .ToTask(http.RequestAborted);

            logger.LogInformation("Stored LinkedIn org credential for {Page} (org {Org})", pagePath, orgUrn ?? "(undiscovered)");
            return result;
        });

        // 3) Sync — pull the Page's posts + share statistics into Post nodes under the Page.
        endpoints.MapGet("/connect/linkedin/sync", async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string page,
            IHttpClientFactory httpFactory,
            IMessageHub hub,
            IMeshService mesh,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("LinkedInPageSync");
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString });
            if (string.IsNullOrWhiteSpace(page))
                return Results.BadRequest("page query parameter is required.");

            // Read the stored org credential (typed — ApiCredentialNodeType registers PlatformCredential).
            PlatformCredential? credential;
            try
            {
                credential = await hub.GetMeshNodeStream(page + "/_ApiCredentials/linkedin")
                    .Select(n => n?.Content as PlatformCredential)
                    .Where(c => c is not null)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(10))
                    .FirstAsync()
                    .ToTask(http.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No LinkedIn org credential at {Page}/_ApiCredentials/linkedin", page);
                return Results.Redirect($"/{page}?sync=error&reason={Uri.EscapeDataString("not-connected")}");
            }

            var orgUrn = credential!.SubjectId;
            if (string.IsNullOrEmpty(orgUrn) || !orgUrn.StartsWith("urn:li:organization:"))
                return Results.Redirect($"/{page}?sync=error&reason={Uri.EscapeDataString("org-urn-missing-reconnect")}");

            var client = httpFactory.CreateClient();

            // Pull posts + stats. Any upstream failure surfaces its status verbatim.
            List<OrgPost> posts;
            Dictionary<string, OrgStats> statsByUrn;
            try
            {
                posts = await FetchOrgPostsAsync(client, orgUrn, credential.AccessToken, 100, http.RequestAborted, logger);
                statsByUrn = await FetchOrgStatsAsync(client, orgUrn, credential.AccessToken, http.RequestAborted, logger);
            }
            catch (LinkedInApiException apiEx)
            {
                logger.LogWarning("LinkedIn Page sync failed {Status}: {Body}", apiEx.StatusCode, apiEx.Body);
                return Results.Redirect($"/{page}?sync=error&stage={apiEx.Stage}&status={apiEx.StatusCode}&reason={Uri.EscapeDataString(ShortReason(apiEx.Body))}");
            }

            // Write one Post node per fetched post; merge stats by share URN.
            var written = 0;
            foreach (var post in posts)
            {
                statsByUrn.TryGetValue(post.Urn, out var stats);
                var postNode = new MeshNode(SanitizeId(post.Urn), page + "/posts")
                {
                    Name = Title(post.Text),
                    NodeType = "Systemorph/Post",
                    State = MeshNodeState.Active,
                    Content = new Dictionary<string, object?>
                    {
                        ["$type"] = "SocialMediaPost",
                        ["title"] = Title(post.Text),
                        ["body"] = post.Text,
                        ["profilePath"] = page,
                        ["platform"] = "LinkedIn",
                        ["publishedAt"] = post.PublishedAt,
                        ["impressions"] = stats?.Impressions ?? 0,
                        ["likes"] = stats?.Likes ?? 0,
                        ["comments"] = stats?.Comments ?? 0,
                        ["mediaUrl"] = post.MediaUrl,
                    }
                };

                try
                {
                    await mesh.CreateOrUpdateNode(postNode).FirstAsync().ToTask(http.RequestAborted);
                    written++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to write Post node {Path}", postNode.Path);
                }
            }

            logger.LogInformation("LinkedIn Page sync for {Page}: {Written}/{Total} posts written", page, written, posts.Count);
            return Results.Redirect($"/{page}?sync=ok&posts={written}");
        }).RequireAuthorization();

        return endpoints;
    }

    // ---- LinkedIn Community Management API calls (versioned /rest/ surface) ----

    /// <summary>
    /// Discovers the first organization the caller administers via
    /// <c>GET /rest/organizationAcls?q=roleAssignee&amp;role=ADMINISTRATOR&amp;state=APPROVED</c>.
    /// Returns the org URN or null if none / not permitted.
    /// </summary>
    private static async Task<string?> TryDiscoverFirstOrgAsync(HttpClient client, string accessToken, CancellationToken ct, ILogger logger)
    {
        try
        {
            using var req = Versioned(HttpMethod.Get, "rest/organizationAcls?q=roleAssignee&role=ADMINISTRATOR&state=APPROVED", accessToken);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogInformation("organizationAcls discovery returned {Status} — org URN left undiscovered", (int)resp.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("elements", out var els) && els.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in els.EnumerateArray())
                {
                    if (el.TryGetProperty("organizationalTarget", out var t) && t.ValueKind == JsonValueKind.String)
                        return t.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "organizationAcls discovery threw — org URN left undiscovered");
        }
        return null;
    }

    /// <summary>
    /// Fetches the Page's posts via <c>GET /rest/posts?q=author&amp;author={orgUrn}</c>, paging until
    /// exhausted or <paramref name="maxItems"/> reached. Throws <see cref="LinkedInApiException"/> on
    /// a non-success status so the sync surfaces the real reason.
    /// </summary>
    private static async Task<List<OrgPost>> FetchOrgPostsAsync(
        HttpClient client, string orgUrn, string accessToken, int maxItems, CancellationToken ct, ILogger logger)
    {
        var result = new List<OrgPost>();
        var pageSize = 50;
        var start = 0;
        while (result.Count < maxItems)
        {
            var path = $"rest/posts?q=author&author={Uri.EscapeDataString(orgUrn)}&count={pageSize}&start={start}";
            using var req = Versioned(HttpMethod.Get, path, accessToken);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new LinkedInApiException("posts", (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("elements", out var els) || els.GetArrayLength() == 0)
                break;

            var count = 0;
            foreach (var el in els.EnumerateArray())
            {
                count++;
                var urn = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(urn)) continue;

                var text = el.TryGetProperty("commentary", out var cEl) && cEl.ValueKind == JsonValueKind.String
                    ? cEl.GetString() ?? ""
                    : "";
                var publishedAt = el.TryGetProperty("publishedAt", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(pEl.GetInt64())
                    : (el.TryGetProperty("createdAt", out var caEl) && caEl.ValueKind == JsonValueKind.Number
                        ? DateTimeOffset.FromUnixTimeMilliseconds(caEl.GetInt64())
                        : DateTimeOffset.UtcNow);

                result.Add(new OrgPost(urn!, text, publishedAt, null));
                if (result.Count >= maxItems) break;
            }

            if (count < pageSize) break;
            start += pageSize;
        }
        return result;
    }

    /// <summary>
    /// Fetches per-post engagement via
    /// <c>GET /rest/organizationalEntityShareStatistics?q=organizationalEntity&amp;organizationalEntity={orgUrn}</c>,
    /// returning a map keyed by the post/share URN. Throws <see cref="LinkedInApiException"/> on failure.
    /// </summary>
    private static async Task<Dictionary<string, OrgStats>> FetchOrgStatsAsync(
        HttpClient client, string orgUrn, string accessToken, CancellationToken ct, ILogger logger)
    {
        var map = new Dictionary<string, OrgStats>(StringComparer.Ordinal);
        var path = $"rest/organizationalEntityShareStatistics?q=organizationalEntity&organizationalEntity={Uri.EscapeDataString(orgUrn)}";
        using var req = Versioned(HttpMethod.Get, path, accessToken);
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new LinkedInApiException("stats", (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("elements", out var els) || els.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var el in els.EnumerateArray())
        {
            // Per-share rows carry a "share" or "ugcPost" URN; the aggregate row carries neither.
            var shareUrn = el.TryGetProperty("share", out var shEl) && shEl.ValueKind == JsonValueKind.String
                ? shEl.GetString()
                : el.TryGetProperty("ugcPost", out var ugcEl) && ugcEl.ValueKind == JsonValueKind.String
                    ? ugcEl.GetString()
                    : null;
            if (string.IsNullOrEmpty(shareUrn)) continue;
            if (!el.TryGetProperty("totalShareStatistics", out var t)) continue;

            map[shareUrn!] = new OrgStats(
                Impressions: IntOf(t, "impressionCount"),
                Likes: IntOf(t, "likeCount"),
                Comments: IntOf(t, "commentCount"));
        }
        return map;
    }

    private static HttpRequestMessage Versioned(HttpMethod method, string relativePath, string accessToken)
    {
        var req = new HttpRequestMessage(method, new Uri(ApiBase, relativePath));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Add("X-Restli-Protocol-Version", "2.0.0");
        req.Headers.Add("LinkedIn-Version", ApiVersion);
        return req;
    }

    private static int IntOf(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string GenerateState()
    {
        Span<byte> buf = stackalloc byte[24];
        RandomNumberGenerator.Fill(buf);
        return WebEncoders.Base64UrlEncode(buf);
    }

    private static string ShortReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString() ?? "error";
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString() ?? "error";
        }
        catch { /* non-JSON */ }
        return "linkedin-error";
    }

    private static string SanitizeId(string urn)
    {
        // urn:li:share:123 / urn:li:ugcPost:123 -> share-123 / ugcPost-123
        var tail = urn.Contains("li:") ? urn[(urn.IndexOf("li:", StringComparison.Ordinal) + 3)..] : urn;
        var id = tail.Replace(':', '-');
        return string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
    }

    private static string Title(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(untitled post)";
        var firstLine = text.Split('\n', 2)[0].Trim();
        return firstLine.Length <= 90 ? firstLine : firstLine[..90].TrimEnd() + "…";
    }

    private sealed record OrgPost(string Urn, string Text, DateTimeOffset PublishedAt, string? MediaUrl);

    private sealed record OrgStats(int Impressions, int Likes, int Comments);

    private sealed class LinkedInApiException : Exception
    {
        public LinkedInApiException(string stage, int statusCode, string body)
            : base($"LinkedIn {stage} {statusCode}")
        {
            Stage = stage;
            StatusCode = statusCode;
            Body = body;
        }

        public string Stage { get; }
        public int StatusCode { get; }
        public string Body { get; }
    }
}
