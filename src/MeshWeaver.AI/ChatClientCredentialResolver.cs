using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
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
    /// <summary>Sentinel result meaning no credential could be resolved — the caller falls back to its <c>IOptions</c> configuration binding.</summary>
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

    // Reactive snapshot. A PERSISTENT subscription to the watched GetQuery union keeps the latest
    // LanguageModel + ModelProvider snapshot warm and cached, so Resolve reads a ready value instead
    // of synchronously grabbing a COLD observable. The old per-call ReadLatest grab subscribed to the
    // synced query and disposed inline — dropping the RefCount to 0 and CANCELLING the async warm-up
    // before it completed, so under load the snapshot never populated within the caller's window
    // (the ProviderKeyEncryptionTest "observable did not emit within 15s" CI flake). The persistent
    // subscription warms once, uninterrupted, and every later emission refreshes the cache.
    private volatile IReadOnlyList<MeshNode> cachedSnapshot = Array.Empty<MeshNode>();
    private IDisposable? snapshotSubscription;

    // Single-flight guard for the on-miss authoritative read-through (TriggerAuthoritativeRefresh).
    // A caller's poll loop (e.g. the chat factory retrying Resolve) can hit a miss many times per
    // second; this ensures at most ONE source-of-truth re-read is in flight, so a miss can never storm
    // the mesh with re-reads (the 2026-06-08-class failure mode we explicitly avoid).
    private volatile bool refreshInFlight;

    // The in-flight authoritative-refresh subscription. Its `.Timeout(10s)` schedules a TimerQueue
    // timer whose closure captures THIS resolver → hub → MeshService → MessageHub. Left untracked, that
    // timer is a live GC root that pins the WHOLE mesh for up to 10s after Dispose() — the
    // MeshHubDisposalLeakTest "mesh hub survived disposal … TimerQueue timer" leak, and the sustained
    // memory pressure (every disposed-but-still-rooted mesh lingers) behind the suite-wide GC-stall
    // timeouts. Tracked here and torn down in Dispose() so disposal collapses that window to zero.
    private IDisposable? refreshSubscription;

    /// <summary>
    /// Initialises the resolver with its hub, resolving the optional logger and provider-key
    /// protector from the hub's service provider.
    /// </summary>
    /// <param name="hub">The message hub whose workspace and services back the live credential snapshot.</param>
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
    /// Establishes the persistent snapshot subscription for the root catalog (and any
    /// partitions watched so far) so the cache is warming before the first <see cref="Resolve"/>.
    /// Idempotent: re-subscribes to the current watched set.
    /// </summary>
    public void EnsureSubscription() => RebuildSubscription();

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
        bool changed;
        lock (gate)
        {
            var prev = watchedPartitions;
            watchedPartitions = watchedPartitions.Add(userPartition);
            changed = !ReferenceEquals(watchedPartitions, prev);
        }
        // Widen the persistent subscription to include the new partition's queries.
        if (changed) RebuildSubscription();
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
        bool changed;
        lock (gate)
        {
            var prev = sharedProviderPaths;
            sharedProviderPaths = sharedProviderPaths.Add(providerPath);
            changed = !ReferenceEquals(sharedProviderPaths, prev);
        }
        // Widen the persistent subscription to include the shared subtree (read under System).
        if (changed) RebuildSubscription();
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
    /// LanguageModel id the chat selected (e.g. <c>claude-opus-4-7</c>) OR the
    /// full LanguageModel node PATH the composer persists
    /// (e.g. <c>Provider/OpenAICompatible/qwen-small</c> — the picker stores
    /// the node path per <c>AgentPickerProjection.ToModelInfo</c>'s
    /// Path-carrying contract; both forms resolve here).
    /// Walks the precedence chain documented on the class against a LIVE
    /// snapshot of the model + provider nodes.
    /// </summary>
    public CredentialResolution Resolve(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return CredentialResolution.Missing;

        var snapshot = ReadSnapshot();

        // There can be MORE THAN ONE LanguageModel with the same id: a keyless ROOT catalog entry
        // (registered from config via AgentPickerProjection/AddLanguageModelCatalogSource) AND the
        // user's OWN provider node that carries the real key. Returning the FIRST match arbitrarily
        // lets the keyless catalog entry SHADOW the user's keyed one — Resolve hands back a null ApiKey
        // and the caller never gets a usable credential. Which duplicate sorts first in the merged
        // snapshot is nondeterministic, hence the ProviderKeyEncryptionTest "~50%" flake. Walk EVERY
        // candidate and prefer the one that actually yields a key.
        var candidates = FindModelDefinitions(snapshot, modelId);
        if (candidates.Count == 0)
        {
            // Not in the warm snapshot at all — consult the source of truth once (single-flight) so a
            // subsequent Resolve sees it. CQRS cache-miss read-through, NOT a blind poll/timer.
            TriggerAuthoritativeRefresh();
            return CredentialResolution.Missing;
        }

        CredentialResolution? keyless = null;
        foreach (var def in candidates)
        {
            var r = TryResolveForDefinition(snapshot, def);
            if (r is null) continue;
            if (!string.IsNullOrEmpty(r.ApiKey))
                return r;          // a candidate that yields a real key always wins over a keyless one
            keyless ??= r;         // remember an endpoint-only / keyless resolution as a last resort
        }

        // No candidate produced a key. The keyed (user) model may simply not be in the warm snapshot
        // yet — read the source of truth once so a later Resolve picks it up (single-flight).
        TriggerAuthoritativeRefresh();
        return keyless ?? CredentialResolution.Missing;
    }

    /// <summary>
    /// Walks the credential precedence chain (ProviderRef → conventional Provider/{name} → legacy
    /// model-node fields) for ONE model definition. Returns <c>null</c> when this definition yields no
    /// credential at all (so <see cref="Resolve"/> can try the next same-id candidate).
    /// </summary>
    private CredentialResolution? TryResolveForDefinition(IReadOnlyList<MeshNode> snapshot, ModelDefinition def)
    {
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

        return null;
    }

    /// <summary>
    /// The DEFAULT model id to fall back to when a selected model no longer resolves — the
    /// LOWEST-<see cref="MeshNode.Order"/> <c>LanguageModel</c> in the live catalog whose credentials
    /// actually resolve (so the fallback is never ANOTHER broken model). Mirrors
    /// <c>AgentPickerProjection.ObserveDefaultComposer</c>'s "lowest Order wins" rule, read from the
    /// same warm snapshot this resolver already maintains. Returns <c>null</c> when no model in the
    /// catalog resolves (e.g. a deployment whose models bypass the catalog entirely).
    /// </summary>
    public string? ResolveDefaultModelId()
    {
        var snapshot = ReadSnapshot();
        return snapshot
            .Where(n => string.Equals(n.NodeType, LanguageModelNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
            .Select(n =>
            {
                var def = ExtractContent<ModelDefinition>(n.Content);
                // Rank by MeshNode.Order, falling back to the ModelDefinition's Order when the node
                // carries none — mirrors the picker (ToModelInfo reads def.Order), so a per-model
                // default (DeepSeek-V4-Flash → -1) is honoured whether the Order lives on the node
                // or only on its content. Without the def fallback a BYO model that set only
                // ModelDefinition.Order ranked 0 here while ranking correctly in the picker.
                var order = n.Order ?? (def?.Order ?? 0);
                return (Order: order, Id: def?.Id);
            })
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .OrderBy(x => x.Order)
            .Select(x => x.Id!)
            .FirstOrDefault(HasUsableCredential);
    }

    /// <summary>
    /// True when <paramref name="modelId"/> resolves to a credential the model factories can ACTUALLY
    /// use — i.e. a NON-EMPTY ApiKey. This is the honest "is this model usable" predicate, distinct
    /// from <see cref="Resolve"/><c> != Missing</c>: <see cref="Resolve"/> returns a KEYLESS resolution
    /// (an endpoint-only provider — e.g. <c>Provider/OpenAICompatible</c> with a gateway URL but no key)
    /// as non-<see cref="CredentialResolution.Missing"/>, yet every registered factory
    /// (<c>OpenAIChatClientAgentFactory</c> et al.) throws <c>"ApiKey is missing for model '…'"</c>
    /// without a key. Selecting such a model as a default/fallback surfaces that raw factory error to
    /// the user (the <c>z-ai/glm-5.2</c> report). Requiring a key here means default/fallback selection
    /// only ever lands on a model that will actually run. No working model regresses: a model whose key
    /// comes from the factory's own config/IOptions fallback (not a provider node) already resolves
    /// <see cref="CredentialResolution.Missing"/> today and was never eligible as a catalog default.
    /// </summary>
    public bool HasUsableCredential(string? modelId)
        => !string.IsNullOrEmpty(modelId)
           && !string.IsNullOrEmpty(Resolve(modelId!).ApiKey);

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
    /// The full co-hosted-CLI credential: the decrypted token/key, the
    /// <see cref="ModelProviderConfiguration.AuthMethod"/> that says WHICH env var to set, and the
    /// optional gateway base URL. All fields null when the user hasn't connected.
    /// </summary>
    public sealed record HarnessCredential(string? Token, string? AuthMethod, string? BaseUrl);

    /// <summary>
    /// Method-aware companion to <see cref="ResolveConnectToken"/>: resolves the token AND its
    /// <see cref="ModelProviderConfiguration.AuthMethod"/> (+ gateway <see cref="ModelProviderConfiguration.Endpoint"/>)
    /// so the harness sets the RIGHT env var — the fix that lets a stored API key actually authenticate
    /// (before, only <c>CLAUDE_CODE_OAUTH_TOKEN</c> was ever set). All-null when not connected.
    /// </summary>
    public HarnessCredential ResolveConnectCredential(string providerName, string? userPartition)
    {
        if (string.IsNullOrEmpty(providerName) || string.IsNullOrEmpty(userPartition))
            return new HarnessCredential(null, null, null);
        WatchPartition(userPartition!);
        var providerPath = $"{ModelProviderNodeType.UserNamespacePath(userPartition!)}/{providerName}";
        return TryGetProvider(ReadSnapshot(), providerPath, out var cfg) && cfg is not null
            ? new HarnessCredential(Decrypt(cfg.ApiKey), cfg.AuthMethod, cfg.Endpoint)
            : new HarnessCredential(null, null, null);
    }

    /// <summary>
    /// Decrypts a stored credential. Passthrough when no protector is registered
    /// or the value is legacy plaintext (see <see cref="IProviderKeyProtector"/>).
    /// </summary>
    private string? Decrypt(string? stored) =>
        keyProtector is null ? stored : keyProtector.Unprotect(stored);

    /// <summary>
    /// Returns the <see cref="ModelDefinition.Provider"/> string for a
    /// given model id OR full LanguageModel node path, looked up from the
    /// live snapshot. Factories call
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

    /// <summary>
    /// Canonical bare model id (what the provider's wire API expects in the
    /// <c>model</c> field) for a picker value that may be EITHER the bare id or
    /// the full LanguageModel node PATH (<c>Provider/{provider}/{modelId}</c> —
    /// the form the composer persists). When the value matches a LanguageModel
    /// node's path in the live snapshot, that node's <see cref="ModelDefinition.Id"/>
    /// is returned; otherwise the value passes through unchanged. This is the
    /// resolver-backed replacement for the <see cref="SelectionId.IdOf"/> last-segment
    /// heuristic (which mangles org/model-shaped ids like <c>qwen/qwen-2.5</c>).
    /// </summary>
    public string? ResolveModelId(string? modelNameOrPath)
    {
        if (string.IsNullOrEmpty(modelNameOrPath) || !modelNameOrPath.Contains('/'))
            return modelNameOrPath;
        var snapshot = ReadSnapshot();
        var def = FindDefinitionByNodePath(snapshot, modelNameOrPath);
        if (def != null)
            return def.Id;
        // A slash-bearing value that is itself a REGISTERED wire id (an org/model slug like
        // "z-ai/glm-5.2" — OpenRouter's id shape) must pass through UNCHANGED. This is the
        // double-normalization case: Initialize already collapsed the picker path to the bare id,
        // and the factory calls ResolveModelId again on that id — last-segmenting it here ("glm-5.2")
        // makes it resolve NO credentials ("ApiKey is missing for model 'glm-5.2'" on a configured
        // deployment). Match against the catalog snapshot AND the config-seeded model lists (the
        // latter are warm even when the snapshot isn't).
        if (IsRegisteredModelId(snapshot, modelNameOrPath))
            return modelNameOrPath;
        // Unresolved NODE PATH (node not yet in the warm snapshot): the wire id is the LAST SEGMENT
        // — never the whole path (sending "Provider/OpenAICompatible/qwen3.6:latest" as the model
        // name 400s). Every LanguageModel node-path shape carries a catalog marker segment —
        // Provider/{p}/{id}, {space}/Provider/{p}/{id}, {user}/_Memex/{p}/{id} — so ONLY those
        // last-segment. A slash-bearing value WITHOUT a marker is an org/model wire id we simply
        // don't know (yet) — cold snapshot, unseeded model: pass it through rather than mangle it.
        if (LooksLikeCatalogNodePath(modelNameOrPath))
            return modelNameOrPath[(modelNameOrPath.LastIndexOf('/') + 1)..];
        return modelNameOrPath;
    }

    /// <summary>
    /// True when <paramref name="value"/> is shaped like a LanguageModel node PATH: it contains a
    /// catalog marker segment (<c>Provider</c> — <see cref="ModelProviderNodeType.RootNamespace"/> —
    /// or the user dotfile namespace <c>_Memex</c>). Org/model wire ids ("z-ai/glm-5.2") carry
    /// neither, so an UNRECOGNIZED slug is never last-segmented even before the snapshot warms.
    /// </summary>
    private static bool LooksLikeCatalogNodePath(string value)
    {
        foreach (var segment in value.Split('/'))
        {
            if (string.Equals(segment, ModelProviderNodeType.RootNamespace, StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, ModelProviderNodeType.UserNamespace, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="value"/> is a registered model wire id: a LanguageModel node in the
    /// snapshot carries it as <see cref="ModelDefinition.Id"/>, or a configured catalog source lists it
    /// in its <c>{Section}:Models</c> array (the exact list <c>BuiltInLanguageModelProvider</c> seeds
    /// from — readable before the snapshot warms).
    /// </summary>
    private bool IsRegisteredModelId(IReadOnlyList<MeshNode> snapshot, string value)
    {
        foreach (var node in snapshot)
        {
            if (!string.Equals(node.NodeType, LanguageModelNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(ExtractContent<ModelDefinition>(node.Content)?.Id, value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var sources = hub.ServiceProvider.GetService<LanguageModelCatalogOptions>();
        var configuration = hub.ServiceProvider.GetService<IConfiguration>();
        if (sources is null || configuration is null) return false;
        foreach (var source in sources.Sources)
        {
            string[]? models;
            try
            {
                models = configuration.GetSection($"{source.SectionName}:Models")
                    .Get<string[]>();
            }
            catch
            {
                continue; // malformed section — same skip as the catalog seeder.
            }
            if (models is not null
                && models.Any(m => string.Equals(m, value, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the selected model may be sent tool/function definitions. Returns FALSE only when the
    /// resolved catalog entry is KNOWN not to support tools (<see cref="ModelDefinition.SupportsTools"/>
    /// == <c>false</c>) — an Ollama roleplay model (TieFighter) that returns HTTP 400 "does not support
    /// tools". Unknown / not-found / not-set → TRUE (assume supported — the historical behaviour).
    /// Accepts a bare id or a full node PATH; for a bare id that matches several catalog entries, a
    /// declared "no tools" on ANY of them wins (safer to omit tools than to 400 the whole round).
    /// </summary>
    public bool ModelSupportsTools(string? modelNameOrPath)
    {
        if (string.IsNullOrEmpty(modelNameOrPath)) return true;
        var snapshot = ReadSnapshot();
        if (modelNameOrPath.Contains('/'))
            return FindDefinitionByNodePath(snapshot, modelNameOrPath)?.SupportsTools != false;
        return !FindModelDefinitions(snapshot, modelNameOrPath).Any(d => d.SupportsTools == false);
    }

    /// <summary>
    /// Tears down the persistent snapshot subscription and any in-flight authoritative refresh so
    /// their timers stop GC-rooting the resolver (and the mesh hub it references). Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        IDisposable? sub;
        IDisposable? refresh;
        lock (gate)
        {
            watchedPartitions = ImmutableHashSet<string>.Empty;
            sharedProviderPaths = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
            sub = snapshotSubscription;
            snapshotSubscription = null;
            refresh = refreshSubscription;
            refreshSubscription = null;
        }
        sub?.Dispose();
        // Kill any in-flight authoritative refresh: its .Timeout(10s) timer otherwise keeps this
        // resolver (and the whole mesh hub it references) GC-rooted until the timer fires.
        refresh?.Dispose();
    }

    /// <summary>
    /// Returns the latest LanguageModel + ModelProvider snapshot, kept warm by the persistent
    /// <see cref="RebuildSubscription"/> subscription — NO synchronous grab of a cold observable.
    /// Lazily establishes the subscription if a caller resolves before any explicit
    /// <see cref="EnsureSubscription"/>/<see cref="WatchPartition"/>.
    /// </summary>
    private IReadOnlyList<MeshNode> ReadSnapshot()
    {
        if (snapshotSubscription is null) RebuildSubscription();
        return cachedSnapshot;
    }

    /// <summary>
    /// (Re)establishes the persistent snapshot subscription over the current watched partitions +
    /// shared provider subtrees. The base query unions the root catalog + each watched user
    /// partition; each shared subtree is read under a SYSTEM identity so an Api-gated provider node
    /// surfaces (the per-user Read gate is enforced separately in <see cref="IsAllowedSharedAccess"/>).
    /// <see cref="Observable.CombineLatest{T}(System.Collections.Generic.IEnumerable{IObservable{T}})"/>
    /// merges all sources; every emission swaps <see cref="cachedSnapshot"/> atomically. Because the
    /// subscription stays alive, the synced queries warm ONCE and uninterrupted — no per-call
    /// subscribe/dispose that cancels the warm-up.
    /// </summary>
    private void RebuildSubscription()
    {
        if (disposed) return;
        ImmutableHashSet<string> partitions;
        ImmutableHashSet<string> shared;
        lock (gate)
        {
            partitions = watchedPartitions;
            shared = sharedProviderPaths;
        }

        var workspace = hub.GetWorkspace();
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        // Root catalog + each watched user partition share one cache id; the
        // workspace caches the union by id (Replay(1).RefCount upstream).
        var baseQueries = BuildModelQueries(partitions);
        var baseId = "ChatClientCredentialResolver|" + string.Join(",", partitions.OrderBy(p => p, StringComparer.Ordinal));
        var sources = new List<IObservable<IEnumerable<MeshNode>>>
        {
            workspace.GetQuery(baseId, baseQueries)
        };

        // Shared provider subtrees: read so the Api-gated provider node + its LanguageModel children
        // are visible to the resolver process (the per-user Read gate is enforced at hand-out time
        // in IsAllowedSharedAccess). They are covered by the SYSTEM scope held over the whole
        // subscription below, so no per-source impersonation is needed.
        if (!shared.IsEmpty)
        {
            var typeFilter = $"{LanguageModelNodeType.NodeType}|{ModelProviderNodeType.NodeType}";
            foreach (var path in shared)
            {
                var sharedQuery = $"namespace:{path} nodeType:{typeFilter} scope:selfAndDescendants";
                sources.Add(workspace.GetQuery($"ChatClientCredentialResolver.Shared|{path}", sharedQuery));
            }
        }

        var combined = sources.Count == 1
            ? sources[0]
            : Observable.CombineLatest(sources).Select(MergeByPath);

        // 🚨 Hold a SYSTEM identity across the ENTIRE subscription lifetime. The resolver is
        // server-side infrastructure resolving credentials to inject into the chat client; its
        // GetQuery union opens cross-hub synced-query subscriptions to the root catalog, the user's
        // own partition, and shared subtrees. Those SubscribeRequests run on the synced query's
        // scheduler — where the caller's AccessContext has been wiped by the Rx hop — so without an
        // explicit identity they post a NULL AccessContext, the owning partition's PostPipeline fails
        // them CLOSED, and the query rides the 15s deadlock-guard timeout before recovering: the
        // ProviderKeyEncryptionTest / ConnectStrategyTest "observable did not emit within 15s" flake.
        // Observable.Using keeps the impersonation alive for the whole subscription so every
        // (re)subscribe carries System. Real security is enforced at hand-out time
        // (IsAllowedSharedAccess for shared providers; the user's own partition is theirs to read).
        var newSub = Observable.Using(
                () => accessService is null
                    ? System.Reactive.Disposables.Disposable.Empty
                    : accessService.ImpersonateAsSystem(),
                _ => combined)
            .Subscribe(
                nodes => cachedSnapshot = nodes as IReadOnlyList<MeshNode> ?? nodes.ToList(),
                ex => logger?.LogWarning(ex,
                    "ChatClientCredentialResolver snapshot stream faulted; cache retains last good snapshot"));

        IDisposable? old;
        lock (gate)
        {
            old = snapshotSubscription;
            snapshotSubscription = newSub;
        }
        old?.Dispose();
    }

    /// <summary>
    /// One-shot authoritative re-read of the watched union on a Resolve MISS. Unlike the persistent
    /// <see cref="RebuildSubscription"/> (which reads the RefCount-cached <c>GetQuery</c> — re-subscribing
    /// to the SAME id replays the same stale snapshot, and a one-shot grab of it cancels its own
    /// warm-up), this issues a fresh <see cref="IMeshService.Query{T}"/> — the raw live query that
    /// re-executes against the store right now — and merges its current snapshot into
    /// <see cref="cachedSnapshot"/>. By the time a miss happens the model's index/partition lag has
    /// typically resolved, so this fresh read surfaces it; if it is still lagging, the next miss tries
    /// again (single-flight, never concurrently). Read under SYSTEM (the per-user gate is enforced at
    /// hand-out in <see cref="IsAllowedSharedAccess"/>).
    /// </summary>
    private void TriggerAuthoritativeRefresh()
    {
        if (disposed || refreshInFlight) return;
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null) return;
        refreshInFlight = true;

        ImmutableHashSet<string> partitions;
        ImmutableHashSet<string> shared;
        lock (gate)
        {
            partitions = watchedPartitions;
            shared = sharedProviderPaths;
        }

        var queries = new List<string>(BuildModelQueries(partitions));
        if (!shared.IsEmpty)
        {
            var typeFilter = $"{LanguageModelNodeType.NodeType}|{ModelProviderNodeType.NodeType}";
            foreach (var path in shared)
                queries.Add($"namespace:{path} nodeType:{typeFilter} scope:selfAndDescendants");
        }

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var request = MeshQueryRequest.FromQueries(queries, WellKnownUsers.System);

        var sub = Observable.Using(
                () => accessService is null
                    ? System.Reactive.Disposables.Disposable.Empty
                    : accessService.ImpersonateAsSystem(),
                _ => meshService.Query<MeshNode>(request))
            .Where(c => c.Items is { Count: > 0 })
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Subscribe(
                c => { MergeIntoSnapshot(c.Items!); refreshInFlight = false; },
                _ => refreshInFlight = false);

        // Track the subscription so Dispose() tears down its .Timeout timer immediately. Single-flight
        // means at most one is ever live; dispose any prior (completed → no-op) before storing. If we
        // were disposed while subscribing, drop it now so the timer can't outlive the mesh.
        lock (gate)
        {
            if (disposed)
            {
                refreshInFlight = false;
                sub.Dispose();
                return;
            }
            refreshSubscription?.Dispose();
            refreshSubscription = sub;
        }
    }

    /// <summary>Merges a fresh authoritative snapshot into <see cref="cachedSnapshot"/> (fresh wins per path).</summary>
    private void MergeIntoSnapshot(IEnumerable<MeshNode> fresh)
    {
        var merged = new Dictionary<string, MeshNode>(StringComparer.Ordinal);
        foreach (var n in cachedSnapshot)
            if (n.Path != null) merged[n.Path] = n;
        foreach (var n in fresh)
            if (n.Path != null) merged[n.Path] = n;
        cachedSnapshot = merged.Values.ToList();
    }

    /// <summary>Dedupe-by-path merge of the base + shared snapshot lists (last write per path wins).</summary>
    private static IEnumerable<MeshNode> MergeByPath(IList<IEnumerable<MeshNode>> lists)
    {
        var merged = new Dictionary<string, MeshNode>(StringComparer.Ordinal);
        foreach (var list in lists)
            foreach (var n in list)
                if (n.Path != null) merged[n.Path] = n;
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
        modelId = CanonicalModelId(snapshot, modelId);
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

    /// <summary>
    /// ALL model definitions in the snapshot matching <paramref name="modelId"/> — there can be several
    /// (a keyless root catalog entry + the user's own keyed provider node share the same id). Callers
    /// that need credentials must try each (see <see cref="Resolve"/>), not just the first.
    /// Accepts BOTH the bare model id and the full LanguageModel node path (the composer's persisted
    /// form) — the path is canonicalised to its node's <see cref="ModelDefinition.Id"/> first, so a
    /// path selection still walks every same-id candidate (e.g. the user's keyed copy of a catalog model).
    /// </summary>
    private List<ModelDefinition> FindModelDefinitions(IReadOnlyList<MeshNode> snapshot, string modelId)
    {
        modelId = CanonicalModelId(snapshot, modelId);
        var result = new List<ModelDefinition>();
        foreach (var node in snapshot)
        {
            if (!string.Equals(node.NodeType, LanguageModelNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                continue;
            var def = ExtractContent<ModelDefinition>(node.Content);
            if (def != null && string.Equals(def.Id, modelId, StringComparison.OrdinalIgnoreCase))
                result.Add(def);
        }
        return result;
    }

    /// <summary>
    /// Collapses a full LanguageModel node PATH (the composer's persisted form —
    /// <c>Provider/{provider}/{modelId}</c>, see <c>AgentPickerProjection.ToModelInfo</c>) to that
    /// node's bare <see cref="ModelDefinition.Id"/>. A value that matches no node path (including
    /// every bare id — ids without '/' short-circuit) passes through unchanged.
    /// </summary>
    private string CanonicalModelId(IReadOnlyList<MeshNode> snapshot, string modelNameOrPath)
    {
        if (!modelNameOrPath.Contains('/'))
            return modelNameOrPath;
        return FindDefinitionByNodePath(snapshot, modelNameOrPath)?.Id ?? modelNameOrPath;
    }

    /// <summary>The ModelDefinition of the LanguageModel node at exactly <paramref name="nodePath"/>, or null.</summary>
    private ModelDefinition? FindDefinitionByNodePath(IReadOnlyList<MeshNode> snapshot, string nodePath)
    {
        foreach (var node in snapshot)
        {
            if (node.Path == null
                || !string.Equals(node.NodeType, LanguageModelNodeType.NodeType, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(node.Path, nodePath, StringComparison.OrdinalIgnoreCase))
                continue;
            var def = ExtractContent<ModelDefinition>(node.Content);
            if (def != null)
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
