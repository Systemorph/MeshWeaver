using System.Collections.Concurrent;
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
/// Creation uses standard CreateNodeRequest with nodeType=ApiToken — the RLS validator
/// maps this to Permission.Api via GetPermissionForNodeType.
/// </summary>
public static class ApiTokenNodeType
{
    public const string NodeType = "ApiToken";
    private static readonly ConcurrentDictionary<string, (ValidateTokenResponse Response, DateTimeOffset ExpiresAt)> ValidationCache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public static TBuilder AddApiTokenType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        // Access control: GetPermissionForNodeType maps "ApiToken" → Permission.Api
        // The RLS validator checks Permission.Api on the MainNode (User path)
        // Same pattern as Thread → Permission.Thread and Comment → Permission.Comment.
        //
        // Register all ApiToken domain + message types in the hub's type registry so
        // they serialize correctly across silos (Orleans). Mirrors CommentNodeType /
        // ThreadNodeType which do the same — without this, cross-silo CreateNodeRequest
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
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(ApiTokenNodeType).Assembly.Location,
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
    /// Sync handler — composes via <c>IObservable</c> + <c>Subscribe</c>; no <c>await</c>.
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

        // Check cache first
        if (ValidationCache.TryGetValue(hash, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            hub.Post(cached.Response, o => o.ResponseFor(request));
            return request.Processed();
        }

        // Read own MeshNode via the workspace stream — replaces persistence.GetNodeAsync(hubPath).
        var workspace = hub.GetWorkspace();
        workspace.GetMeshNodeStream()
            .Select(n => (MeshNode?)n)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
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

                    // Cross-hub read for the actual token node — workspace dispatches local vs. remote.
                    tokenNodeObs = workspace.GetMeshNodeStream(index.TokenPath)
                        .Select(n => (MeshNode?)n)
                        .Take(1)
                        .Timeout(TimeSpan.FromSeconds(10))
                        .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null));
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

                    var response = ValidateTokenResponse.Ok(apiToken.UserId, apiToken.UserName, apiToken.UserEmail);
                    ValidationCache[hash] = (response, DateTimeOffset.UtcNow + CacheDuration);
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
