using System.Reactive;
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
    /// Validates an API token. Routes to ApiToken/{hashPrefix}, follows index to User/{userId}/_Api/{hash}.
    /// Results are cached for 5 minutes.
    /// Sync handler â€” composes via <c>IObservable</c> + <c>Subscribe</c>; no <c>await</c>.
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

        // Read own MeshNode via one-shot GetDataRequest â€” true request/response, no
        // lingering subscription. Posts to self (hub.Address); the handler's Subscribe
        // runs on the event loop after this returns, so no deadlock.
        //
        // Impersonate-as-System for the lookup: token validation is the entry
        // point that turns a raw token into a user identity, so by definition
        // the caller is unauthenticated. Without an explicit System scope, the
        // SecurePersistence ACL would deny the read (anonymous can't read an
        // ApiToken node) and the validator would return "Token not found" for
        // every request. The hash-compare further down is the actual
        // authentication step; the System scope only covers this one read.
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var hubAddress = hub.Address.ToString();
        Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => hub.GetMeshNode(hubAddress, TimeSpan.FromSeconds(10)))
            .SelectMany(node =>
            {
                if (node == null)
                {
                    hub.Post(ValidateTokenResponse.Fail("Token not found"), o => o.ResponseFor(request));
                    return Observable.Empty<Unit>();
                }

                var index = node.Content as ApiTokenIndex ?? ExtractApiTokenIndex(node, hub.JsonSerializerOptions);

                IObservable<MeshNode?> tokenNodeObs;
                if (index != null)
                {
                    if (!string.Equals(index.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
                    {
                        hub.Post(ValidateTokenResponse.Fail("Invalid token"), o => o.ResponseFor(request));
                        return Observable.Empty<Unit>();
                    }

                    // Cross-hub one-shot read for the actual token node â€” GetDataRequest
                    // routes to the owning per-node hub via the mesh. Re-impersonate
                    // as System for this read too: the AsyncLocal context that the
                    // outer Observable.Using set may not be alive when the SelectMany
                    // continuation re-subscribes downstream, and the actual token
                    // node sits at "{userId}/ApiToken/{hashPrefix}" which RLS gates
                    // on the owning user's identity.
                    tokenNodeObs = Observable.Using(
                        () => accessService.ImpersonateAsSystem(),
                        _ => hub.GetMeshNode(index.TokenPath, TimeSpan.FromSeconds(10)));
                }
                else
                {
                    tokenNodeObs = Observable.Return<MeshNode?>(node);
                }

                return tokenNodeObs.Select(tokenNode =>
                {
                    var apiToken = tokenNode?.Content as ApiToken ?? ExtractApiToken(tokenNode, hub.JsonSerializerOptions);

                    if (apiToken == null)
                    {
                        hub.Post(ValidateTokenResponse.Fail("Token not found"), o => o.ResponseFor(request));
                        return Unit.Default;
                    }

                    if (!string.Equals(apiToken.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
                    {
                        hub.Post(ValidateTokenResponse.Fail("Invalid token"), o => o.ResponseFor(request));
                        return Unit.Default;
                    }

                    if (apiToken.IsRevoked)
                    {
                        hub.Post(ValidateTokenResponse.Fail("Token revoked"), o => o.ResponseFor(request));
                        return Unit.Default;
                    }

                    if (apiToken.ExpiresAt.HasValue && apiToken.ExpiresAt.Value < DateTimeOffset.UtcNow)
                    {
                        hub.Post(ValidateTokenResponse.Fail("Token expired"), o => o.ResponseFor(request));
                        return Unit.Default;
                    }

                    // Include the roles captured on the ApiToken at creation time.
                    // UserContextMiddleware stamps these onto AccessContext.Roles so
                    // SecurityService.GetEffectivePermissions can resolve them via
                    // the claim-based role path on per-node hubs â€” where the
                    // synced AccessAssignment query is intentionally not registered
                    // (SecurityServiceExtensions:44-50, recursion avoidance).
                    var response = ValidateTokenResponse.Ok(
                        apiToken.UserId, apiToken.UserName, apiToken.UserEmail, apiToken.Roles);
                    hub.Post(response, o => o.ResponseFor(request));
                    return Unit.Default;
                });
            })
            .Subscribe(
                _ => { },
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

    private static ApiTokenIndex? ExtractApiTokenIndex(MeshNode? node, JsonSerializerOptions options)
    {
        if (node?.Content is not JsonElement jsonElement) return null;
        try
        {
            var index = JsonSerializer.Deserialize<ApiTokenIndex>(jsonElement.GetRawText(), options);
            return !string.IsNullOrEmpty(index?.TokenPath) ? index : null;
        }
        catch { return null; }
    }
}
