using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Social;

/// <summary>
/// The mesh-facing LinkedIn member-publish chain: read the target <c>SocialMediaPost</c> node → check
/// the caller's access → read the caller's stored LinkedIn credential → publish via
/// <see cref="LinkedInPostsApi"/> → write <c>Status</c>/<c>PublishedUrn</c> back onto the post node.
/// The HTTP endpoints (<c>Memex.Portal.Shared.Social.LinkedInPublishEndpoints</c>) are thin adapters
/// that construct this service from the request's <see cref="IMessageHub"/> + <see cref="IMeshService"/>
/// and map its outcome to a redirect / JSON — so the whole credential-read → publish → write-back path
/// is one testable unit (see <c>LinkedInPublishServiceTest</c>).
///
/// <para><b>Access control.</b> Every mesh read/write runs under the <em>caller's</em> AccessContext —
/// this service NEVER impersonates system. Two explicit gates enforce the rules the endpoint promises:
/// <list type="number">
///   <item><description>the caller must have <see cref="Permission.Update"/> on the post node (they are
///     mutating it — publishing flips its <c>Status</c>) → a user who cannot access the post is denied
///     BEFORE any LinkedIn call is made;</description></item>
///   <item><description>the caller must have <see cref="Permission.Read"/> on the credential node
///     (<c>{profile}/_ApiCredentials/linkedin</c>) → a user cannot publish using someone else's stored
///     credential.</description></item>
/// </list>
/// When row-level security is not configured the permission evaluator returns <see cref="Permission.All"/>
/// for everyone (dev/single-user meshes); with RLS on, these gates deny cross-user access.</para>
///
/// <para>This is HTTP-edge application code (invoked from ASP.NET minimal-API handlers, exactly like
/// <see cref="LinkedInPublisher"/>), NOT hub code — so it is <c>Task</c>-based and bridges the reactive
/// mesh surface with <c>.FirstAsync().ToTask()</c> at its own boundary.</para>
/// </summary>
public sealed class LinkedInPublishService
{
    private readonly IMessageHub _hub;
    private readonly IMeshService _mesh;
    private readonly ILogger? _logger;

    /// <summary>Creates the service over a request-scoped hub + mesh service.</summary>
    public LinkedInPublishService(IMessageHub hub, IMeshService mesh, ILogger<LinkedInPublishService>? logger = null)
    {
        _hub = hub;
        _mesh = mesh;
        _logger = logger;
    }

    /// <summary>
    /// Publishes the <c>SocialMediaPost</c> at <paramref name="postPath"/> to LinkedIn and writes the
    /// result back. <paramref name="textOverride"/> wins over the post's <c>Body</c>/<c>Text</c> when set.
    /// Returns the outcome (including <see cref="PublishNodeOutcome.HttpAttempted"/> so callers/tests can
    /// assert that a guarded request made no outbound LinkedIn call).
    /// </summary>
    public async Task<PublishNodeOutcome> PublishPostAsync(
        HttpClient client, string postPath, string? textOverride, string? visibility, string? apiVersion, CancellationToken ct)
    {
        var postNode = await TryReadNodeAsync(postPath, ct);
        if (postNode is null)
            return PublishNodeOutcome.Fail("post-not-found");

        // Gate 1: the caller must be able to UPDATE the post (we write Status/PublishedUrn back).
        // Denied → no LinkedIn call, no write. Runs under the caller's AccessContext.
        if (!await CanAsync(postPath, Permission.Update, ct))
            return PublishNodeOutcome.Fail("access-denied");

        var text = textOverride ?? Prop(postNode, "body") ?? Prop(postNode, "text");
        var profilePath = Prop(postNode, "profilePath");
        if (string.IsNullOrWhiteSpace(profilePath))
            return PublishNodeOutcome.Fail("profile-path-missing");
        if (string.IsNullOrWhiteSpace(text))
            return PublishNodeOutcome.Fail("empty-text");

        var credential = await ReadOwnCredentialAsync(profilePath!, ct);
        if (credential is null)
            return PublishNodeOutcome.Fail("not-connected");
        if (!HasPublishScope(credential))
            return PublishNodeOutcome.Fail("missing-w_member_social-reconnect");

        var outcome = await LinkedInPostsApi.PublishAsync(client, credential, text!, visibility, apiVersion, ct);

        var updates = outcome.Success
            ? new Dictionary<string, object?>
            {
                ["status"] = "Published",
                ["publishedUrn"] = outcome.Urn,
                ["publishedAt"] = DateTimeOffset.UtcNow,
            }
            : new Dictionary<string, object?> { ["status"] = "Failed" };
        await WriteBackAsync(postNode, updates, ct);

        return outcome.Success
            ? new PublishNodeOutcome(true, outcome.Urn, outcome.PostUrl, null, outcome.StatusCode, HttpAttempted: true)
            : new PublishNodeOutcome(false, null, null, ShortReason(outcome.Error), outcome.StatusCode, HttpAttempted: true);
    }

