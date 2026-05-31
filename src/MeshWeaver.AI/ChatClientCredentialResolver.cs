using System.Collections.Concurrent;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Outcome of a credential lookup. <see cref="Source"/> is a short
/// human-readable tag identifying which rung of the resolution chain
/// produced the value — appears in factory logs so a stale or wrong key
/// can be traced back to the MeshNode (or IOptions) that supplied it.
/// </summary>
public record CredentialResolution(string? Endpoint, string? ApiKey, string Source)
{
    public static readonly CredentialResolution Missing = new(null, null, "missing");
}

/// <summary>
/// Unified Endpoint + ApiKey lookup for AI chat-client factories.
///
/// <para>Consumes the same <c>workspace.GetQuery</c> snapshot the chat
/// model-picker uses (<see cref="AgentPickerProjection.BuildModelQueries"/>
/// returns <c>nodeType:LanguageModel|ModelProvider</c>) — one synced
/// subscription, two consumers. See the <c>SyncedMeshNodeQueries</c>
/// architecture doc for the canonical query semantics.</para>
///
/// <para>Resolution precedence (top wins):</para>
/// <list type="number">
///   <item><b>Explicit <see cref="ModelDefinition.ProviderRef"/></b> →
///         <see cref="ModelProviderConfiguration"/> at that path. This is
///         the normal path — both <see cref="BuiltInLanguageModelProvider"/>
///         and <c>ModelProviderService</c> stamp the reference when they
///         create the LanguageModel node.</item>
///   <item><b>Conventional fallback</b> at
///         <c>Model/{ModelDefinition.Provider}</c> — covers legacy catalog
///         entries that didn't stamp <see cref="ModelDefinition.ProviderRef"/>.</item>
///   <item><b>Legacy ModelDefinition fields</b>
///         (<see cref="ModelDefinition.ApiKeySecretRef"/> /
///         <see cref="ModelDefinition.Endpoint"/>) — stamped per-model on
///         older catalog rollouts.</item>
///   <item><see cref="CredentialResolution.Missing"/> — caller falls back
///         to its <c>IOptions&lt;...Configuration&gt;</c> binding.</item>
/// </list>
///
/// <para>User-partition ModelProvider visibility: callers (notably
/// <c>AgentChatClient</c>) invoke <see cref="WatchPartition"/> to extend
/// the snapshot with <c>{userPartition}/Model/...</c> nodes. Without this
/// only the root catalog is visible — sufficient for system-default
/// deployments but blind to per-user BYO keys. Subscriptions are
/// idempotent per partition.</para>
/// </summary>
public sealed class ChatClientCredentialResolver : IDisposable
{
    private readonly IMessageHub hub;
    private readonly ILogger<ChatClientCredentialResolver>? logger;

    // Both populated from the same synced query (type alternation
    // LanguageModel|ModelProvider), so a single Subscribe drives both
    // dictionaries. ModelDefinitionsById is keyed by id (what the chat
    // picker selects); ProvidersByPath is keyed by full MeshNode path
    // (what ModelDefinition.ProviderRef points at).
    private readonly ConcurrentDictionary<string, ModelDefinition> modelsById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ModelProviderConfiguration> providersByPath =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IDisposable> partitionSubscriptions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object initLock = new();
    private IDisposable? rootSubscription;
    private bool disposed;

    public ChatClientCredentialResolver(IMessageHub hub)
    {
        this.hub = hub;
        logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<ChatClientCredentialResolver>();
    }

    /// <summary>
    /// Idempotent — starts the root-namespace synced query on first call.
    /// Uses <see cref="AgentPickerProjection.BuildModelQueries"/> with no
    /// context path, so the result set is the static built-in catalog. The
    /// per-user-partition extension is opt-in via <see cref="WatchPartition"/>.
    /// </summary>
    public void EnsureSubscription()
    {
        if (rootSubscription != null) return;
        lock (initLock)
        {
            if (rootSubscription != null) return;
            var workspace = hub.GetWorkspace();
            // Share the picker's root cache entry: same id, same queries.
            // workspace.GetQuery is keyed on id; using the no-context
            // ModelsQueryId means any picker that also subscribes without
            // a context path joins the same upstream Replay(1).RefCount()
            // — see SyncedMeshNodeQueries.md "Caching by id".
            rootSubscription = AgentPickerProjection
                .ObserveSnapshot(workspace, hub,
                    AgentPickerProjection.ModelsQueryId,
                    AgentPickerProjection.BuildModelQueries())
                .Subscribe(IngestSnapshot);
        }
    }

