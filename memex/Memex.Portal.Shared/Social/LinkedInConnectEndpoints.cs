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
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// OAuth2 authorization-code flow for connecting a LinkedIn publishing identity
/// to a profile in the mesh. The deployed surface is intentionally tiny — just
/// the parts that need to live in the portal binary because they involve
/// browser cookies, HTTP routing, and a callback URL whitelisted on LinkedIn:
///
///   GET /connect/linkedin/me                    — convenience: redirect into the flow for the signed-in user
///   GET /connect/linkedin?profile={path}        — start the flow (sets CSRF cookie, redirects to LinkedIn)
///   GET /connect/linkedin/callback?code=…       — finish the flow, persist credential + LinkedInProfile node
///
/// Everything else (pulling past posts, comments, likes, computing analytics,
/// appending telemetry samples) lives as Code on the <c>Systemorph/LinkedInProfile</c>
/// NodeType — see the <c>LinkedInPullActions</c> Code piece. That keeps the
/// deployed binary stable while the actual ingest logic can be edited without a deploy.
/// </summary>
public static class LinkedInConnectEndpoints
{
    public const string StateCookieName = "lnkd_connect_state";
    private const string CallbackPath = "/connect/linkedin/callback";

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
            IConfiguration config) =>
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
                // r_member_social (Community Management API engagement reads) requires
                // explicit app review on LinkedIn — drop it from the default scope so
                // OAuth completes for apps that don't have it. Engagement pulls
                // (comments/likes per post) will return 403 from /v2/socialActions/*
                // until the scope is granted, and the publisher logs + skips them.
                + "&scope=" + Uri.EscapeDataString("openid profile email w_member_social");

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
                NodeType = ApiCredentialNodeType.NodeType,
                Content = credential,
                State = MeshNodeState.Active,
            };
            try { await mesh.CreateNodeAsync(credentialNode, http.RequestAborted); }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "Credential create failed at {Path}, attempting update", credentialNode.Path);
                await mesh.UpdateNodeAsync(credentialNode, http.RequestAborted);
            }

            // Upsert the LinkedInProfile node so the analytics dashboard has somewhere
            // to render. Loose dictionary content avoids a hard dependency on the
            // dynamic LinkedInProfile content type from this assembly.
            var profileNode = new MeshNode("LinkedIn", profilePath)
            {
                Name = displayName ?? "LinkedIn",
                NodeType = "Systemorph/LinkedInProfile",
                State = MeshNodeState.Active,
                Content = new Dictionary<string, object?>
                {
                    ["$type"] = "LinkedInProfile",
                    ["displayName"] = displayName ?? subject,
                    ["subjectUrn"] = $"urn:li:person:{subject}",
                    ["pictureUrl"] = pictureUrl,
                    ["email"] = emailAddress,
                    ["connectedAt"] = DateTimeOffset.UtcNow,
                }
            };
            try { await mesh.CreateNodeAsync(profileNode, http.RequestAborted); }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "LinkedInProfile create failed at {Path}, attempting update", profileNode.Path);
                try { await mesh.UpdateNodeAsync(profileNode, http.RequestAborted); }
                catch (Exception ex2) { logger.LogWarning(ex2, "LinkedInProfile upsert failed for {Path}", profileNode.Path); }
            }

            logger.LogInformation("Connected LinkedIn credential for profile {Profile} (subject {Subject})", profilePath, subject);
            return Results.Redirect($"/{profilePath}/LinkedIn?connect=linkedin-ok");
        });

        return endpoints;
    }

    private static string GenerateState()
    {
        Span<byte> buf = stackalloc byte[24];
        RandomNumberGenerator.Fill(buf);
        return WebEncoders.Base64UrlEncode(buf);
    }

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
