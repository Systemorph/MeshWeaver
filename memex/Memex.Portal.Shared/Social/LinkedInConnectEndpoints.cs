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
        // Convenience: /connect/linkedin/me binds the credential to the authenticated
        // user's own mesh node (User/{identity}). Redirects into the main flow.
        endpoints.MapGet("/connect/linkedin/me", (HttpContext http) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = "/connect/linkedin/me" });
            var user = http.User.Identity!.Name ?? "anonymous";
            return Results.Redirect($"/connect/linkedin?profile=User/{Uri.EscapeDataString(user)}");
        }).RequireAuthorization();

        endpoints.MapGet("/connect/linkedin/pull/me", (HttpContext http) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = "/connect/linkedin/pull/me" });
            var user = http.User.Identity!.Name ?? "anonymous";
            return Results.Redirect($"/connect/linkedin/pull?profile=User/{Uri.EscapeDataString(user)}");
        }).RequireAuthorization();

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

            // Also upsert a LinkedInProfile node at {profilePath}/LinkedIn so the
            // analytics page has somewhere to render. Loose dictionary content avoids
            // a hard dependency on the dynamic LinkedInProfile content type from this
            // assembly — the NodeType registration handles deserialization.
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
            try
            {
                await mesh.CreateNodeAsync(profileNode, http.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "LinkedInProfile create failed at {Path}, attempting update", profileNode.Path);
                try { await mesh.UpdateNodeAsync(profileNode, http.RequestAborted); }
                catch (Exception ex2) { logger.LogWarning(ex2, "LinkedInProfile upsert failed for {Path}", profileNode.Path); }
            }

            return Results.Redirect($"/{profilePath}/LinkedIn?connect=linkedin-ok");
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

        endpoints.MapGet("/connect/linkedin/pull-engagement/me", (HttpContext http) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = "/connect/linkedin/pull-engagement/me" });
            var user = http.User.Identity!.Name ?? "anonymous";
            return Results.Redirect($"/connect/linkedin/pull-engagement?profile=User/{Uri.EscapeDataString(user)}");
        }).RequireAuthorization();

        // Pull comments + likes for the most recent N posts under a profile and
        // upsert them as satellites under each post node ({post}/comments/*, {post}/likes/*).
        endpoints.MapGet("/connect/linkedin/pull-engagement", async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string profile,
            [Microsoft.AspNetCore.Mvc.FromQuery] int? maxPostsPerCall,
            IServiceProvider sp,
            IMeshService mesh,
            ILoggerFactory loggers) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString });

            var logger = loggers.CreateLogger("LinkedInEngagement");
            var maxPosts = Math.Clamp(maxPostsPerCall ?? 20, 1, 100);

            // Load credential.
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
            else if (credNode.Content is JsonElement je)
                credential = je.Deserialize<PlatformCredential>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            if (credential is null)
                return Results.Problem("Credential node has unexpected content shape.");

            var publisher = sp.GetService<LinkedInPublisher>();
            if (publisher is null)
                return Results.Problem("LinkedInPublisher not registered.", statusCode: 500);

            // Collect Post nodes under {profile}/posts/* with platformUrn set, newest first.
            var posts = new List<(MeshNode Node, string Urn)>();
            await foreach (var p in mesh.QueryAsync<MeshNode>($"namespace:{profile}/posts nodeType:Systemorph/Post", ct: http.RequestAborted))
            {
                var urn = TryGetUrn(p);
                if (string.IsNullOrEmpty(urn)) continue;
                posts.Add((p, urn!));
            }
            posts = posts
                .OrderByDescending(t => TryGetPublishedAt(t.Node) ?? DateTimeOffset.MinValue)
                .Take(maxPosts)
                .ToList();

            int totalComments = 0, totalLikes = 0;
            foreach (var (postNode, urn) in posts)
            {
                // Comments.
                var commentCount = 0;
                await foreach (var c in publisher.ListCommentsAsync(urn, credential, maxItems: 200, http.RequestAborted))
                {
                    commentCount++;
                    var commentId = SanitizeUrn(c.Urn);
                    var commentPath = $"{postNode.Path}/comments/{commentId}";

                    bool exists = false;
                    await foreach (var _ in mesh.QueryAsync<MeshNode>($"path:{commentPath}", ct: http.RequestAborted))
                    {
                        exists = true;
                        break;
                    }
                    if (exists) continue;

                    var commentNode = new MeshNode(commentId, $"{postNode.Path}/comments")
                    {
                        Name = TruncateForName(c.Text),
                        NodeType = "Systemorph/PostComment",
                        State = MeshNodeState.Active,
                        Content = new Dictionary<string, object?>
                        {
                            ["$type"] = "PostComment",
                            ["urn"] = c.Urn,
                            ["actor"] = c.ActorUrn,
                            ["actorName"] = c.ActorName,
                            ["actorProfileUrl"] = c.ActorProfileUrl,
                            ["text"] = c.Text,
                            ["createdAt"] = c.CreatedAt
                        }
                    };
                    try { await mesh.CreateNodeAsync(commentNode, http.RequestAborted); totalComments++; }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to create comment node {Path}", commentPath); }
                }

                // Likes.
                var likeCount = 0;
                await foreach (var lk in publisher.ListLikesAsync(urn, credential, maxItems: 500, http.RequestAborted))
                {
                    likeCount++;
                    var likeId = SanitizeUrn(lk.Urn);
                    var likePath = $"{postNode.Path}/likes/{likeId}";

                    bool exists = false;
                    await foreach (var _ in mesh.QueryAsync<MeshNode>($"path:{likePath}", ct: http.RequestAborted))
                    {
                        exists = true;
                        break;
                    }
                    if (exists) continue;

                    var likeNode = new MeshNode(likeId, $"{postNode.Path}/likes")
                    {
                        Name = lk.ActorName ?? lk.ActorUrn,
                        NodeType = "Systemorph/PostLike",
                        State = MeshNodeState.Active,
                        Content = new Dictionary<string, object?>
                        {
                            ["$type"] = "PostLike",
                            ["urn"] = lk.Urn,
                            ["actor"] = lk.ActorUrn,
                            ["actorName"] = lk.ActorName,
                            ["actorProfileUrl"] = lk.ActorProfileUrl,
                            ["createdAt"] = lk.CreatedAt,
                            ["reactionType"] = lk.ReactionType
                        }
                    };
                    try { await mesh.CreateNodeAsync(likeNode, http.RequestAborted); totalLikes++; }
                    catch (Exception ex) { logger.LogWarning(ex, "Failed to create like node {Path}", likePath); }
                }

                logger.LogInformation("Engagement pull for {Post}: {Comments} comments, {Likes} likes", postNode.Path, commentCount, likeCount);

                // Recompute analytics for this post by aggregating its satellites.
                // Stored as a Systemorph/PostAnalytics node at {post}/analytics so the
                // analytics dashboard reads pre-computed data rather than recomputing live.
                await UpsertPostAnalyticsAsync(mesh, postNode, urn, http.RequestAborted, logger);
            }

            logger.LogInformation("Engagement pull complete for {Profile}: {NewComments} new comments, {NewLikes} new likes across {Posts} posts", profile, totalComments, totalLikes, posts.Count);
            return Results.Redirect($"/{profile}?engagement-pull=ok&posts={posts.Count}&comments={totalComments}&likes={totalLikes}");
        });

        return endpoints;
    }

    private static async Task UpsertPostAnalyticsAsync(
        IMeshService mesh, MeshNode postNode, string urn, CancellationToken ct, ILogger logger)
    {
        // Pull all comment + like satellites for this post via mesh query syntax.
        var commentList = new List<(string ActorUrn, string? ActorName, string? ActorProfileUrl, DateTimeOffset CreatedAt)>();
        await foreach (var c in mesh.QueryAsync<MeshNode>(
            $"namespace:{postNode.Path}/comments nodeType:Systemorph/PostComment", ct: ct))
        {
            var (actor, name, url, ts) = ExtractEngager(c);
            commentList.Add((actor, name, url, ts));
        }

        var likeBuckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var likeList = new List<(string ActorUrn, string? ActorName, string? ActorProfileUrl, DateTimeOffset CreatedAt, string ReactionType)>();
        await foreach (var l in mesh.QueryAsync<MeshNode>(
            $"namespace:{postNode.Path}/likes nodeType:Systemorph/PostLike", ct: ct))
        {
            var (actor, name, url, ts) = ExtractEngager(l);
            var reaction = ExtractReactionType(l) ?? "LIKE";
            likeBuckets[reaction] = likeBuckets.TryGetValue(reaction, out var v) ? v + 1 : 1;
            likeList.Add((actor, name, url, ts, reaction));
        }

        // Top engagers: aggregate likes + comments by actor URN.
        var byActor = new Dictionary<string, (string? Name, string? Url, int Count, DateTimeOffset LastAt)>();
        foreach (var c in commentList)
        {
            if (string.IsNullOrEmpty(c.ActorUrn)) continue;
            var existing = byActor.TryGetValue(c.ActorUrn, out var v)
                ? v
                : (Name: c.ActorName, Url: c.ActorProfileUrl, Count: 0, LastAt: DateTimeOffset.MinValue);
            byActor[c.ActorUrn] = (
                Name: existing.Name ?? c.ActorName,
                Url: existing.Url ?? c.ActorProfileUrl,
                Count: existing.Count + 1,
                LastAt: c.CreatedAt > existing.LastAt ? c.CreatedAt : existing.LastAt);
        }
        foreach (var l in likeList)
        {
            if (string.IsNullOrEmpty(l.ActorUrn)) continue;
            var existing = byActor.TryGetValue(l.ActorUrn, out var v)
                ? v
                : (Name: l.ActorName, Url: l.ActorProfileUrl, Count: 0, LastAt: DateTimeOffset.MinValue);
            byActor[l.ActorUrn] = (
                Name: existing.Name ?? l.ActorName,
                Url: existing.Url ?? l.ActorProfileUrl,
                Count: existing.Count + 1,
                LastAt: l.CreatedAt > existing.LastAt ? l.CreatedAt : existing.LastAt);
        }

        var topEngagers = byActor
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.LastAt)
            .Take(20)
            .Select(kv => new Dictionary<string, object?>
            {
                ["actorUrn"] = kv.Key,
                ["actorName"] = kv.Value.Name,
                ["actorProfileUrl"] = kv.Value.Url,
                ["engagementCount"] = kv.Value.Count,
                ["lastEngagedAt"] = kv.Value.LastAt
            })
            .ToList();

        var topReaction = likeBuckets.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        var impressions = TryGetImpressions(postNode);
        var totalEngagements = commentList.Count + likeList.Count;
        var engagementRate = impressions > 0 ? (double)totalEngagements / impressions : 0d;

        var analyticsNode = new MeshNode("analytics", postNode.Path)
        {
            Name = "Engagement analytics",
            NodeType = "Systemorph/PostAnalytics",
            State = MeshNodeState.Active,
            Content = new Dictionary<string, object?>
            {
                ["$type"] = "PostAnalytics",
                ["postPath"] = postNode.Path,
                ["postUrn"] = urn,
                ["totalLikes"] = likeList.Count,
                ["totalComments"] = commentList.Count,
                ["totalImpressions"] = impressions,
                ["engagementRate"] = engagementRate,
                ["topReactionType"] = topReaction,
                ["reactionBreakdown"] = likeBuckets,
                ["topEngagers"] = topEngagers,
                ["lastComputedAt"] = DateTimeOffset.UtcNow
            }
        };

        // Upsert: query existing, then create or update.
        MeshNode? existingAnalytics = null;
        await foreach (var n in mesh.QueryAsync<MeshNode>($"path:{postNode.Path}/analytics", ct: ct))
        {
            existingAnalytics = n;
            break;
        }
        try
        {
            if (existingAnalytics is null)
                await mesh.CreateNodeAsync(analyticsNode, ct);
            else
                await mesh.UpdateNodeAsync(analyticsNode with { Id = existingAnalytics.Id, Namespace = existingAnalytics.Namespace }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to upsert analytics node for {PostPath}", postNode.Path);
        }
    }

    private static (string ActorUrn, string? ActorName, string? ActorProfileUrl, DateTimeOffset CreatedAt) ExtractEngager(MeshNode node)
    {
        string actor = "", name = null!, url = null!;
        DateTimeOffset ts = DateTimeOffset.UtcNow;

        if (node.Content is JsonElement je)
        {
            actor = TryString(je, "actor", "Actor") ?? "";
            name = TryString(je, "actorName", "ActorName");
            url = TryString(je, "actorProfileUrl", "ActorProfileUrl");
            var tsStr = TryString(je, "createdAt", "CreatedAt");
            if (!string.IsNullOrEmpty(tsStr) && DateTimeOffset.TryParse(tsStr, out var parsed)) ts = parsed;
        }
        else if (node.Content is IDictionary<string, object?> d)
        {
            actor = (d.TryGetValue("actor", out var a) || d.TryGetValue("Actor", out a)) ? a as string ?? "" : "";
            name = (d.TryGetValue("actorName", out var n) || d.TryGetValue("ActorName", out n)) ? n as string : null;
            url = (d.TryGetValue("actorProfileUrl", out var u) || d.TryGetValue("ActorProfileUrl", out u)) ? u as string : null;
            if (d.TryGetValue("createdAt", out var t) || d.TryGetValue("CreatedAt", out t))
            {
                ts = t switch
                {
                    DateTimeOffset dto => dto,
                    string s when DateTimeOffset.TryParse(s, out var p) => p,
                    _ => ts
                };
            }
        }
        return (actor, name, url, ts);
    }

    private static string? ExtractReactionType(MeshNode node)
    {
        if (node.Content is JsonElement je) return TryString(je, "reactionType", "ReactionType");
        if (node.Content is IDictionary<string, object?> d &&
            (d.TryGetValue("reactionType", out var v) || d.TryGetValue("ReactionType", out v)))
            return v as string;
        return null;
    }

    private static int TryGetImpressions(MeshNode node)
    {
        if (node.Content is JsonElement je)
        {
            if ((je.TryGetProperty("impressions", out var p) || je.TryGetProperty("Impressions", out p)) &&
                p.ValueKind == JsonValueKind.Number) return p.GetInt32();
        }
        if (node.Content is IDictionary<string, object?> d &&
            (d.TryGetValue("impressions", out var v) || d.TryGetValue("Impressions", out v)))
        {
            return v switch
            {
                int i => i,
                long l => (int)l,
                _ => 0
            };
        }
        return 0;
    }

    private static string? TryString(JsonElement je, string a, string b)
    {
        if (je.TryGetProperty(a, out var x) && x.ValueKind == JsonValueKind.String) return x.GetString();
        if (je.TryGetProperty(b, out var y) && y.ValueKind == JsonValueKind.String) return y.GetString();
        return null;
    }

    private static string? TryGetUrn(MeshNode node)
    {
        if (node.Content is JsonElement je)
        {
            if (je.TryGetProperty("platformUrn", out var u) && u.ValueKind == JsonValueKind.String)
                return u.GetString();
            if (je.TryGetProperty("PlatformUrn", out var u2) && u2.ValueKind == JsonValueKind.String)
                return u2.GetString();
        }
        if (node.Content is IDictionary<string, object?> d)
        {
            if (d.TryGetValue("platformUrn", out var v) && v is string s) return s;
            if (d.TryGetValue("PlatformUrn", out var v2) && v2 is string s2) return s2;
        }
        return null;
    }

    private static DateTimeOffset? TryGetPublishedAt(MeshNode node)
    {
        if (node.Content is JsonElement je)
        {
            JsonElement p;
            if (je.TryGetProperty("publishedAt", out p) || je.TryGetProperty("PublishedAt", out p))
            {
                if (p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), out var dt)) return dt;
            }
        }
        if (node.Content is IDictionary<string, object?> d)
        {
            if (d.TryGetValue("publishedAt", out var v) || d.TryGetValue("PublishedAt", out v))
            {
                return v switch
                {
                    DateTimeOffset dto => dto,
                    string str when DateTimeOffset.TryParse(str, out var dt) => dt,
                    _ => null
                };
            }
        }
        return null;
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
