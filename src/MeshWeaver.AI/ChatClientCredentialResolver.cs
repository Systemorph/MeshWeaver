using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
/// <para>Reads LIVE from the same <c>workspace.GetQuery</c> snapshot the chat
/// model-picker uses (<see cref="AgentPickerProjection.BuildModelQueries"/>
/// returns <c>nodeType:LanguageModel|ModelProvider</c>). No materialised
/// dictionary of node content is held — every <see cref="Resolve"/> /
/// <see cref="GetProviderForModel"/> call grabs the current snapshot from the
/// workspace's per-id cache (already <c>Replay(1).RefCount()</c>), so it can
/// never go stale. See the <c>SyncedMeshNodeQueries</c> architecture doc for
/// the canonical query semantics.</para>
///
/// <para>Resolution precedence (top wins):</para>
/// <list type="number">
///   <item><b>Explicit <see cref="ModelDefinition.ProviderRef"/></b> →
///         <see cref="ModelProviderConfiguration"/> at that path. This is
///         the normal path — both <see cref="BuiltInLanguageModelProvider"/>
///         and <c>ModelProviderService</c> stamp the reference when they
///         create the LanguageModel node.</item>
///   <item><b>Conventional fallback</b> at
///         <c>Provider/{ModelDefinition.Provider}</c> — covers legacy catalog
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
/// <c>AgentChatClient</c>) invoke <see cref="WatchPartition"/> to widen the
/// live query with <c>{userPartition}/_Memex/...</c> + <c>{userPartition}/Provider/...</c>
/// nodes. Without this
/// only the root catalog is visible — sufficient for system-default
/// deployments but blind to per-user BYO keys. Calls are idempotent per
/// partition; they record which subtrees the next <see cref="Resolve"/>
/// should include, they do NOT cache node content.</para>
/// </summary>
public sealed class ChatClientCredentialResolver : IDisposable
{
    private readonly IMessageHub hub;
    private readonly ILogger<ChatClientCredentialResolver>? logger;
    private readonly IProviderKeyProtector? keyProtector;

    // "What to look at", NOT cached node content. These are tiny, immutable
    // path sets describing which extra subtrees the live query must union in.
    // Swapped atomically under `gate`; they never hold MeshNode data, so they
    // can never go stale — the data is always read fresh from the workspace.
    private readonly object gate = new();
    private ImmutableHashSet<string> watchedPartitions = ImmutableHashSet<string>.Empty;
    private ImmutableHashSet<string> sharedProviderPaths = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    private bool disposed;

    public ChatClientCredentialResolver(IMessageHub hub)
    {
        this.hub = hub;
        logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<ChatClientCredentialResolver>();
        // ModelProvider.ApiKey is encrypted at rest — decrypt at the moment we
        // hand the credential to a factory. Null when the protector isn't
        // registered (then values are plaintext anyway → Decrypt passthrough).
        keyProtector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
    }

    /// <summary>
    /// No-op retained for API compatibility. The root catalog is always
    /// included by every live read (see <see cref="BuildModelQueries"/> with
    /// no context path), so there is no eager root subscription to start.
    /// </summary>
    public void EnsureSubscription() { }

    /// <summary>
    /// Widen subsequent <see cref="Resolve"/> reads to include ModelProvider +
    /// LanguageModel nodes under a user partition. Called from the per-chat
    /// <c>AgentChatClient</c> with the chat user's partition (their
    /// <see cref="MeshWeaver.Messaging.AccessContext.ObjectId"/>). Idempotent
    /// on partition. Records the path only — no node content is cached.
    /// </summary>
    public IDisposable WatchPartition(string userPartition)
    {
        if (string.IsNullOrEmpty(userPartition)) return Disposable.Empty;
        lock (gate)
            watchedPartitions = watchedPartitions.Add(userPartition);
        return Disposable.Empty;
    }