    /// <summary>
    /// Publishes free text for <paramref name="profilePath"/>'s member (no post node). Used by the JSON
    /// <c>POST /linkedin/publish</c> path when only <c>profilePath</c> + <c>text</c> are supplied. Still
    /// gated on <see cref="Permission.Read"/> of the credential node so a caller cannot borrow another
    /// member's credential.
    /// </summary>
    public async Task<LinkedInPublishOutcome> PublishTextAsync(
        HttpClient client, string profilePath, string text, string? visibility, string? apiVersion, CancellationToken ct)
    {
        var credential = await ReadOwnCredentialAsync(profilePath, ct);
        if (credential is null)
            return new LinkedInPublishOutcome(false, null, null, 0, "not-connected");
        if (!HasPublishScope(credential))
            return new LinkedInPublishOutcome(false, null, null, 0, "missing-w_member_social-reconnect");
        return await LinkedInPostsApi.PublishAsync(client, credential, text, visibility, apiVersion, ct);
    }

    /// <summary>
    /// Refreshes engagement (like/comment counts) for a published post and writes the counts back. Same
    /// two access gates as <see cref="PublishPostAsync"/>.
    /// </summary>
    public async Task<EngagementNodeOutcome> RefreshEngagementAsync(
        HttpClient client, string postPath, string? apiVersion, CancellationToken ct)
    {
        var postNode = await TryReadNodeAsync(postPath, ct);
        if (postNode is null)
            return EngagementNodeOutcome.Fail("post-not-found");
        if (!await CanAsync(postPath, Permission.Update, ct))
            return EngagementNodeOutcome.Fail("access-denied");

        var urn = Prop(postNode, "publishedUrn");
        if (string.IsNullOrWhiteSpace(urn))
            return EngagementNodeOutcome.Fail("not-published");
        var profilePath = Prop(postNode, "profilePath");
        if (string.IsNullOrWhiteSpace(profilePath))
            return EngagementNodeOutcome.Fail("profile-path-missing");

        var credential = await ReadOwnCredentialAsync(profilePath!, ct);
        if (credential is null)
            return EngagementNodeOutcome.Fail("not-connected");

        var outcome = await LinkedInPostsApi.GetSocialActionsAsync(client, urn!, credential, apiVersion, ct);
        if (!outcome.Success)
            return new EngagementNodeOutcome(false, 0, 0, ShortReason(outcome.Error), outcome.StatusCode, HttpAttempted: true);

        await WriteBackAsync(postNode, new Dictionary<string, object?>
        {
            ["likes"] = outcome.LikeCount,
            ["comments"] = outcome.CommentCount,
        }, ct);
        return new EngagementNodeOutcome(true, outcome.LikeCount, outcome.CommentCount, null, outcome.StatusCode, HttpAttempted: true);
    }

    // ---- access + mesh helpers (all under the caller's AccessContext) ----

    private async Task<bool> CanAsync(string path, Permission permission, CancellationToken ct)
    {
        try
        {
            return await _hub.CheckPermission(path, permission)
                .Timeout(TimeSpan.FromSeconds(10))
                .FirstAsync()
                .ToTask(ct);
        }
        catch (Exception ex)
        {
            // Fail CLOSED: an errored/timed-out permission check denies. Never publish on an
            // indeterminate access answer.
            _logger?.LogWarning(ex, "Permission check failed for {Path} ({Permission}) — denying", path, permission);
            return false;
        }
    }

