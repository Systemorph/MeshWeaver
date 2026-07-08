using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeshWeaver.Social;

/// <summary>
/// Thin, stateless wrapper over LinkedIn's <b>versioned REST</b> member-publishing surface —
/// the <c>w_member_social</c> "create a post on your behalf" flow:
///
/// <list type="bullet">
///   <item><description><c>POST https://api.linkedin.com/rest/posts</c> — publish a text share as the member.</description></item>
///   <item><description><c>GET  https://api.linkedin.com/rest/socialActions/{urn}</c> — read like + comment counts back.</description></item>
/// </list>
///
/// This is deliberately separate from <see cref="LinkedInPublisher"/> (which speaks the older
/// <c>/v2/ugcPosts</c> UGC API used by the scheduler subsystem). The versioned <c>/rest/posts</c>
/// endpoint is what the connect-endpoint publish path uses because it is the current, documented
/// member-post API and returns the created post URN in the <c>x-restli-id</c> response header.
///
/// Every method is a pure function of (HttpClient, credential, inputs) → outcome record, so the
/// HTTP boundary can be stubbed with a test <see cref="HttpMessageHandler"/> (no mesh, no mocking).
/// Callers own persistence: they write the returned <see cref="LinkedInPublishOutcome.Urn"/> /
/// counts back onto the mesh node.
/// </summary>
public static class LinkedInPostsApi
{
    /// <summary>
    /// LinkedIn versioned-API month header value (<c>LinkedIn-Version: YYYYMM</c>). LinkedIn pins
    /// each versioned endpoint to a month; bump this to a currently-supported version as LinkedIn
    /// rolls the window forward. Callers may override per-call.
    /// </summary>
    public const string DefaultApiVersion = "202506";

    private static readonly Uri PostsEndpoint = new("https://api.linkedin.com/rest/posts");
    private const string SocialActionsBase = "https://api.linkedin.com/rest/socialActions/";

    /// <summary>
    /// Normalizes a stored subject id into a member author URN. LinkedIn's <c>sub</c> claim from
    /// <c>/v2/userinfo</c> is the bare person id (e.g. <c>abc123</c>); the Posts API wants the full
    /// <c>urn:li:person:abc123</c>. An already-<c>urn:</c>-prefixed value (person OR organization)
    /// is returned unchanged.
    /// </summary>
    public static string NormalizeMemberUrn(string subjectId) =>
        string.IsNullOrEmpty(subjectId)
            ? subjectId
            : subjectId.StartsWith("urn:", StringComparison.Ordinal)
                ? subjectId
                : $"urn:li:person:{subjectId}";