    /// <summary>
    /// Make a shared / organisation <c>ModelProvider</c> subtree usable by
    /// <paramref name="userId"/> under <b>use-without-see</b>: the provider node
    /// is read under a SYSTEM identity (so its <see cref="MeshWeaver.Mesh.Security.Permission.Api"/>-gated
    /// key reaches the resolver process), but <see cref="Resolve"/> only hands
    /// the key to a user who holds <see cref="MeshWeaver.Mesh.Security.Permission.Read"/>
    /// on the subtree — evaluated LIVE at resolve time via
    /// <c>hub.CheckPermission</c>. The raw key never leaves the server.
    /// Idempotent per path; records the path only — no node content is cached.
    /// </summary>
    public IDisposable WatchSharedProvider(string providerPath, string userId)
    {
        if (string.IsNullOrEmpty(providerPath) || string.IsNullOrEmpty(userId))
            return Disposable.Empty;
        lock (gate)
            sharedProviderPaths = sharedProviderPaths.Add(providerPath);
        return Disposable.Empty;
    }

    /// <summary>
    /// Gate for shared-provider keys. Non-shared providers (root catalog, the
    /// resolving user's own partition via <see cref="WatchPartition"/>) are not
    /// gated here — RLS already governed their visibility at read time.
    /// Shared providers fail closed: no Read ⇒ no key. The Read check is
    /// evaluated LIVE (no cached gate result) against the user's effective
    /// permissions on the subtree.
    /// </summary>
    private bool IsAllowedSharedAccess(string providerPath)
    {
        if (!sharedProviderPaths.Contains(providerPath)) return true;
        var userId = hub.ServiceProvider.GetService<AccessService>()?.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId)) return false;
        return ReadLatest(hub.CheckPermission(providerPath, userId, Permission.Read), false);
    }

    /// <summary>
    /// Resolve credentials for a model. <paramref name="modelId"/> is the
    /// LanguageModel id the chat selected (e.g. <c>claude-opus-4-7</c>).
    /// Walks the precedence chain documented on the class against a LIVE
    /// snapshot of the model + provider nodes.
    /// </summary>
    public CredentialResolution Resolve(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return CredentialResolution.Missing;

        var snapshot = ReadSnapshot();
        var def = FindModelDefinition(snapshot, modelId);
        try { var ctx = hub.ServiceProvider.GetService<AccessService>()?.Context; System.IO.File.AppendAllText(@"C:\tmp\claude\resolve-probe.log", $"{DateTime.Now:HH:mm:ss.fff} Resolve({modelId}) ctx={ctx?.ObjectId ?? "(null)"} watched=[{string.Join(",", watchedPartitions)}] snap={snapshot.Count} paths=[{string.Join(";", snapshot.Select(n => n.Path + ":" + n.NodeType))}] def={(def == null ? "NULL" : $"id={def.Id},ref={def.ProviderRef}")}\n"); } catch { }
        if (def == null)
            return CredentialResolution.Missing;

        // 1. Explicit ProviderRef — the normal path.
        if (!string.IsNullOrEmpty(def.ProviderRef)
            && TryGetProvider(snapshot, def.ProviderRef, out var byRef)
            && HasAnyCredential(byRef!)
            && IsAllowedSharedAccess(def.ProviderRef))
        {
            return new CredentialResolution(byRef!.Endpoint, Decrypt(byRef.ApiKey), $"providerRef:{def.ProviderRef}");
        }

        // 2. Conventional fallback: Provider/{Provider} in the catalog namespace.
        if (!string.IsNullOrEmpty(def.Provider))
        {
            var conventional = $"{ModelProviderNodeType.RootNamespace}/{def.Provider}";
            if (TryGetProvider(snapshot, conventional, out var byConvention)
                && HasAnyCredential(byConvention!)
                && IsAllowedSharedAccess(conventional))
            {
                return new CredentialResolution(byConvention!.Endpoint, Decrypt(byConvention.ApiKey), $"convention:{conventional}");
            }
        }

        // 3. Legacy fields stamped directly on the ModelDefinition.
        if (!string.IsNullOrEmpty(def.ApiKeySecretRef) || !string.IsNullOrEmpty(def.Endpoint))
        {
            return new CredentialResolution(def.Endpoint, Decrypt(def.ApiKeySecretRef), "model-node");
        }

        return CredentialResolution.Missing;
    }

    /// <summary>
    /// Resolve the per-user <b>Connect</b> token for a CLI harness — the user's OWN subscription
    /// token captured by the login (Connect) flow and stored, encrypted, as a <c>ModelProvider</c>
    /// node at <c>{UserNamespacePath(userPartition)}/{providerName}</c>
    /// (e.g. <c>{user}/_Memex/ClaudeCode</c>). Returns the decrypted key, or <c>null</c> when the
    /// user hasn't connected.
    ///
    /// <para>This is deliberately NOT <see cref="Resolve(string)"/>: a CLI harness (Claude Code /
    /// GitHub Copilot) authenticates with the user's <i>subscription</i> token, never with a selected
    /// MODEL's API key. Passing the selected model's key (e.g. the DeepSeek/AzureFoundry key) is
    /// exactly what produced the atioz "Not logged in" failure. Best-effort: the authoritative login
    /// also lives in the CLI's own per-user config dir (<c>.credentials.json</c> on the shared
    /// volume), so a not-yet-warm node read simply leaves the env var unset and the CLI falls back to
    /// its config dir.</para>
    /// </summary>
    public string? ResolveConnectToken(string providerName, string? userPartition)
    {
        if (string.IsNullOrEmpty(providerName) || string.IsNullOrEmpty(userPartition))
            return null;
        // Widen subsequent snapshots to the user's own partition (idempotent), then read.
        WatchPartition(userPartition!);
        var providerPath = $"{ModelProviderNodeType.UserNamespacePath(userPartition!)}/{providerName}";
        return TryGetProvider(ReadSnapshot(), providerPath, out var cfg) && cfg is not null
            ? Decrypt(cfg.ApiKey)
            : null;
    }

    /// <summary>
    /// Decrypts a stored credential. Passthrough when no protector is registered
    /// or the value is legacy plaintext (see <see cref="IProviderKeyProtector"/>).
    /// </summary>
    private string? Decrypt(string? stored) =>
        keyProtector is null ? stored : keyProtector.Unprotect(stored);

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
        var def = FindModelDefinition(ReadSnapshot(), modelId);
        return def?.Provider;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (gate)
        {
            watchedPartitions = ImmutableHashSet<string>.Empty;
            sharedProviderPaths = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Reads the current LanguageModel + ModelProvider snapshot from the
    /// workspace's synced-query cache. The query unions the root catalog, each
    /// watched user partition, and each shared provider subtree. The shared
    /// subtrees are read under a SYSTEM identity so an Api-gated provider node
    /// surfaces (the per-user Read gate is enforced separately in
    /// <see cref="IsAllowedSharedAccess"/>).
    /// </summary>
    private IReadOnlyList<MeshNode> ReadSnapshot()
    {
        ImmutableHashSet<string> partitions;
        ImmutableHashSet<string> shared;
        lock (gate)
        {
            partitions = watchedPartitions;
            shared = sharedProviderPaths;
        }

        var workspace = hub.GetWorkspace();

        // Root catalog + each watched user partition share one cache id; the
        // workspace caches the union by id (Replay(1).RefCount upstream).
        var baseQueries = BuildModelQueries(partitions);
        var baseId = "ChatClientCredentialResolver|" + string.Join(",", partitions.OrderBy(p => p, StringComparer.Ordinal));
        var nodes = ReadLatest(workspace.GetQuery(baseId, baseQueries), Array.Empty<MeshNode>() as IEnumerable<MeshNode>);

        if (shared.IsEmpty)
            return nodes as IReadOnlyList<MeshNode> ?? nodes.ToList();

        // Shared provider subtrees must be read under a system identity so the
        // Api-gated provider node + its LanguageModel children are visible to
        // the resolver process (the Read gate is enforced at hand-out time).
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var merged = new Dictionary<string, MeshNode>(StringComparer.Ordinal);
        foreach (var n in nodes)
            if (n.Path != null) merged[n.Path] = n;

        var typeFilter = $"{LanguageModelNodeType.NodeType}|{ModelProviderNodeType.NodeType}";
        foreach (var path in shared)
        {
            var sharedQuery = $"namespace:{path} nodeType:{typeFilter} scope:selfAndDescendants";
            var sharedObs = workspace.GetQuery($"ChatClientCredentialResolver.Shared|{path}", sharedQuery);
            var asSystem = accessService is null
                ? sharedObs
                : Observable.Using(accessService.ImpersonateAsSystem, _ => sharedObs);
            var sharedNodes = ReadLatest(asSystem, Array.Empty<MeshNode>() as IEnumerable<MeshNode>);
            foreach (var n in sharedNodes)
                if (n.Path != null) merged[n.Path] = n;
        }

        return merged.Values.ToList();
    }

    /// <summary>
    /// Builds the live model queries: the root catalog plus one subtree query
    /// per watched user partition. Mirrors
    /// <see cref="AgentPickerProjection.BuildModelQueries"/>'s root +
    /// per-partition shape (the per-partition entry uses <c>currentPath</c>).
    /// </summary>
    private static string[] BuildModelQueries(ImmutableHashSet<string> partitions)
    {
        if (partitions.IsEmpty)
            return AgentPickerProjection.BuildModelQueries();

        var typeFilter = $"{LanguageModelNodeType.NodeType}|{ModelProviderNodeType.NodeType}";
        var queries = new List<string>
        {
            $"namespace:{ModelProviderNodeType.RootNamespace} nodeType:{typeFilter} scope:descendants",
        };
        // Each watched partition is a USER partition (WatchPartition is called
        // with the resolving user's id). A user's own providers/models live in
        // their dotfile namespace ({user}/_Memex/…); union the context-partition
        // {p}/Provider subtree too so org/space-shared providers still resolve
        // (many queries are fine — the synced collection unions them).
        foreach (var p in partitions)
        {
            queries.Add($"namespace:{ModelProviderNodeType.UserNamespacePath(p)} nodeType:{typeFilter} scope:descendants");
            queries.Add($"namespace:{p}/{ModelProviderNodeType.RootNamespace} nodeType:{typeFilter} scope:descendants");
        }
        return queries.ToArray();
    }

    private ModelDefinition? FindModelDefinition(IReadOnlyList<MeshNode> snapshot, string modelId)
    {
        foreach (var node in snapshot)
        {
            if (!string.Equals(node.NodeType, LanguageModelNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                continue;
            var def = ExtractContent<ModelDefinition>(node.Content);
            if (def != null && string.Equals(def.Id, modelId, StringComparison.OrdinalIgnoreCase))
                return def;
        }
        return null;
    }

    private bool TryGetProvider(IReadOnlyList<MeshNode> snapshot, string providerPath, out ModelProviderConfiguration? cfg)
    {
        foreach (var node in snapshot)
        {
            if (node.Path == null
                || !string.Equals(node.Path, providerPath, StringComparison.Ordinal)
                || !string.Equals(node.NodeType, ModelProviderNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                continue;
            var extracted = ExtractContent<ModelProviderConfiguration>(node.Content);
            if (extracted != null && !string.IsNullOrEmpty(extracted.Provider))
            {
                cfg = extracted;
                return true;
            }
        }
        cfg = null;
        return false;
    }

    /// <summary>
    /// Synchronously grabs the current value of a warm <c>Replay(1).RefCount()</c>
    /// observable (the shape every <c>workspace.GetQuery</c> / permission stream
    /// returns). Subscribe replays the latest value inline; we capture it and
    /// dispose. Returns <paramref name="fallback"/> when nothing has been
    /// emitted yet (cold / first read before the synced query warms).
    /// </summary>
    private static T ReadLatest<T>(IObservable<T>? source, T fallback)
    {
        if (source is null) return fallback;
        var captured = fallback;
        var got = false;
        using var _ = source.Subscribe(v => { captured = v; got = true; }, _ => { });
        return got ? captured : fallback;
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
