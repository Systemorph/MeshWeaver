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
    /// <summary>The node-type identifier string for ApiToken nodes.</summary>
    public const string NodeType = "ApiToken";

    /// <summary>
    /// Registers the ApiToken node type on the mesh builder: adds the MeshNode definition,
    /// excludes it from autocomplete, and registers the ApiToken domain and message types
    /// in the hub's type registry so they serialize across silos.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <returns>The same builder, to allow fluent chaining.</returns>
    public static TBuilder AddApiTokenType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        // Access control: GetPermissionForNodeType maps "ApiToken" → Permission.Api
        // The RLS validator checks Permission.Api on the MainNode (User path)
        // Same pattern as Thread → Permission.Thread and Comment → Permission.Comment.
        //
        // Register all ApiToken domain + message types in the hub's type registry so
        // they serialize correctly across silos (Orleans). Mirrors the collaboration module's Comment /
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

    /// <summary>
    /// Builds the MeshNode definition for the ApiToken node type, including its content
    /// type registrations, excluded contexts, and hub configuration (token views, the
    /// validation handler, and the data source for tokens and their index).
    /// </summary>
    /// <returns>The ApiToken MeshNode definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "API Token",
        // Treated as a regular content type (not a satellite). We rely on
        // MainNode = userId on each token row + the per-user-partition own-scope
        // shortcut in RlsNodeValidator to gate Create/Read.
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .ApplyNodeHubContributions(NodeType)
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
        // ONE verdict for every path that authenticates a token (ApiTokenVerdict) — this handler
        // and the HTTP auth middleware's direct store read must never disagree about a token.
        //
        // The records are read from the AUTHORITATIVE store when this process has one, exactly as
        // ApiTokenService.ConfirmServicePrincipal reads them: an ABSENT record reads as null → a
        // definitive Fail, never a routing NotFound that would surface as Unavailable. Without a
        // store the read goes through the mesh as System — token validation is the entry point
        // that turns a raw token into an identity, so the caller is unauthenticated by definition
        // and the SecurePersistence ACL would otherwise deny the read. The hash compare inside the
        // verdict is the actual authentication step; the System scope covers only the reads.
        //
        // 🚨 RunAsSystem, never Observable.Using (#1790): the scope opens and closes inside one
        // synchronous Subscribe, so this hub's action block never keeps `system-security`.
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        Func<string, IObservable<MeshNode?>> read = storage is not null
            ? path => storage.Read(path, hub.JsonSerializerOptions)
            : path => accessService.RunAsSystem(() => hub.GetMeshNode(path, ApiTokenVerdict.ReadBound));

        // Decide always emits exactly one verdict — a faulted or unanswered read is UNAVAILABLE
        // (retryable), never "invalid" (issue #637) and never a hang.
        ApiTokenVerdict.Decide(request.Message.RawToken, read, hub.JsonSerializerOptions)
            .Take(1)
            .Subscribe(
                verdict => hub.Post(verdict, o => o.ResponseFor(request)),
                ex => hub.Post(
                    ValidateTokenResponse.Unavailable($"Validation error: {ex.Message}"),
                    o => o.ResponseFor(request)));

        return request.Processed();
    }
}