    /// <summary>
    /// Publishes <paramref name="text"/> as a member post via <c>POST /rest/posts</c>. Returns the
    /// created post URN (from the <c>x-restli-id</c> header, falling back to <c>x-linkedin-id</c> or
    /// the response body <c>id</c>). On a non-2xx status the outcome carries the upstream status code
    /// and body verbatim — the caller surfaces it rather than swallowing.
    /// </summary>
    /// <param name="http">HTTP client (may be backed by a test handler).</param>
    /// <param name="credential">The member's LinkedIn credential (access token + subject id).</param>
    /// <param name="text">The post commentary (LinkedIn renders plain text; no markdown).</param>
    /// <param name="visibility">Post visibility: <c>PUBLIC</c> (default) or <c>CONNECTIONS</c>.</param>
    /// <param name="apiVersion">The <c>LinkedIn-Version</c> header value; defaults to <see cref="DefaultApiVersion"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<LinkedInPublishOutcome> PublishAsync(
        HttpClient http,
        PlatformCredential credential,
        string text,
        string? visibility,
        string? apiVersion,
        CancellationToken ct)
    {
        var author = NormalizeMemberUrn(credential.SubjectId);
        var vis = string.IsNullOrWhiteSpace(visibility) ? "PUBLIC" : visibility!.Trim();

        // Verbatim keys — an anonymous object serializes its property names exactly as written
        // (System.Text.Json applies no naming policy by default), so this is the wire body 1:1:
        // {"author":..,"commentary":..,"visibility":"PUBLIC","distribution":{"feedDistribution":
        //  "MAIN_FEED","targetEntities":[],"thirdPartyDistributionChannels":[]},
        //  "lifecycleState":"PUBLISHED","isReshareDisabledByAuthor":false}
        var body = new
        {
            author,
            commentary = text ?? "",
            visibility = vis,
            distribution = new
            {
                feedDistribution = "MAIN_FEED",
                targetEntities = Array.Empty<string>(),
                thirdPartyDistributionChannels = Array.Empty<string>()
            },
            lifecycleState = "PUBLISHED",
            isReshareDisabledByAuthor = false
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, PostsEndpoint)
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        req.Headers.Add("LinkedIn-Version", string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion!);
        req.Headers.Add("X-Restli-Protocol-Version", "2.0.0");

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return new LinkedInPublishOutcome(false, null, null, (int)resp.StatusCode, err);
        }

        var urn = FirstHeader(resp, "x-restli-id") ?? FirstHeader(resp, "x-linkedin-id");
        if (urn is null)
        {
            // Some responses echo the id in the body instead of / in addition to the header.
            try
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    urn = idEl.GetString();
            }
            catch (JsonException) { /* empty / non-JSON body — header was authoritative anyway */ }
        }

        var url = urn is null ? null : $"https://www.linkedin.com/feed/update/{urn}/";
        return new LinkedInPublishOutcome(true, urn, url, (int)resp.StatusCode, null);
    }

    /// <summary>
    /// Reads engagement (like + comment totals) for a previously-published post via
    /// <c>GET /rest/socialActions/{urn}</c>. Parses <c>likesSummary.totalLikes</c> and
    /// <c>commentsSummary.count</c> (with aggregate-field fallbacks LinkedIn has used across
    /// versions). On non-2xx the outcome carries the upstream status + body; counts are 0.
    /// </summary>
    public static async Task<LinkedInEngagementOutcome> GetSocialActionsAsync(
        HttpClient http,
        string urn,
        PlatformCredential credential,
        string? apiVersion,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(SocialActionsBase + Uri.EscapeDataString(urn)));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        req.Headers.Add("LinkedIn-Version", string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion!);
        req.Headers.Add("X-Restli-Protocol-Version", "2.0.0");

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return new LinkedInEngagementOutcome(false, 0, 0, (int)resp.StatusCode, err, DateTimeOffset.UtcNow);
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var likes = SummaryInt(root, "likesSummary", "totalLikes", "aggregatedTotalLikes");
        var comments = SummaryInt(root, "commentsSummary", "count", "aggregatedTotalComments");
        return new LinkedInEngagementOutcome(true, likes, comments, (int)resp.StatusCode, null, DateTimeOffset.UtcNow);
    }

    private static int SummaryInt(JsonElement root, string summaryProp, string primary, string fallback)
    {
        if (!root.TryGetProperty(summaryProp, out var summary) || summary.ValueKind != JsonValueKind.Object)
            return 0;
        if (summary.TryGetProperty(primary, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v))
            return v;
        if (summary.TryGetProperty(fallback, out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var v2))
            return v2;
        return 0;
    }

    private static string? FirstHeader(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var values)
            ? System.Linq.Enumerable.FirstOrDefault(values)
            : null;
}

/// <summary>
/// Outcome of a <see cref="LinkedInPostsApi.PublishAsync"/> call. On success <see cref="Urn"/> is the
/// created post URN; on failure <see cref="Success"/> is false and <see cref="Error"/> holds the
/// upstream body so the caller can surface LinkedIn's own reason.
/// </summary>
public sealed record LinkedInPublishOutcome(
    bool Success,
    string? Urn,
    string? PostUrl,
    int StatusCode,
    string? Error);

/// <summary>
/// Outcome of a <see cref="LinkedInPostsApi.GetSocialActionsAsync"/> call. On success the like /
/// comment counts are populated; on failure <see cref="Success"/> is false and <see cref="Error"/>
/// carries the upstream body.
/// </summary>
public sealed record LinkedInEngagementOutcome(
    bool Success,
    int LikeCount,
    int CommentCount,
    int StatusCode,
    string? Error,
    DateTimeOffset RetrievedAt);
