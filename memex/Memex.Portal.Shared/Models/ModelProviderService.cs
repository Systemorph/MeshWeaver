using System.Collections.Immutable;
using System.Reactive;
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

namespace Memex.Portal.Shared.Models;

/// <summary>
/// Service for creating, rotating, and deleting AI model provider credentials.
/// Modelled on <see cref="Memex.Portal.Shared.Authentication.ApiTokenService"/>
/// — credentials are stored as <c>nodeType:ModelProvider</c> MeshNodes in the
/// owner's dotfile namespace (<c>{userId}/_Memex/{providerName}</c>, the same
/// hidden namespace that hosts <c>{user}/_Memex/ThreadComposer</c>; see
/// <see cref="ModelProviderNodeType.UserNamespace"/>). Any node's namespace
/// works for shared / org-level credentials.
///
/// <para>
/// 🚨 Reactive end-to-end. No <c>async</c>, no <c>await</c>, no
/// <c>FromAsync</c>. Reads go through <c>workspace.GetQuery</c> (synced) or
/// <c>workspace.GetMeshNodeStream</c> (live single-node) per
/// <see cref="MeshWeaver.Documentation.Data.Architecture.SyncedMeshNodeQueries">SyncedMeshNodeQueries</see>.
/// Writes go through <see cref="IMeshService.CreateNode"/> /
/// <see cref="IMeshService.DeleteNode"/> and
/// <see cref="MeshNodeStreamExtensions.Update"/> on the workspace remote
/// stream.
/// </para>
///
/// <para>Layout per provider entry (Anthropic example, owner = <c>rbuergi</c>):</para>
/// <code>
/// rbuergi/_Memex/Anthropic                            ← ModelProvider (carries ApiKey, RLS-gated)
/// rbuergi/_Memex/Anthropic/claude-opus-4-7            ← LanguageModel, ProviderRef → ../Anthropic
/// rbuergi/_Memex/Anthropic/claude-sonnet-4-6          ← LanguageModel, same ProviderRef
/// rbuergi/_Memex/Anthropic/claude-haiku-4-5-20251001  ← LanguageModel, same ProviderRef
/// </code>
///
/// <para>The default model ids come from
/// the live <see cref="LanguageModelCatalogOptions.Sources"/>; callers
/// can override.</para>
/// </summary>
public class ModelProviderService(IMeshService meshService, IMessageHub hub, ILogger<ModelProviderService> logger)
{
    // Per-owner cached snapshot — feeds the Models settings UI without
    // hitting the synced query on every render. Wrapped around the live
    // workspace.GetQuery observable (which is itself Replay(1).RefCount),
    // so the cache holds the latest projection for an hour and writes
    // (Create/RotateKey/Delete) explicitly invalidate the entry. The
    // upstream synced query continues to push live updates into the cached
    // observable so consumers always see fresh data within the TTL.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (IObservable<IReadOnlyList<ProviderInfo>> Stream, DateTimeOffset ExpiresAt)>
        cachedStreams = new(StringComparer.Ordinal);