    private async Task<PlatformCredential?> ReadOwnCredentialAsync(string profilePath, CancellationToken ct)
    {
        var credentialPath = profilePath + "/_ApiCredentials/linkedin";
        // Gate 2: the caller must be able to READ the credential node — a user cannot publish using
        // someone else's stored credential. (RLS + the credential's owner-only access rule enforce this.)
        if (!await CanAsync(credentialPath, Permission.Read, ct))
            return null;
        var node = await TryReadNodeAsync(credentialPath, ct);
        return AsCredential(node);
    }

    private async Task<MeshNode?> TryReadNodeAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _hub.GetMeshNodeStream(path)
                .Where(n => n?.Content is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(10))
                .FirstAsync()
                .ToTask(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogInformation(ex, "No readable node at {Path}", path);
            return null;
        }
    }

    private async Task WriteBackAsync(MeshNode postNode, IReadOnlyDictionary<string, object?> updates, CancellationToken ct)
    {
        try
        {
            var content = ContentToDict(postNode);
            foreach (var kv in updates)
                content[kv.Key] = kv.Value;
            var updated = postNode with { Content = content };
            await _mesh.CreateOrUpdateNode(updated).FirstAsync().ToTask(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to write publish result back to {Path}", postNode.Path);
        }
    }

    private static bool HasPublishScope(PlatformCredential credential) =>
        string.IsNullOrEmpty(credential.Scope)
        || credential.Scope!.Contains("w_member_social", StringComparison.OrdinalIgnoreCase);

    private static PlatformCredential? AsCredential(MeshNode? node)
    {
        if (node?.Content is PlatformCredential typed)
            return typed;
        if (node?.Content is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            try { return je.Deserialize<PlatformCredential>(CredentialJsonOptions); }
            catch (JsonException) { return null; }
        }
        return null;
    }

    private static readonly JsonSerializerOptions CredentialJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static Dictionary<string, object?> ContentToDict(MeshNode node)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (node.Content is null)
            return dict;
        var je = node.Content is JsonElement e ? e : JsonSerializer.SerializeToElement(node.Content, node.Content.GetType());
        if (je.ValueKind == JsonValueKind.Object)
            foreach (var p in je.EnumerateObject())
                dict[p.Name] = p.Value.Clone();
        return dict;
    }

    private static string? Prop(MeshNode node, string name)
    {
        if (node.Content is null)
            return null;
        var je = node.Content is JsonElement e ? e : JsonSerializer.SerializeToElement(node.Content, node.Content.GetType());
        if (je.ValueKind != JsonValueKind.Object)
            return null;
        if (TryString(je, name, out var v))
            return v;
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return TryString(je, pascal, out var v2) ? v2 : null;
    }

    private static bool TryString(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out var p))
            return false;
        value = p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.ToString(),
            _ => null,
        };
        return value is not null;
    }

    private static string ShortReason(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "linkedin-error";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString() ?? "linkedin-error";
            if (doc.RootElement.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String)
                return er.GetString() ?? "linkedin-error";
        }
        catch (JsonException) { /* non-JSON body */ }
        return "linkedin-error";
    }
}

/// <summary>
/// Outcome of a <see cref="LinkedInPublishService.PublishPostAsync"/> call. <see cref="HttpAttempted"/>
/// is false when a pre-publish gate (access / missing profile / empty text / missing scope) short-circuited
/// BEFORE any outbound LinkedIn call — tests assert on it to prove a guarded publish made no HTTP request.
/// </summary>
public sealed record PublishNodeOutcome(
    bool Success, string? Urn, string? PostUrl, string? Reason, int StatusCode, bool HttpAttempted)
{
    internal static PublishNodeOutcome Fail(string reason) => new(false, null, null, reason, 0, HttpAttempted: false);
}

/// <summary>Outcome of a <see cref="LinkedInPublishService.RefreshEngagementAsync"/> call.</summary>
public sealed record EngagementNodeOutcome(
    bool Success, int LikeCount, int CommentCount, string? Reason, int StatusCode, bool HttpAttempted)
{
    internal static EngagementNodeOutcome Fail(string reason) => new(false, 0, 0, reason, 0, HttpAttempted: false);
}
