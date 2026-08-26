using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Social;

/// <summary>
/// OAuth2 authorization-code flow for connecting a LinkedIn publishing identity
/// to a profile in the mesh. This is the Social module's endpoint contribution
/// (<see cref="SocialModuleAttribute"/>) — just the parts that need to live in a
/// compiled binary because they involve browser cookies, HTTP routing, and a
/// callback URL whitelisted on LinkedIn:
///
///   GET /connect/linkedin/me                    — convenience: redirect into the flow for the signed-in user
///   GET /connect/linkedin?profile={path}        — start the flow (sets CSRF cookie, redirects to LinkedIn)
///   GET /connect/linkedin/callback?code=…       — finish the flow, persist credential + LinkedInProfile node
///
/// Everything else (analytics dashboard, CSV telemetry import) lives as Code on the
/// <c>LinkedIn/LinkedInProfile</c> NodeType — see the <c>LinkedInProfileLayoutAreas</c>
/// Code piece. That keeps the deployed binary stable while the dashboard/ingest logic
/// can be edited without a deploy.
/// </summary>
public static class LinkedInConnectEndpoints
{
    /// <summary>Name of the CSRF state cookie set when starting the connect flow.</summary>
    public const string StateCookieName = "lnkd_connect_state";
    private const string CallbackPath = "/connect/linkedin/callback";

    /// <summary>
    /// The scopes the connect flow ALWAYS requests, and the only ones publishing needs.
    ///
    /// <list type="bullet">
    ///   <item><c>openid</c>/<c>profile</c>/<c>email</c> — OIDC sign-in plus the member's <c>sub</c>
    ///     (person id), persisted as the credential's <c>SubjectId</c> so publishing knows the
    ///     author (see <c>LinkedInPostsApi.NormalizeMemberUrn</c>).</item>
    ///   <item><c>w_member_social</c> — "Create, modify, and delete posts, comments, and reactions
    ///     on your behalf". This is the publishing scope, and the consent-screen line the member
    ///     actually approves.</item>
    /// </list>
    /// </summary>
    public const string BaseScopes = "openid profile email w_member_social";

    /// <summary>
    /// The scope that makes IMPRESSIONS (views) and reshares readable for a member's OWN posts
    /// (<c>/rest/memberCreatorPostAnalytics</c>). Without it the stats refresh reports likes and
    /// comments only.
    ///
    /// <para>🚨 <b>Requesting it is OPT-IN, and the default is OFF (issue #51).</b> LinkedIn
    /// rejects the whole authorization — before any sign-in or consent screen — when the app is not
    /// approved for the Member Post Analytics product: the member sees "Bummer, something went
    /// wrong" and is bounced back, so NO new member can connect at all and publishing, which never
    /// needed this scope, is blocked by a request for analytics. Approval is granted per LinkedIn
    /// app, so whether it may be asked for is a property of the DEPLOYMENT, not of this code — a
    /// deployment whose app carries the product sets
    /// <see cref="RequestPostAnalyticsConfigKey"/> to <c>true</c>. Credentials connected while it
    /// was requested keep working; the ones without it simply report no impressions, and the
    /// callback says so (<c>analytics=unavailable</c>) instead of leaving the member to wonder.</para>
    /// </summary>
    public const string PostAnalyticsScope = "r_member_postAnalytics";

    /// <summary>
    /// Configuration key opting a deployment INTO requesting <see cref="PostAnalyticsScope"/>:
    /// <c>Social:LinkedIn:RequestPostAnalytics</c>. Absent or false — the default — requests
    /// <see cref="BaseScopes"/> only.
    /// </summary>
    public const string RequestPostAnalyticsConfigKey = LinkedInOptions.SectionName + ":RequestPostAnalytics";

    /// <summary>
    /// The space-separated scope string to request. Pure, so the one thing that decides whether a
    /// member can connect at all is unit-tested rather than read off a URL in a browser.
    /// </summary>
    /// <param name="requestPostAnalytics">Whether this deployment's LinkedIn app is approved for
    /// the Member Post Analytics product.</param>
    public static string BuildScope(bool requestPostAnalytics) =>
        requestPostAnalytics ? BaseScopes + " " + PostAnalyticsScope : BaseScopes;

    /// <summary>Whether this deployment opted into requesting the analytics scope.</summary>
    /// <param name="config">The host configuration.</param>
    public static bool WantsPostAnalytics(IConfiguration config) =>
        bool.TryParse(config[RequestPostAnalyticsConfigKey], out var wanted) && wanted;