    private LanguageModelCatalogSource? FindCatalogSource(string providerName)
    {
        var opts = hub.ServiceProvider.GetService<LanguageModelCatalogOptions>();
        return opts?.Sources.FirstOrDefault(s =>
            string.Equals(s.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
    }

    private void InvalidateCache(string ownerPath)
    {
        cachedStreams.TryRemove(ownerPath, out _);
    }

    /// <summary>
    /// Reactive provider creation. Creates the <c>ModelProvider</c> node at
    /// <c>{ownerPath}/_Memex/{instanceId ?? provider}</c> with the supplied key +
    /// endpoint, then creates one <c>LanguageModel</c> child per model id (each
    /// pointing back at the provider node via
    /// <see cref="ModelDefinition.ProviderRef"/>). Defaults come from the
    /// live <see cref="LanguageModelCatalogOptions.Sources"/> registered by
    /// each provider's <c>AddXxxCatalog</c> extension — no central registry.
    ///
    /// <para><paramref name="instanceId"/> is the node id (and path segment); it
    /// defaults to <paramref name="provider"/>. For a generic OpenAI-compatible
    /// provider the user can stand up several instances (OpenRouter, Groq, …) that
    /// all carry <c>Content.Provider = "OpenAICompatible"</c> (the wire-protocol
    /// stamp that routes them to the OpenAI factory) but live at distinct paths so
    /// they never collide. For named providers (OpenAI, Anthropic) leave it null —
    /// one instance per type, keyed by the provider name.</para>
    /// </summary>
    public IObservable<ProviderCreationResult> CreateProvider(
        string ownerPath,
        string provider,
        string? apiKey,
        string? label = null,
        string? endpointOverride = null,
        IReadOnlyList<string>? modelIdsOverride = null,
        string? instanceId = null)
    {
        if (string.IsNullOrEmpty(ownerPath))
            return Observable.Throw<ProviderCreationResult>(new ArgumentException("ownerPath required", nameof(ownerPath)));
        if (string.IsNullOrEmpty(provider))
            return Observable.Throw<ProviderCreationResult>(new ArgumentException("provider required", nameof(provider)));

        // The node id/path segment. Distinct instances of the same wire-protocol
        // provider (e.g. two OpenAICompatible gateways) get distinct ids while
        // Content.Provider stays the protocol stamp.
        var providerId = string.IsNullOrWhiteSpace(instanceId) ? provider : instanceId!.Trim();

        var source = FindCatalogSource(provider);
        var endpoint = endpointOverride ?? source?.DefaultEndpoint;
        if (string.IsNullOrEmpty(endpoint)) endpoint = null;

        var modelIds = (modelIdsOverride ?? (IReadOnlyList<string>?)source?.EffectiveModelIds)
            ?? Array.Empty<string>();

        // User-owned providers/models live in the owner's dotfile namespace
        // ({owner}/_Memex/{providerId}/{model}), NOT a shared _Provider satellite.
        // See ModelProviderNodeType.UserNamespace.
        var providerNamespace = ModelProviderNodeType.UserNamespacePath(ownerPath);
        var providerPath = $"{providerNamespace}/{providerId}";

        var providerConfig = new ModelProviderConfiguration
        {
            Provider = provider,
            ApiKey = Protect(apiKey),
            Endpoint = endpoint,
            Label = label ?? (string.IsNullOrWhiteSpace(instanceId) ? null : instanceId) ?? source?.EffectiveLabel ?? provider,
            CreatedAt = DateTimeOffset.UtcNow,
            Models = modelIds.Where(m => !string.IsNullOrWhiteSpace(m))
                .ToImmutableArrayCompat(),
        };

        var providerNode = new MeshNode(providerId, providerNamespace)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = providerConfig.Label,
            State = MeshNodeState.Active,
            MainNode = ownerPath,
            Content = providerConfig,
        };

        logger.LogInformation("Creating ModelProvider {ProviderId} (provider={Provider}) for owner {Owner} with {ModelCount} models, keyFp={KeyFp}",
            providerId, provider, ownerPath, modelIds.Count, Fingerprint(apiKey));

        // 1. Create the ModelProvider node.
        // 2. After commit, fan out N CreateNode calls for the LanguageModel children.
        //    The children reference the provider via ProviderRef.
        InvalidateCache(ownerPath);
        return meshService.CreateNode(providerNode)
            .SelectMany(createdProvider =>
            {
                var modelObservables = modelIds
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(modelId =>
                    {
                        var modelDef = new ModelDefinition
                        {
                            Id = modelId,
                            DisplayName = modelId,
                            Provider = provider,
                            Endpoint = null, // resolver follows ProviderRef
                            ApiKeySecretRef = null,
                            ProviderRef = createdProvider.Path,
                            Order = source?.Order ?? 0
                        };
                        var modelNode = new MeshNode(modelId, providerPath)
                        {
                            NodeType = LanguageModelNodeType.NodeType,
                            Name = modelId,
                            Category = "Models",
                            State = MeshNodeState.Active,
                            MainNode = ownerPath,
                            Content = modelDef,
                        };
                        return meshService.CreateNode(modelNode)
                            .Catch<MeshNode, Exception>(ex =>
                            {
                                logger.LogWarning(ex, "Failed to create LanguageModel {ModelId} under {Path}", modelId, providerPath);
                                return Observable.Return<MeshNode>(null!);
                            });
                    })
                    .ToArray();

                if (modelObservables.Length == 0)
                    return Observable.Return(new ProviderCreationResult(createdProvider, Array.Empty<MeshNode>()));

                return Observable.CombineLatest(modelObservables)
                    .Take(1)
                    .Select(children => new ProviderCreationResult(
                        createdProvider,
                        children.Where(c => c != null).ToArray()));
            });
    }

    /// <summary>
    /// Reactive rotate-key. Updates the <c>ApiKey</c> field on the
    /// <c>ModelProvider</c> node via
    /// <see cref="MeshNodeStreamExtensions.Update"/>. Other fields are
    /// preserved.
    /// </summary>
    public IObservable<bool> RotateKey(string providerNodePath, string? newApiKey)
    {
        if (string.IsNullOrEmpty(providerNodePath))
            return Observable.Return(false);

        logger.LogInformation("Rotating ModelProvider key at {Path} newKeyFp={KeyFp}",
            providerNodePath, Fingerprint(newApiKey));

        var workspace = hub.GetWorkspace();
        return workspace.GetMeshNodeStream(providerNodePath)
            .Update(current =>
            {
                var cfg = current.Content as ModelProviderConfiguration
                    ?? ExtractContent<ModelProviderConfiguration>(current);
                if (cfg == null) return current;
                return current with { Content = cfg with { ApiKey = Protect(newApiKey) } };
            })
            .Do(updatedNode =>
            {
                // Force persistence at the per-node hub. Sync-protocol updates
                // don't always fire the per-node hub's `saveSub` for
                // remote-driven changes (see ApiTokenService.RevokeToken for
                // the matching pattern + comment).
                hub.Post(new SaveMeshNodeRequest(updatedNode),
                    o => o.WithTarget(new Address(providerNodePath)));
            })
            .Select(_ => true)
            .Catch<bool, Exception>(ex =>
            {
                logger.LogWarning(ex, "RotateKey failed for {Path}", providerNodePath);
                return Observable.Return(false);
            });
    }

    /// <summary>
    /// Reactive cascade-delete. Removes all child <c>LanguageModel</c> nodes
    /// (their paths recorded in the provider's
    /// <see cref="ModelProviderConfiguration.Models"/> snapshot), then the
    /// <c>ModelProvider</c> node itself.
    /// </summary>
    public IObservable<bool> DeleteProvider(string providerNodePath)
    {
        if (string.IsNullOrEmpty(providerNodePath))
            return Observable.Return(false);

        logger.LogInformation("Deleting ModelProvider {Path} (cascade includes child LanguageModels)", providerNodePath);

        var workspace = hub.GetWorkspace();
        return workspace.GetMeshNodeStream(providerNodePath)
            .Take(1)
            .SelectMany(current =>
            {
                var cfg = current?.Content as ModelProviderConfiguration
                    ?? ExtractContent<ModelProviderConfiguration>(current);
                var childPaths = cfg?.Models
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => $"{providerNodePath}/{m}")
                    .ToArray()
                    ?? Array.Empty<string>();

                IObservable<Unit> childDeletes = childPaths.Length == 0
                    ? Observable.Return(Unit.Default)
                    : Observable.CombineLatest(childPaths.Select(p =>
                        meshService.DeleteNode(p)
                            .Catch<bool, Exception>(ex =>
                            {
                                logger.LogDebug(ex, "Child LanguageModel delete failed for {Path}", p);
                                return Observable.Return(false);
                            })))
                        .Take(1)
                        .Select(_ => Unit.Default);

                return childDeletes
                    .SelectMany(_ => meshService.DeleteNode(providerNodePath))
                    .Select(_ => true);
            })
            .Catch<bool, Exception>(ex =>
            {
                logger.LogWarning(ex, "DeleteProvider failed for {Path}", providerNodePath);
                return Observable.Return(false);
            });
    }

