using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for ApiToken nodes in the graph.
/// Tokens are satellites of User nodes, stored at User/{userId}/_Api/{hashPrefix}.
/// An index at ApiToken/{hashPrefix} enables fast validation routing.
/// Creation uses standard CreateNodeRequest with nodeType=ApiToken â€” the RLS validator
/// maps this to Permission.Api via GetPermissionForNodeType.
/// </summary>
public static class ApiTokenNodeType
{
    public const string NodeType = "ApiToken";

    public static TBuilder AddApiTokenType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        // Access control: GetPermissionForNodeType maps "ApiToken" â†’ Permission.Api
        // The RLS validator checks Permission.Api on the MainNode (User path)
        // Same pattern as Thread â†’ Permission.Thread and Comment â†’ Permission.Comment.
        //
        // Register all ApiToken domain + message types in the hub's type registry so
        // they serialize correctly across silos (Orleans). Mirrors CommentNodeType /
        // ThreadNodeType which do the same â€” without this, cross-silo CreateNodeRequest
        // for nodeType=ApiToken fails with "NodeType 'ApiToken' is not registered"
        // because the receiving silo can't deserialize the typed payload.
        builder.ConfigureHub(config => config
            .WithType<ApiToken>(nameof(ApiToken))
            .WithType<ApiTokenIndex>(nameof(ApiTokenIndex))
            .WithType<ValidateTokenRequest>(nameof(ValidateTokenRequest))
            .WithType<ValidateTokenResponse>(nameof(ValidateTokenResponse)));
        return builder;
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "API Token",
        // Treated as a regular content type (not a satellite). We rely on
        // MainNode = userId on each token row + the per-user-partition own-scope
        // shortcut in RlsNodeValidator to gate Create/Read.
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddApiTokenViews()
            .WithHandler<ValidateTokenRequest>(HandleValidateToken)
            .AddMeshDataSource(source => source
                .WithContentType<ApiToken>()
                .WithContentType<ApiTokenIndex>())
    };

    /// <summary>
    /// Validates an API token by reading the live token node from the mesh: a single synced
    /// query against the <c>auth</c> mirror schema (<c>nodeType:ApiToken content.tokenHash:{hash}</c>),
    /// the same shape as <c>OnboardingMiddleware.FindUserByEmail</c>. The synced query bypasses RLS
    /// and stays live, so a revoked token (<c>IsRevoked</c> on the node) is rejected immediately —
    /// no cache, no per-hash index, no cross-hub invalidation. Sync handler: composes via
    /// <c>IObservable</c> + <c>Subscribe</c>; no <c>await</c>.
    /// </summary>
    private static IMessageDelivery HandleValidateToken(
        IMessageHub hub,
        IMessageDelivery<ValidateTokenRequest> request)
    {
        var rawToken = request.Message.RawToken;

        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(ValidateTokenRequest.TokenPrefix))
        {
            hub.Post(ValidateTokenResponse.Fail("Invalid token format"), o => o.ResponseFor(request));
            return request.Processed();
        }

        var hash = ValidateTokenRequest.HashToken(rawToken);

        hub.GetWorkspace()
            .GetApiTokenByHash(hash)
            .Timeout(TimeSpan.FromSeconds(10), Observable.Return<MeshNode?>(null))
            .Subscribe(
                node =>
                {
                    var apiToken = node?.Content as ApiToken ?? ExtractApiToken(node, hub.JsonSerializerOptions);

                    if (apiToken == null || !string.Equals(apiToken.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
                    {
                        hub.Post(ValidateTokenResponse.Fail("Token not found"), o => o.ResponseFor(request));
                        return;
                    }
                    if (apiToken.IsRevoked)
                    {
                        hub.Post(ValidateTokenResponse.Fail("Token revoked"), o => o.ResponseFor(request));
                        return;
                    }
                    if (apiToken.ExpiresAt.HasValue && apiToken.ExpiresAt.Value < DateTimeOffset.UtcNow)
                    {
                        hub.Post(ValidateTokenResponse.Fail("Token expired"), o => o.ResponseFor(request));
                        return;
                    }

                    // Roles captured on the token at creation time — UserContextMiddleware stamps
                    // these onto AccessContext.Roles so per-node hubs resolve them via the
                    // claim-based path (the synced AccessAssignment query isn't registered there;
                    // recursion avoidance, SecurityServiceExtensions).
                    hub.Post(
                        ValidateTokenResponse.Ok(apiToken.UserId, apiToken.UserName, apiToken.UserEmail, apiToken.Roles),
                        o => o.ResponseFor(request));
                },
                ex => hub.Post(
                    ValidateTokenResponse.Fail($"Validation error: {ex.Message}"),
                    o => o.ResponseFor(request)));

        return request.Processed();
    }

    private static ApiToken? ExtractApiToken(MeshNode? node, JsonSerializerOptions options)
    {
        if (node?.Content is not JsonElement jsonElement) return null;
        try { return JsonSerializer.Deserialize<ApiToken>(jsonElement.GetRawText(), options); }
        catch { return null; }
    }
}
