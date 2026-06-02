using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.AI;                        // IProviderKeyProtector
using MeshWeaver.Blazor.Infrastructure;     // PortalApplication
using MeshWeaver.Data;                       // IWorkspace.GetMeshNodeStream
using MeshWeaver.Graph.Configuration;        // EaCredentialNodeType
using MeshWeaver.Mesh;                        // EaCredential, MeshNode
using MeshWeaver.Mesh.Security;               // ImpersonateAsSystem
using MeshWeaver.Mesh.Services;               // IMeshService
using MeshWeaver.Messaging;                   // AccessService
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Per-user, just-in-time <b>delegated</b> Microsoft Graph access for the Executive Assistant. The user
/// consents to the EA accessing <i>their own</i> mailbox/calendar only when they first use the tool; we
/// exchange the auth code for tokens, store the refresh token <b>encrypted</b> as an
/// <see cref="EaCredential"/> node, and mint short-lived access tokens from it on demand. No standing
/// application-wide Graph permission is used.
///
/// <para>This sits at the OAuth/HTTP boundary (called from the consent controller and the async EA tool),
/// so <c>async</c>/<c>await</c> is appropriate here — it is not hub-reachable reactive code.</para>
///
/// <para><b>Azure setup (one-time):</b> on the sign-in app registration add the <i>delegated</i> scopes
/// <c>Mail.ReadWrite Mail.Send Calendars.ReadWrite offline_access</c> and the redirect URI
/// <c>{BaseUrl}/auth/ea/callback</c>. The user's first use triggers the consent screen.</para>
/// </summary>
public sealed class EaGraphAuth(
    IServiceProvider rootServices,
    IConfiguration configuration,
    IProviderKeyProtector protector,
    HttpClient http,
    ILogger<EaGraphAuth>? logger = null) : IEaGraphAuth
{
    /// <summary>Delegated scopes the EA needs (space-separated, Graph v2 form).</summary>
    public const string Scopes =
        "https://graph.microsoft.com/Mail.ReadWrite https://graph.microsoft.com/Mail.Send " +
        "https://graph.microsoft.com/Calendars.ReadWrite offline_access";

    private string TenantId => configuration["Authentication:Microsoft:TenantId"] ?? "common";
    private string? ClientId => configuration["Authentication:Microsoft:ClientId"];
    private string? ClientSecret => configuration["Authentication:Microsoft:ClientSecret"];
    private string Authority => $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0";

    /// <summary>True when the sign-in app credentials needed for the delegated flow are configured.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret);

    /// <summary>The Microsoft consent/authorize URL to send the user to (incremental consent, forces the prompt).</summary>
    public string BuildConsentUrl(string state, string redirectUri) =>
        $"{Authority}/authorize?client_id={Uri.EscapeDataString(ClientId ?? "")}" +
        "&response_type=code&response_mode=query" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&scope={Uri.EscapeDataString(Scopes)}" +
        $"&state={Uri.EscapeDataString(state)}&prompt=consent";

    /// <summary>Exchanges the consent auth-code for tokens and stores the (encrypted) refresh token for the user.</summary>
    public async Task<bool> ExchangeAndStoreAsync(string code, string redirectUri, string userObjectId, CancellationToken ct)
    {
        if (!IsConfigured) return false;
        var json = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = ClientId!,
            ["client_secret"] = ClientSecret!,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scopes
        }, ct);
        if (json is null) return false;
        using var doc = JsonDocument.Parse(json);
        var refresh = doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        if (string.IsNullOrEmpty(refresh)) { logger?.LogWarning("EaGraphAuth: no refresh_token in code exchange"); return false; }
        await StoreAsync(userObjectId, refresh!, ct);
        return true;
    }

    /// <summary>Returns a fresh access token for the user, or null when they have not connected (no stored credential).</summary>
    public async Task<string?> GetAccessTokenAsync(string userObjectId, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        var (_, cred) = await LoadAsync(userObjectId, ct);
        var refresh = protector.Unprotect(cred?.RefreshTokenEncrypted);
        if (string.IsNullOrEmpty(refresh)) return null;

        var json = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = ClientId!,
            ["client_secret"] = ClientSecret!,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh!,
            ["scope"] = Scopes
        }, ct);
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        // Rotate the stored refresh token if Entra issued a new one.
        if (doc.RootElement.TryGetProperty("refresh_token", out var nr) && nr.GetString() is { Length: > 0 } rotated)
            await StoreAsync(userObjectId, rotated, ct);
        return doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
    }

    /// <summary>True when the user already connected (has a stored credential) — lets callers skip the consent prompt.</summary>
    public async Task<bool> IsConnectedAsync(string userObjectId, CancellationToken ct)
        => (await LoadAsync(userObjectId, ct)).cred?.RefreshTokenEncrypted is { Length: > 0 };

    private async Task<string?> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await http.PostAsync($"{Authority}/token", new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode) return body;
        logger?.LogWarning("EaGraphAuth: token endpoint returned {Status}", (int)resp.StatusCode);
        return null;
    }

    private static string PathFor(string userObjectId) =>
        $"Auth/{EaCredentialNodeType.UserSegment}/{userObjectId}";

    private async Task StoreAsync(string userObjectId, string refreshToken, CancellationToken ct)
    {
        using var scope = rootServices.CreateScope();
        var hub = scope.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();

        var (existing, _) = await LoadAsync(userObjectId, ct, hub);
        var content = new EaCredential
        {
            UserObjectId = userObjectId,
            RefreshTokenEncrypted = protector.Protect(refreshToken),
            Scopes = Scopes,
            AcquiredAt = DateTimeOffset.UtcNow
        };
        var node = (existing ?? new MeshNode(EaCredentialNodeType.NodeType, PathFor(userObjectId)) { Name = "EA Credential" })
            with { Content = content };

        using (access.ImpersonateAsSystem())
            await (existing is null ? meshService.CreateNode(node) : meshService.UpdateNode(node))
                .FirstAsync().ToTask(ct);
    }

    private Task<(MeshNode? node, EaCredential? cred)> LoadAsync(string userObjectId, CancellationToken ct)
        => LoadAsync(userObjectId, ct, hub: null);

    private async Task<(MeshNode? node, EaCredential? cred)> LoadAsync(
        string userObjectId, CancellationToken ct, MeshWeaver.Messaging.IMessageHub? hub)
    {
        IServiceScope? owned = null;
        try
        {
            if (hub is null)
            {
                owned = rootServices.CreateScope();
                hub = owned.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
            }
            var ws = hub.GetWorkspace();
            var access = hub.ServiceProvider.GetRequiredService<AccessService>();
            MeshNode? node;
            using (access.ImpersonateAsSystem())
                node = await ws.GetMeshNodeStream(PathFor(userObjectId))
                    .Take(1).Timeout(TimeSpan.FromSeconds(10)).FirstAsync().ToTask(ct);
            var cred = node?.Content switch
            {
                EaCredential e => e,
                JsonElement je => Safe(je, hub.JsonSerializerOptions),
                _ => null
            };
            return (node, cred);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "EaGraphAuth: load failed for {User}", userObjectId);
            return (null, null);
        }
        finally { owned?.Dispose(); }
    }

    private static EaCredential? Safe(JsonElement je, JsonSerializerOptions opts)
    {
        try { return JsonSerializer.Deserialize<EaCredential>(je.GetRawText(), opts); }
        catch { return null; }
    }
}