    /// <summary>
    /// Live list of ModelProviders owned by <paramref name="ownerPath"/>.
    /// Same shape as
    /// <see cref="Memex.Portal.Shared.Authentication.ApiTokenService.GetTokensForUser"/>
    /// — synced via <c>workspace.GetQuery</c>.
    /// </summary>
    public IObservable<IReadOnlyList<ProviderInfo>> GetProvidersForOwner(string ownerPath)
    {
        if (string.IsNullOrEmpty(ownerPath))
            return Observable.Return((IReadOnlyList<ProviderInfo>)Array.Empty<ProviderInfo>());

        if (cachedStreams.TryGetValue(ownerPath, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            return entry.Stream;

        var workspace = hub.GetWorkspace();
        var providerNamespace = ModelProviderNodeType.UserNamespacePath(ownerPath);

        var stream = workspace.GetQuery(
                $"model-providers:{ownerPath}",
                $"namespace:{providerNamespace} nodeType:{ModelProviderNodeType.NodeType}")
            .Select(snapshot =>
            {
                var providers = new List<ProviderInfo>();
                foreach (var node in snapshot)
                {
                    if (node.Path is null) continue;
                    if (!string.Equals(node.NodeType, ModelProviderNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var cfg = node.Content as ModelProviderConfiguration
                        ?? ExtractContent<ModelProviderConfiguration>(node);
                    if (cfg == null) continue;
                    providers.Add(new ProviderInfo
                    {
                        NodePath = node.Path,
                        Provider = cfg.Provider,
                        Label = cfg.Label,
                        Endpoint = cfg.Endpoint,
                        CreatedAt = cfg.CreatedAt,
                        LastUsedAt = cfg.LastUsedAt,
                        ModelIds = cfg.Models.ToArray(),
                        ApiKeyFingerprint = Fingerprint(Unprotect(cfg.ApiKey)),
                    });
                }
                return (IReadOnlyList<ProviderInfo>)providers;
            })
            // Replay the latest projected snapshot to subsequent subscribers
            // without re-subscribing upstream. The upstream synced query
            // pushes live changes through; the TTL bounds how long we keep
            // the projection alive when nobody is actively watching.
            .Replay(1)
            .RefCount();

        cachedStreams[ownerPath] = (stream, DateTimeOffset.UtcNow + CacheTtl);
        return stream;
    }

    /// <summary>
    /// Live list of the owner's selected provider paths (the provider-selection
    /// picker). Empty when no selection node exists yet. Single-node read via
    /// <c>GetMeshNodeStream</c> per CqrsAndContentAccess.
    /// </summary>
    public IObservable<ImmutableArray<string>> GetSelection(string ownerPath)
    {
        if (string.IsNullOrEmpty(ownerPath))
            return Observable.Return(ImmutableArray<string>.Empty);
        // 🚨 Read the selection via a QUERY, not a point GetMeshNodeStream(exactPath):
        // a pre-existing user partition has no selection node, and a point-subscribe
        // to a missing path routes to a NotFound DeliveryFailure (the resubscribe-storm
        // that froze the portal, 2026-06-09). A query returns EMPTY on absence — the
        // documented "no selection ⇒ default catalog" behaviour — and never errors.
        return hub.GetWorkspace()
            .GetQuery(
                $"{ModelProviderNodeType.SelectionNodeType}|{ownerPath}",
                $"namespace:{ModelProviderNodeType.UserNamespacePath(ownerPath)} nodeType:{ModelProviderNodeType.SelectionNodeType}")
            .Select(snapshot =>
            {
                var node = snapshot.FirstOrDefault(n =>
                    string.Equals(n.NodeType, ModelProviderNodeType.SelectionNodeType, StringComparison.OrdinalIgnoreCase));
                var sel = node?.Content as ModelProviderSelection
                    ?? ExtractContent<ModelProviderSelection>(node);
                if (sel is null) return ImmutableArray<string>.Empty;
                return sel.SelectedProviderPaths.IsDefault
                    ? ImmutableArray<string>.Empty
                    : sel.SelectedProviderPaths;
            });
    }

    /// <summary>
    /// Persist the owner's selected provider paths. Create-or-update via
    /// <c>stream.Update</c> — when the node doesn't exist yet the handshake
    /// delivers <c>null</c> and we substitute the full new node (own-node
    /// writes upsert through the local data source).
    /// </summary>
    public IObservable<bool> SetSelection(string ownerPath, ImmutableArray<string> providerPaths)
    {
        if (string.IsNullOrEmpty(ownerPath))
            return Observable.Return(false);

        var ns = ModelProviderNodeType.UserNamespacePath(ownerPath);
        var content = new ModelProviderSelection { SelectedProviderPaths = providerPaths };
        var newNode = new MeshNode(ModelProviderNodeType.SelectionNodeId, ns)
        {
            NodeType = ModelProviderNodeType.SelectionNodeType,
            Name = "Model Provider Selection",
            State = MeshNodeState.Active,
            MainNode = ownerPath,
            Content = content,
        };

        // Create on first write; fall back to update when the node already
        // exists (stream.Update alone does not create a missing own-node).
        return meshService.CreateNode(newNode)
            .Select(_ => true)
            .Catch<bool, Exception>(_ => hub.GetWorkspace()
                .GetMeshNodeStream(newNode.Path)
                .Update(current => current is null ? newNode : current with { Content = content })
                .Select(_ => true))
            .Catch<bool, Exception>(ex =>
            {
                logger.LogWarning(ex, "SetSelection failed for {Owner}", ownerPath);
                return Observable.Return(false);
            });
    }

    private T? ExtractContent<T>(MeshNode? node) where T : class
    {
        if (node?.Content is null) return null;
        if (node.Content is T typed) return typed;
        if (node.Content is System.Text.Json.JsonElement je)
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<T>(je.GetRawText(), hub.JsonSerializerOptions); }
            catch { return null; }
        }
        return null;
    }

    // Encryption-at-rest for the literal ApiKey. Resolved lazily from the hub's
    // service provider (same place the ChatClientCredentialResolver reads it);
    // passthrough when not registered or no master key is configured.
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

    /// <summary>
    /// 8-char SHA-256 prefix — never the raw key. Same shape as the
    /// factories' Fingerprint helper so logs/UI can correlate across
    /// layers.
    /// </summary>
    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}

/// <summary>
/// Returned by <see cref="ModelProviderService.CreateProvider"/> once the
/// provider node + all child LanguageModel nodes have been written.
/// </summary>
public record ProviderCreationResult(MeshNode ProviderNode, IReadOnlyList<MeshNode> ModelNodes);

/// <summary>
/// Safe DTO for listing providers — exposes a SHA-256 fingerprint of the
/// key rather than the key itself, so the UI can show "is this set / has
/// this changed" without reading the literal credential.
/// </summary>
public record ProviderInfo
{
    public string NodePath { get; init; } = "";
    public string Provider { get; init; } = "";
    public string? Label { get; init; }
    public string? Endpoint { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public IReadOnlyList<string> ModelIds { get; init; } = Array.Empty<string>();
    public string ApiKeyFingerprint { get; init; } = "(empty)";
}

internal static class ImmutableArrayExtensions
{
    public static System.Collections.Immutable.ImmutableArray<T> ToImmutableArrayCompat<T>(this IEnumerable<T> source) =>
        System.Collections.Immutable.ImmutableArray.CreateRange(source);
}
