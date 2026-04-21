using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Social;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// OAuth2 authorization-code flow for connecting a LinkedIn publishing identity
/// to a profile in the mesh. Separate from the sign-in flow (which only requests
/// openid/profile/email) because publishing requires the extra
/// <c>w_member_social</c> (+ <c>r_member_social</c> for analytics) scopes that
/// LinkedIn treats as a distinct product (Share on LinkedIn + Community
/// Management API) and that users must explicitly consent to per-profile.
///
/// Endpoints:
///   GET /connect/linkedin?profile={profilePath}  — begins the flow; redirects to LinkedIn
///   GET /connect/linkedin/callback?code=...&amp;state=...  — finishes the flow,
///       exchanges the code for tokens, stores them as an <c>ApiCredential</c> node
///       under <c>{profilePath}/_ApiCredentials/linkedin</c>, and redirects back to the
///       profile page.
///
/// STUB COMPLETENESS:
///   - CSRF state is generated + checked via signed cookie (no server-side store needed).
///   - Token exchange uses <see cref="HttpClient"/> against the standard LinkedIn endpoint.
///   - Credential persistence uses <see cref="IMeshNodeFactory"/>. ApiCredential
///     NodeType must be registered (see <see cref="ApiCredentialNodeType"/>).
///   - Token encryption at rest: TODO — wire IPersonalDataProtector to protect
///     AccessToken / RefreshToken before persistence. Logged with a warning.
/// </summary>
public static class LinkedInConnectEndpoints
{
    public const string StateCookieName = "lnkd_connect_state";
    private const string CallbackPath = "/connect/linkedin/callback";

