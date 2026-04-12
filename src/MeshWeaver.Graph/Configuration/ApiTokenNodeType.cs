using System.Collections.Concurrent;
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
        // Same pattern as Thread → Permission.Thread and Comment → Permission.Comment
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
    /// </summary>
    private static async Task<IMessageDelivery> HandleValidateToken(
        IMessageHub hub,
        IMessageDelivery<ValidateTokenRequest> request,
        CancellationToken ct)
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

        // Load from persistence (bypasses RLS)
        var hubPath = hub.Address.Path;
        var persistenceCore = hub.ServiceProvider.GetService<IStorageService>();
        if (persistenceCore == null)
        {
            hub.Post(ValidateTokenResponse.Fail("Persistence not available"), o => o.ResponseFor(request));
            return request.Processed();
        }

        var node = await persistenceCore.GetNodeAsync(hubPath, hub.JsonSerializerOptions, ct);
        if (node == null)
        {
            hub.Post(ValidateTokenResponse.Fail("Token not found"), o => o.ResponseFor(request));
            return request.Processed();
        }

        // Check if this is an index pointer or a legacy full token
        var index = node.Content as ApiTokenIndex ?? ExtractApiTokenIndex(node, hub.JsonSerializerOptions);
        ApiToken? apiToken;

        if (index != null)
        {
            if (!string.Equals(index.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                hub.Post(ValidateTokenResponse.Fail("Invalid token"), o => o.ResponseFor(request));
                return request.Processed();
            }

            var tokenNode = await persistenceCore.GetNodeAsync(index.TokenPath, hub.JsonSerializerOptions, ct);
            apiToken = tokenNode?.Content as ApiToken ?? ExtractApiToken(tokenNode, hub.JsonSerializerOptions);
        }
        else
        {
            apiToken = node.Content as ApiToken ?? ExtractApiToken(node, hub.JsonSerializerOptions);
        }

        if (apiToken == null)
        {
            hub.Post(ValidateTokenResponse.Fail("Token not found"), o => o.ResponseFor(request));
            return request.Processed();
        }

        if (!string.Equals(apiToken.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            hub.Post(ValidateTokenResponse.Fail("Invalid token"), o => o.ResponseFor(request));
            return request.Processed();
        }

        if (apiToken.IsRevoked)
        {
            hub.Post(ValidateTokenResponse.Fail("Token revoked"), o => o.ResponseFor(request));
            return request.Processed();
        }

        if (apiToken.ExpiresAt.HasValue && apiToken.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            hub.Post(ValidateTokenResponse.Fail("Token expired"), o => o.ResponseFor(request));
            return request.Processed();
        }

        var response = ValidateTokenResponse.Ok(apiToken.UserId, apiToken.UserName, apiToken.UserEmail);
        ValidationCache[hash] = (response, DateTimeOffset.UtcNow + CacheDuration);

        hub.Post(response, o => o.ResponseFor(request));
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
