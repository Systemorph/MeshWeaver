using System.Reactive.Linq;
using System.Security.Cryptography;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// Stores / reads a user's GitHub OAuth credential as a MeshNode at
/// <c>{userId}/_Provider/GitHub</c> on the user's own partition. The access /
/// refresh tokens are encrypted at rest via <see cref="IProviderKeyProtector"/>
/// (the same protector the AI model-provider keys use); everything else is
/// plaintext metadata.
///
/// <para>🚨 Reactive end-to-end — no <c>async</c>/<c>await</c>/<c>Task</c>. Writes
/// go through the canonical <see cref="CreateOrUpdateNodeRequest"/> verb (create OR
/// update); the maybe-absent read goes through <c>workspace.GetQuery</c>
/// (empty-on-absent), never a point <c>GetMeshNodeStream</c> on an absent exact
/// path (which Orleans-NotFound-storms).</para>
/// </summary>
public sealed class GitHubCredentialService(IMeshService meshService, IMessageHub hub, ILogger<GitHubCredentialService>? logger = null)
{
    /// <summary>The satellite namespace segment under a user that holds connected-provider credentials.</summary>
    public const string ProviderNamespaceSegment = "_Provider";
    /// <summary>The fixed node id of the GitHub credential within the provider namespace.</summary>
    public const string CredentialId = "GitHub";
    /// <summary>The <see cref="MeshNode.NodeType"/> used for the stored GitHub credential node.</summary>
    public const string NodeType = "GitHubCredential";

    /// <summary>The credential node path for a user: <c>{userId}/_Provider/GitHub</c>.</summary>
    public static string CredentialPath(string userId) => $"{userId}/{ProviderNamespaceSegment}/{CredentialId}";

    /// <summary>
    /// Persists (creating or updating) the user's GitHub credential, encrypting the
    /// tokens. Subscribe to observe completion.
    /// </summary>
    public IObservable<GitHubCredential> Save(string userId, GitHubToken token, string? gitHubLogin)
    {
        if (string.IsNullOrEmpty(userId))
            return Observable.Throw<GitHubCredential>(new ArgumentException("userId required", nameof(userId)));

        var credential = new GitHubCredential
        {
            AccessToken = Protect(token.AccessToken),
            RefreshToken = Protect(token.RefreshToken),
            TokenType = token.TokenType,
            Scopes = token.Scope,
            GitHubLogin = gitHubLogin,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = token.ExpiresInSeconds is { } secs and > 0
                ? DateTimeOffset.UtcNow.AddSeconds(secs)
                : null,
        };

        var node = new MeshNode(CredentialId, $"{userId}/{ProviderNamespaceSegment}")
        {
            NodeType = NodeType,
            Name = "GitHub",
            State = MeshNodeState.Active,
            MainNode = userId,
            Content = credential,
        };

        logger?.LogInformation("Saving GitHub credential for {User} (login={Login}, keyFp={Fp})",
            userId, gitHubLogin, Fingerprint(token.AccessToken));

        return hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
            .FirstAsync()
            .Select(d => d.Message)
            .SelectMany(resp => resp.Success
                ? Observable.Return(credential)
                : Observable.Throw<GitHubCredential>(new InvalidOperationException(
                    $"Failed to save GitHub credential: {resp.Error}")));
    }

    /// <summary>
    /// LIVE stream of the user's GitHub credential (tokens DECRYPTED), re-emitting on every
    /// change; null when none is connected. Use this for GUI binding so the connect state flips
    /// the instant the OAuth callback's saved credential syncs in. (A one-shot read here grabbed
    /// the synced query's EMPTY pre-sync first emission and showed "Not connected" even right after
    /// a successful connect.) Maybe-absent → <c>GetQuery</c>.
    /// </summary>
    public IObservable<GitHubCredential?> GetStream(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return Observable.Return<GitHubCredential?>(null);

        var path = CredentialPath(userId);
        return hub.GetWorkspace()
            .GetQuery($"github-cred:{userId}", $"path:{path}")
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));
                var cred = ExtractCredential(node);
                if (cred is null) return null;
                return cred with
                {
                    AccessToken = Unprotect(cred.AccessToken),
                    RefreshToken = Unprotect(cred.RefreshToken),
                };
            });
    }

    /// <summary>
    /// One-shot decrypted credential read (or null). For action paths (Sync / Import) — the GUI
    /// connect-state binding keeps the shared <c>github-cred:{userId}</c> query warm, so by action
    /// time the first emission already reflects the saved credential. The GUI uses
    /// <see cref="GetStream"/> for display.
    /// </summary>
    public IObservable<GitHubCredential?> Get(string userId) => GetStream(userId).Take(1);

    /// <summary>True when the user has a stored GitHub credential.</summary>
    public IObservable<bool> IsConnected(string userId) =>
        Get(userId).Select(c => c is { AccessToken.Length: > 0 });

    /// <summary>Removes the user's GitHub credential.</summary>
    public IObservable<bool> Delete(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return Observable.Return(false);
        return meshService.DeleteNode(CredentialPath(userId))
            .Catch<bool, Exception>(ex =>
            {
                logger?.LogWarning(ex, "Failed to delete GitHub credential for {User}", userId);
                return Observable.Return(false);
            });
    }

    private GitHubCredential? ExtractCredential(MeshNode? node)
    {
        if (node?.Content is null) return null;
        if (node.Content is GitHubCredential typed) return typed;
        if (node.Content is System.Text.Json.JsonElement je)
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<GitHubCredential>(je.GetRawText(), hub.JsonSerializerOptions); }
            catch { return null; }
        }
        return null;
    }

    private string? Protect(string? plaintext)
    {
        var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
        return protector is null ? plaintext : protector.Protect(plaintext);
    }

    private string? Unprotect(string? stored)
    {
        var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
        return protector is null ? stored : protector.Unprotect(stored);
    }

    /// <summary>8-char SHA-256 prefix for logs/UI — never the raw token.</summary>
    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(none)";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