    /// <summary>
    /// Registers the connect endpoints on the app. Call AFTER <c>UseAuthentication</c>
    /// so <c>HttpContext.User</c> is populated — the endpoint requires an authenticated
    /// user so we know which mesh user to bind the credential to.
    /// </summary>
    public static IEndpointRouteBuilder MapLinkedInConnect(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/connect/linkedin", async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string profile,
            IConfiguration config,
            ILoggerFactory loggers) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString });

            var clientId = config["Social:LinkedIn:ClientId"];
            if (string.IsNullOrEmpty(clientId))
                return Results.Problem("LinkedIn client id is not configured (Social:LinkedIn:ClientId).", statusCode: 500);

            if (string.IsNullOrWhiteSpace(profile))
                return Results.BadRequest("profile query parameter is required (path to the Systemorph/Profile node).");

            var state = GenerateState();
            // Sign the state with a short TTL cookie; we'll compare on callback.
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
                + "&scope=" + Uri.EscapeDataString("openid profile email w_member_social r_member_social");

            return Results.Redirect(url);
        }).RequireAuthorization();

        endpoints.MapGet(CallbackPath, async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? code,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? state,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? error,
            IConfiguration config,
            IHttpClientFactory httpFactory,
            IMeshService mesh,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("LinkedInConnect");

            if (!string.IsNullOrEmpty(error))
                return Results.Redirect($"/?connect=linkedin-error&reason={Uri.EscapeDataString(error)}");

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
                return Results.Problem("LinkedIn token exchange failed. See server logs.", statusCode: 502);
            }

            using var doc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync(http.RequestAborted));
            var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var scope = doc.RootElement.TryGetProperty("scope", out var sc) ? sc.GetString() : null;

            // Fetch the user's LinkedIn subject id so the credential knows who to post as.
            using var uiReq = new HttpRequestMessage(HttpMethod.Get, "https://api.linkedin.com/v2/userinfo");
            uiReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var uiResp = await http2.SendAsync(uiReq, http.RequestAborted);
            if (!uiResp.IsSuccessStatusCode)
                return Results.Problem("LinkedIn userinfo fetch failed.", statusCode: 502);
            using var uiDoc = JsonDocument.Parse(await uiResp.Content.ReadAsStringAsync(http.RequestAborted));
            var subject = uiDoc.RootElement.GetProperty("sub").GetString()!;

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
                NodeType = ApiCredentialNodeType.NodeType,
                Content = credential,
                State = MeshNodeState.Active,
            };

            try
            {
                await mesh.CreateNodeAsync(credentialNode, http.RequestAborted);
            }
            catch (Exception ex)
            {
                // Likely already exists — update instead.
                logger.LogInformation(ex, "Create failed, attempting update for LinkedIn credential under {Profile}", profilePath);
                await mesh.UpdateNodeAsync(credentialNode, http.RequestAborted);
            }

            logger.LogInformation("Connected LinkedIn credential for profile {Profile} (subject {Subject})", profilePath, subject);

            return Results.Redirect($"/{profilePath}?connect=linkedin-ok");
        });

        // Manual "pull past posts now" trigger — calls LinkedInPublisher.ListPastPostsAsync
        // using the credential stored under the profile, creates a Systemorph/Post node for
        // each returned item (skipping urns that already exist), and redirects back.
        endpoints.MapGet("/connect/linkedin/pull", async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string profile,
            IServiceProvider sp,
            IMeshService mesh,
            ILoggerFactory loggers) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString });

            var logger = loggers.CreateLogger("LinkedInConnect");

            // Load the credential node.
            MeshNode? credNode = null;
            await foreach (var n in mesh.QueryAsync<MeshNode>($"path:{profile}/_ApiCredentials/linkedin", ct: http.RequestAborted))
            {
                credNode = n;
                break;
            }
            if (credNode is null)
                return Results.BadRequest($"No LinkedIn credential found at {profile}/_ApiCredentials/linkedin. Use /connect/linkedin?profile={profile} first.");

            PlatformCredential? credential = null;
            if (credNode.Content is PlatformCredential typed)
                credential = typed;
            else if (credNode.Content is System.Text.Json.JsonElement je)
                credential = je.Deserialize<PlatformCredential>(new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            if (credential is null)
                return Results.Problem("Credential node has unexpected content shape.");

            var publisher = sp.GetService<LinkedInPublisher>();
            if (publisher is null)
                return Results.Problem("LinkedInPublisher not registered. Check that AddSocialPublishing was called and LinkedIn config is present.", statusCode: 500);

            int imported = 0;
            await foreach (var past in publisher.ListPastPostsAsync(credential, sinceInclusive: null, maxItems: 200, http.RequestAborted))
            {
                // Dedup by urn.
                bool exists = false;
                await foreach (var _ in mesh.QueryAsync<MeshNode>($"namespace:{profile} -urn:{past.Urn}", ct: http.RequestAborted))
                {
                    // just probe first match
                    exists = true;
                    break;
                }
                if (exists) continue;

                // Build a post node under {profile}/posts/{urn-sanitized}.
                var id = SanitizeUrn(past.Urn);
                var postNode = new MeshNode(id, $"{profile}/posts")
                {
                    Name = TruncateForName(past.Text),
                    NodeType = "Systemorph/Post",
                    State = MeshNodeState.Active,
                    // Content as a loose dictionary so we don't hard-depend on SocialMediaPost shape here.
                    Content = new Dictionary<string, object?>
                    {
                        ["$type"] = "SocialMediaPost",
                        ["title"] = TruncateForName(past.Text),
                        ["body"] = past.Text,
                        ["profilePath"] = profile,
                        ["platform"] = "LinkedIn",
                        ["publishedAt"] = past.PublishedAt,
                        ["platformUrn"] = past.Urn,
                        ["platformUrl"] = past.PostUrl,
                        ["impressions"] = past.Stats?.Impressions ?? 0,
                        ["likes"] = past.Stats?.Likes ?? 0,
                    }
                };

                try
                {
                    await mesh.CreateNodeAsync(postNode, http.RequestAborted);
                    imported++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to create post node for urn {Urn}", past.Urn);
                }
            }

            logger.LogInformation("Pull: imported {Count} LinkedIn posts under {Profile}/posts/", imported, profile);
            return Results.Redirect($"/{profile}?pull=linkedin&count={imported}");
        });

        return endpoints;
    }

    private static string SanitizeUrn(string urn) =>
        urn.Replace(':', '_').Replace('/', '_').Replace('?', '_');

    private static string TruncateForName(string text)
    {
        var t = (text ?? "").ReplaceLineEndings(" ").Trim();
        if (t.Length == 0) return "(untitled)";
        return t.Length > 80 ? t[..80] + "…" : t;
    }

    private static string GenerateState()
    {
        Span<byte> buf = stackalloc byte[24];
        RandomNumberGenerator.Fill(buf);
        return WebEncoders.Base64UrlEncode(buf);
    }

    private static string BuildRedirectUri(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}{CallbackPath}";
}

/// <summary>
/// Lightweight helper matching Microsoft.AspNetCore.WebUtilities.WebEncoders so we
/// don't have to take a package reference just for Base64UrlEncode/Decode.
/// </summary>
internal static class WebEncoders
{
    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4) { case 2: padded += "=="; break; case 3: padded += "="; break; }
        return Convert.FromBase64String(padded);
    }
}