    /// <summary>
    /// Whether an authorization LinkedIn granted actually carries <see cref="PostAnalyticsScope"/>.
    /// LinkedIn returns the granted scopes comma-separated on the token response; a member who
    /// declined the analytics line (or an app that was never approved for it) still gets a working
    /// publishing credential, and the difference must be SAID rather than surfacing later as
    /// permanently-zero impressions.
    /// </summary>
    /// <param name="grantedScope">The <c>scope</c> field of LinkedIn's token response.</param>
    public static bool GrantsPostAnalytics(string? grantedScope) =>
        grantedScope?.Contains(PostAnalyticsScope, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Registers the LinkedIn connect endpoints.</summary>
    public static IEndpointRouteBuilder MapLinkedInConnect(this IEndpointRouteBuilder endpoints)
    {
        // Convenience: bind the credential to the authenticated user's own User node.
        endpoints.MapGet("/connect/linkedin/me", (HttpContext http) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = "/connect/linkedin/me" });
            var user = http.User.Identity!.Name ?? "anonymous";
            return Results.Redirect($"/connect/linkedin?profile=User/{Uri.EscapeDataString(user)}");
        }).RequireAuthorization();

        endpoints.MapGet("/connect/linkedin", (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string profile,
            [Microsoft.AspNetCore.Mvc.FromServices] IConfiguration config) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString });

            var clientId = config["Social:LinkedIn:ClientId"];
            if (string.IsNullOrEmpty(clientId))
                return Results.Problem("LinkedIn client id is not configured (Social:LinkedIn:ClientId).", statusCode: 500);

            if (string.IsNullOrWhiteSpace(profile))
                return Results.BadRequest("profile query parameter is required.");

            var state = GenerateState();
            http.Response.Cookies.Append(StateCookieName,
                WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes($"{state}|{profile}")),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.FromMinutes(10)
                });

            var redirectUri = BuildRedirectUri(http);
            var url = "https://www.linkedin.com/oauth/v2/authorization?response_type=code"
                + $"&client_id={Uri.EscapeDataString(clientId!)}"
                + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                + $"&state={Uri.EscapeDataString(state)}"
                + "&scope=" + Uri.EscapeDataString(BuildScope(WantsPostAnalytics(config)));

            return Results.Redirect(url);
        }).RequireAuthorization();

        // DI parameters are EXPLICIT ([FromServices]) throughout these module-contributed
        // endpoints: parameter inference classifies a service parameter by asking the host's
        // container (IServiceProviderIsService) at endpoint-materialization time, and a module
        // must not depend on what the host happens to have registered to keep its parameters
        // from degrading into inferred-body binding.
        endpoints.MapGet(CallbackPath, async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? code,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? state,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? error,
            [Microsoft.AspNetCore.Mvc.FromServices] IConfiguration config,
            [Microsoft.AspNetCore.Mvc.FromServices] IHttpClientFactory httpFactory,
            [Microsoft.AspNetCore.Mvc.FromServices] IMeshService mesh,
            [Microsoft.AspNetCore.Mvc.FromServices] IMessageHub hub,
            [Microsoft.AspNetCore.Mvc.FromServices] ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("LinkedInConnect");

            // 🚨 The state cookie is read BEFORE the error branch, and that ordering is the fix
            // (issue #51). LinkedIn's refusals — a scope the app is not approved for, a member who
            // declines consent — come back through `error`, and answering them at "/" threw away
            // the one thing that makes the refusal actionable: WHICH profile was being connected.
            // The member landed on the home page with a query string nothing renders, so a
            // deterministic, reproducible refusal read as "it just doesn't work".
            if (!http.Request.Cookies.TryGetValue(StateCookieName, out var cookieValue) || string.IsNullOrEmpty(cookieValue))
                return Results.BadRequest("Missing connect state cookie (CSRF).");
            http.Response.Cookies.Delete(StateCookieName);

            string cookieState, profilePath;
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cookieValue));
                var parts = decoded.Split('|', 2);
                cookieState = parts[0];
                profilePath = parts[1];
            }
            catch
            {
                return Results.BadRequest("Bad state cookie.");
            }

            if (!string.IsNullOrEmpty(error))
            {
                logger.LogWarning(
                    "LinkedIn refused the authorization for {Profile}: {Error} (requested scope '{Scope}')",
                    profilePath, error, BuildScope(WantsPostAnalytics(config)));
                return Results.Redirect(
                    $"/{profilePath}/LinkedIn?connect=linkedin-error&stage=authorize"
                    + $"&reason={Uri.EscapeDataString(error!)}"
                    + $"&scope={Uri.EscapeDataString(BuildScope(WantsPostAnalytics(config)))}");
            }

            if (!string.Equals(cookieState, state, StringComparison.Ordinal))
                return Results.BadRequest("State mismatch (CSRF).");
            if (string.IsNullOrEmpty(code))
                return Results.BadRequest("No authorization code.");

            var clientId = config["Social:LinkedIn:ClientId"]!;
            var clientSecret = config["Social:LinkedIn:ClientSecret"] ?? "";

            var http2 = httpFactory.CreateClient();
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = BuildRedirectUri(http),
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            });

            using var tokenResp = await http2.PostAsync("https://www.linkedin.com/oauth/v2/accessToken", form, http.RequestAborted);
            if (!tokenResp.IsSuccessStatusCode)
            {
                var body = await tokenResp.Content.ReadAsStringAsync(http.RequestAborted);
                logger.LogWarning("LinkedIn token exchange failed {Status}: {Body}", (int)tokenResp.StatusCode, body);
                // Friendly landing instead of raw Bad Gateway JSON — pass the reason
                // so the profile page can show a visible banner.
                var reason = ExtractLinkedInErrorReason(body);
                return Results.Redirect($"/{profilePath}/LinkedIn?connect=linkedin-error&stage=token&reason={Uri.EscapeDataString(reason)}");
            }

            using var doc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync(http.RequestAborted));
            var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var scope = doc.RootElement.TryGetProperty("scope", out var sc) ? sc.GetString() : null;

            using var uiReq = new HttpRequestMessage(HttpMethod.Get, "https://api.linkedin.com/v2/userinfo");
            uiReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var uiResp = await http2.SendAsync(uiReq, http.RequestAborted);
            if (!uiResp.IsSuccessStatusCode)
                return Results.Redirect($"/{profilePath}/LinkedIn?connect=linkedin-error&stage=userinfo&reason={Uri.EscapeDataString("userinfo-" + (int)uiResp.StatusCode)}");

            using var uiDoc = JsonDocument.Parse(await uiResp.Content.ReadAsStringAsync(http.RequestAborted));
            var subject = uiDoc.RootElement.GetProperty("sub").GetString()!;
            var displayName = uiDoc.RootElement.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            var pictureUrl = uiDoc.RootElement.TryGetProperty("picture", out var pic) ? pic.GetString() : null;
            var emailAddress = uiDoc.RootElement.TryGetProperty("email", out var em) ? em.GetString() : null;

            var credential = new PlatformCredential
            {
                Platform = LinkedInPublisher.PlatformId,
                SubjectId = subject,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                Scope = scope,
                AcquiredAt = DateTimeOffset.UtcNow,
            };

            // Persist under {profilePath}/_ApiCredentials/linkedin.
            var credentialNode = new MeshNode("linkedin", profilePath + "/_ApiCredentials")
            {
                Name = "LinkedIn credential",
                NodeType = PlatformCredential.ApiCredentialNodeType,
                Content = credential,
                State = MeshNodeState.Active,
            };
            // Upsert the LinkedInProfile node so the analytics dashboard has somewhere to render.
            // Loose JSON content avoids a hard dependency on the dynamic LinkedInProfile content
            // type from this assembly — and it is JSON, not a Dictionary, because a dictionary's
            // "$type" entry is DISCARDED on write and replaced by the dictionary's own CLR
            // collection name (issue #52; see NodeContentJson). This node carried the same defect
            // as the profile the issue reported: its dashboard had no typed content to read.
            var profileNode = new MeshNode("LinkedIn", profilePath)
            {
                Name = displayName ?? "LinkedIn",
                NodeType = "LinkedIn/LinkedInProfile",
                State = MeshNodeState.Active,
                Content = NodeContentJson.Create("LinkedInProfile",
                [
                    new("displayName", displayName ?? subject),
                    new("subjectUrn", $"urn:li:person:{subject}"),
                    new("pictureUrl", pictureUrl),
                    new("email", emailAddress),
                    new("connectedAt", DateTimeOffset.UtcNow),
                ])
            };

            // SYNC THE IDENTITY ONTO THE PROFILE ITSELF. When the caller pointed at a
            // SocialMedia/Profile, LinkedIn's userinfo is the AUTHORITATIVE source for that
            // profile's display name and photo — so write them there rather than only into the
            // legacy LinkedInProfile child. Read-merge-write: every field we do not own
            // (network, headline, owner, handle) survives untouched. Best-effort — a profile
            // that cannot be read or is of another type is simply left alone.
            var syncProfileIdentity = hub.GetMeshNodeStream(profilePath)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(10))
                .SelectMany(existing =>
                {
                    if (existing is null
                        || !string.Equals(existing.NodeType, SocialProfileNodeType, StringComparison.Ordinal))
                        return Observable.Return(existing ?? profileNode);

                    var merged = LinkedInIdentitySync.Merge(existing.Content, displayName, pictureUrl);
                    return mesh.UpdateNode(existing with { Content = merged });
                })
                .Catch<MeshNode, Exception>(ex =>
                {
                    logger.LogInformation(ex,
                        "Could not sync LinkedIn identity onto {Path} — continuing", profilePath);
                    return Observable.Return(profileNode);
                });

            // Reactive persistence chain — mesh.CreateNode/UpdateNode return IObservable<MeshNode>
            // (see AsynchronousCalls.md). Each Create attempt falls back to Update on failure
            // via Rx Catch. Profile upsert errors are swallowed (best-effort). The whole chain
            // resolves once and emits the final IResult.
            var tcs = new TaskCompletionSource<IResult>();

            var upsertCredential = mesh.CreateNode(credentialNode)
                .Catch<MeshNode, Exception>(createEx =>
                {
                    logger.LogInformation(createEx, "Credential create failed at {Path}, attempting update", credentialNode.Path);
                    return mesh.UpdateNode(credentialNode);
                });

            var upsertProfile = mesh.CreateNode(profileNode)
                .Catch<MeshNode, Exception>(createEx =>
                {
                    logger.LogInformation(createEx, "LinkedInProfile create failed at {Path}, attempting update", profileNode.Path);
                    return mesh.UpdateNode(profileNode);
                })
                .Catch<MeshNode, Exception>(updateEx =>
                {
                    logger.LogWarning(updateEx, "LinkedInProfile upsert failed for {Path} — continuing", profileNode.Path);
                    return Observable.Return(profileNode);
                });

            upsertCredential
                .SelectMany(_ => upsertProfile)
                .SelectMany(_ => syncProfileIdentity)
                // Never let a stuck reactive write freeze the browser on the callback — a hang
                // surfaces as an error redirect, not an indefinite spinner (see /async).
                .Timeout(TimeSpan.FromSeconds(20))
                .Subscribe(
                    _ =>
                    {
                        logger.LogInformation("Connected LinkedIn credential for profile {Profile} (subject {Subject})", profilePath, subject);
                        // DEGRADED BUT HONEST (issue #51): publishing is connected either way, but a
                        // credential without r_member_postAnalytics can only ever report likes and
                        // comments. Say it on the landing page rather than letting the member
                        // discover it as impressions that are permanently 0.
                        var analytics = WantsPostAnalytics(config) && !GrantsPostAnalytics(scope)
                            ? "&analytics=unavailable"
                            : string.Empty;
                        tcs.TrySetResult(Results.Redirect(
                            $"/{profilePath}/LinkedIn?connect=linkedin-ok{analytics}"));
                    },
                    ex =>
                    {
                        logger.LogWarning(ex, "Credential persist failed at {Path}. Redirecting to profile with error.", credentialNode.Path);
                        tcs.TrySetResult(Results.Redirect($"/{profilePath}/LinkedIn?connect=linkedin-error&stage=credential&reason=persist-failed"));
                    });

            return await tcs.Task;
        })
        // ANONYMOUS BY DESIGN, exactly as before the module move: this is LinkedIn's OAuth
        // redirect target — routing it through the module group's authenticated-by-default
        // policy could bounce the redirect into a login challenge and drop the code/state
        // query. The CSRF state cookie (set by the authenticated /connect/linkedin start)
        // is the guard; without it the request is rejected before any token exchange.
        .AllowAnonymous();

        return endpoints;
    }

    private static string GenerateState()
    {
        Span<byte> buf = stackalloc byte[24];
        RandomNumberGenerator.Fill(buf);
        return WebEncoders.Base64UrlEncode(buf);
    }

    /// <summary>The social-suite profile type whose identity LinkedIn's userinfo owns.</summary>
    private const string SocialProfileNodeType = "SocialMedia/Profile";

    private static string BuildRedirectUri(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}{CallbackPath}";

    /// <summary>
    /// Extracts the short <c>error</c> field from a LinkedIn OAuth error payload,
    /// falling back to a generic slug if the body isn't parseable. Used to surface
    /// a compact query-string reason code to the user instead of raw JSON.
    /// </summary>
    private static string ExtractLinkedInErrorReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return err.GetString() ?? "unknown";
        }
        catch { /* non-JSON response */ }
        return "token-exchange-failed";
    }
}
