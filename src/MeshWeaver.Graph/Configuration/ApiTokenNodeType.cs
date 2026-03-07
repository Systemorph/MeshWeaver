using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for ApiToken nodes in the graph.
/// ApiToken nodes are system-managed — excluded from search and create contexts.
/// Handles ValidateTokenRequest to authenticate API tokens via the message hub.
/// </summary>
public static class ApiTokenNodeType
{
    public const string NodeType = "ApiToken";

    public static TBuilder AddApiTokenType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "API Token",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(ApiTokenNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddApiTokenViews()
            .WithHandler<ValidateTokenRequest>(HandleValidateToken)
            .AddMeshDataSource(source => source
                .WithContentType<ApiToken>())
    };

    /// <summary>
    /// Validates an API token. The handler runs on the token's hosted hub (ApiToken/{hashPrefix}).
    /// Uses IPersistenceServiceCore to bypass RLS — token validation happens before any user is logged in.
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

        // Load the token node directly from persistence (bypasses RLS)
        var hubPath = hub.Address.ToString();
        var persistenceCore = hub.ServiceProvider.GetService<IPersistenceServiceCore>();
        if (persistenceCore == null)
        {
            hub.Post(ValidateTokenResponse.Fail("Persistence not available"), o => o.ResponseFor(request));
            return request.Processed();
        }

        var node = await persistenceCore.GetNodeAsync(hubPath, hub.JsonSerializerOptions, ct);
        var apiToken = node?.Content as ApiToken ?? ExtractApiToken(node, hub.JsonSerializerOptions);
        if (apiToken == null)
        {
            hub.Post(ValidateTokenResponse.Fail("Token not found"), o => o.ResponseFor(request));
            return request.Processed();
        }

        // Verify hash matches
        var hash = ValidateTokenRequest.HashToken(rawToken);
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

        hub.Post(
            ValidateTokenResponse.Ok(apiToken.UserId, apiToken.UserName, apiToken.UserEmail),
            o => o.ResponseFor(request));
        return request.Processed();
    }

    private static ApiToken? ExtractApiToken(MeshNode? node, JsonSerializerOptions options)
    {
        if (node?.Content is JsonElement jsonElement)
        {
            try
            {
                return JsonSerializer.Deserialize<ApiToken>(jsonElement.GetRawText(), options);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }
}