    /// <summary>
    /// Subscribe to ModelProvider + LanguageModel nodes under a user
    /// partition so their credentials participate in <see cref="Resolve"/>
    /// lookups. Called from the per-chat <c>AgentChatClient</c> with the
    /// chat user's partition (their <see cref="MeshWeaver.Messaging.AccessContext.ObjectId"/>).
    /// Idempotent on partition.
    /// </summary>
    public IDisposable WatchPartition(string userPartition)
    {
        if (string.IsNullOrEmpty(userPartition)) return Disposable.Empty;
        EnsureSubscription();
        return partitionSubscriptions.GetOrAdd(userPartition, p =>
        {
            var workspace = hub.GetWorkspace();
            return AgentPickerProjection
                .ObserveSnapshot(workspace, hub,
                    $"ChatClientCredentialResolver.Partition/{p}",
                    AgentPickerProjection.BuildModelQueries(currentPath: p))
                .Subscribe(IngestSnapshot);
        });
    }

    /// <summary>
    /// Resolve credentials for a model. <paramref name="modelId"/> is the
    /// LanguageModel id the chat selected (e.g. <c>claude-opus-4-7</c>).
    /// Walks the precedence chain documented on the class.
    /// </summary>
    public CredentialResolution Resolve(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return CredentialResolution.Missing;
        EnsureSubscription();

        if (!modelsById.TryGetValue(modelId, out var def))
            return CredentialResolution.Missing;

        // 1. Explicit ProviderRef — the normal path.
        if (!string.IsNullOrEmpty(def.ProviderRef)
            && providersByPath.TryGetValue(def.ProviderRef, out var byRef)
            && HasAnyCredential(byRef))
        {
            return new CredentialResolution(byRef.Endpoint, byRef.ApiKey, $"providerRef:{def.ProviderRef}");
        }

        // 2. Conventional fallback: Model/{Provider} in the root namespace.
        if (!string.IsNullOrEmpty(def.Provider))
        {
            var conventional = $"{ModelProviderNodeType.RootNamespace}/{def.Provider}";
            if (providersByPath.TryGetValue(conventional, out var byConvention)
                && HasAnyCredential(byConvention))
            {
                return new CredentialResolution(byConvention.Endpoint, byConvention.ApiKey, $"convention:{conventional}");
            }
        }

        // 3. Legacy fields stamped directly on the ModelDefinition.
        if (!string.IsNullOrEmpty(def.ApiKeySecretRef) || !string.IsNullOrEmpty(def.Endpoint))
        {
            return new CredentialResolution(def.Endpoint, def.ApiKeySecretRef, "model-node");
        }

        return CredentialResolution.Missing;
    }

    /// <summary>
    /// Returns the <see cref="ModelDefinition.Provider"/> string for a
    /// given model id, looked up from the live snapshot. Factories call
    /// this to gate their <c>Supports(modelName)</c> on the model's
    /// declared provider rather than on the model id alone (so the same
    /// <c>claude-*</c> id can route to direct-Anthropic or Azure-Claude
    /// based on which provider node owns it).
    /// </summary>
    public string? GetProviderForModel(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return null;
        EnsureSubscription();
        return modelsById.TryGetValue(modelId, out var def) ? def.Provider : null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        rootSubscription?.Dispose();
        foreach (var s in partitionSubscriptions.Values) s.Dispose();
        partitionSubscriptions.Clear();
    }

    private void IngestSnapshot(IEnumerable<MeshNode> snapshot)
    {
        foreach (var node in snapshot)
        {
            if (string.IsNullOrEmpty(node.Path)) continue;
            if (string.Equals(node.NodeType, LanguageModelNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
            {
                var def = ExtractContent<ModelDefinition>(node.Content);
                if (def != null && !string.IsNullOrEmpty(def.Id))
                    modelsById[def.Id] = def;
            }
            else if (string.Equals(node.NodeType, ModelProviderNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
            {
                var cfg = ExtractContent<ModelProviderConfiguration>(node.Content);
                if (cfg != null && !string.IsNullOrEmpty(cfg.Provider))
                    providersByPath[node.Path] = cfg;
            }
        }
    }

    private T? ExtractContent<T>(object? content) where T : class
    {
        return content switch
        {
            T typed => typed,
            JsonElement je => TryDeserialise<T>(je),
            _ => null,
        };
    }

    private T? TryDeserialise<T>(JsonElement je) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(je.GetRawText(), hub.JsonSerializerOptions); }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to deserialise content as {Type}", typeof(T).Name);
            return null;
        }
    }

    private static bool HasAnyCredential(ModelProviderConfiguration p) =>
        !string.IsNullOrEmpty(p.ApiKey) || !string.IsNullOrEmpty(p.Endpoint);

    private static class Disposable
    {
        public static readonly IDisposable Empty = new EmptyDisposable();
        private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    }
}
