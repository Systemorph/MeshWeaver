using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Per-(thread, model) token usage — the record of how many input/output tokens ONE model
/// consumed in ONE thread. Stored as a SATELLITE MeshNode at <c>{threadPath}/_Usage/{modelKey}</c>
/// (keyed by model) and accumulated across the thread's rounds.
///
/// <para>This is the SINGLE SOURCE OF TRUTH for token/cost reporting: the <see cref="Thread"/>
/// node itself carries NO token state — all cost tracking lives here, outside the thread. Cost is
/// NOT stored; it is derived on read from the configured model prices (<see cref="ModelPricing"/>),
/// so a price change re-prices historical usage. <see cref="UserId"/> + <see cref="ThreadId"/> are
/// denormalized onto the content so usage is queryable <c>nodeType:TokenUsage</c> across the mesh —
/// by thread (the satellite's parent) AND by model, and rolled up per user / per space.</para>
/// </summary>
public record TokenUsage
{
    /// <summary>ObjectId of the user who owns the thread (per-user usage roll-up). Null if unknown.</summary>
    public string? UserId { get; init; }

    /// <summary>Path of the thread this usage belongs to (equals the satellite node's MainNode).</summary>
    public string? ThreadId { get; init; }

    /// <summary>The bare model id (e.g. <c>claude-opus-4-8</c>) — the satellite's key dimension.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// Cumulative input (prompt) tokens for this model in this thread — the FULL prompt-token
    /// count including any cache hits/writes. <see cref="CacheReadTokens"/> and
    /// <see cref="CacheWriteTokens"/> are SUBSETS of this (see <see cref="UsageTokens"/>).
    /// </summary>
    public long InputTokens { get; init; }

    /// <summary>Cumulative output (completion) tokens for this model in this thread.</summary>
    public long OutputTokens { get; init; }

    /// <summary>
    /// Cumulative cache-READ (cache-hit) prompt tokens — a subset of <see cref="InputTokens"/>.
    /// Billed at the reduced cache-read rate. Zero when the provider reports no prompt caching.
    /// </summary>
    public long CacheReadTokens { get; init; }

    /// <summary>
    /// Cumulative cache-WRITE (cache-creation) prompt tokens — a subset of <see cref="InputTokens"/>.
    /// Billed at the premium cache-write rate. Zero on the OpenAI wire (no separate write) and when
    /// the provider reports no prompt caching.
    /// </summary>
    public long CacheWriteTokens { get; init; }

    /// <summary>Returns a copy with the given round's counts added.</summary>
    public TokenUsage Add(long inputTokens, long outputTokens, long cacheReadTokens = 0, long cacheWriteTokens = 0)
        => this with
        {
            InputTokens = InputTokens + inputTokens,
            OutputTokens = OutputTokens + outputTokens,
            CacheReadTokens = CacheReadTokens + cacheReadTokens,
            CacheWriteTokens = CacheWriteTokens + cacheWriteTokens,
        };
}

/// <summary>
/// The <see cref="TokenUsage"/> satellite NodeType. Like Activity / Comment, it is a
/// system-generated satellite — excluded from search and create contexts, with access delegated to
/// the MainNode (the thread) via <see cref="SatelliteAccessRule"/> (Read needs Read on the thread;
/// Create/Update need Update on the thread).
/// </summary>
public static class TokenUsageNodeType
{
    /// <summary>The NodeType discriminator for token-usage satellite nodes (<c>TokenUsage</c>).</summary>
    public const string NodeType = "TokenUsage";

    /// <summary>The satellite sub-namespace under a thread — usage lives at <c>{threadPath}/_Usage/{modelKey}</c>.</summary>
    public const string SatelliteSegment = "_Usage";

    /// <summary>
    /// Registers the <see cref="TokenUsage"/> satellite NodeType on the mesh builder:
    /// the node definition, autocomplete exclusion, and the MainNode-delegating
    /// <c>SatelliteAccessRule</c>.
    /// </summary>
    /// <typeparam name="TBuilder">The concrete <c>MeshBuilder</c> type, returned for fluent chaining.</typeparam>
    /// <param name="builder">The mesh builder to register on.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static TBuilder AddTokenUsageType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SatelliteAccessRule(NodeType, sp.GetRequiredService<IMessageHub>()));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Creates the <see cref="TokenUsage"/> NodeType definition — a search/create-excluded
    /// satellite type whose per-node hub hosts a data source over <see cref="TokenUsage"/> content.
    /// </summary>
    /// <returns>The NodeType definition mesh node.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Token Usage",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<TokenUsage>())
    };

    /// <summary>
    /// Records ONE round's token usage onto the per-model satellite at
    /// <c>{threadPath}/_Usage/{modelKey}</c>, ACCUMULATING input/output across the thread's rounds
    /// (keyed by model). A no-token round is a no-op. Returns an <see cref="IObservable{T}"/> that
    /// completes when the satellite is persisted (fail-open: it never errors). The caller subscribes
    /// it as an INDEPENDENT side effect — it MUST NOT be chained before the round's terminal status
    /// write (that delayed the terminal write and gated round-completion on a slow satellite write).
    /// The satellite is a SEPARATE node; the GUI chip and the token tests WAIT for it (a
    /// <c>Where(...).Timeout</c> read), so it can land shortly AFTER the terminal status.
    ///
    /// <para>Two NON-poisoning phases (rounds run serially per thread, so this read-modify-write is
    /// race-free): (1) create-only EnsureExists via <see cref="IMeshService.CreateNode"/> (a mesh-targeted
    /// CreateNodeRequest — never a point GetMeshNodeStream read of an absent node, which would trip the
    /// MeshNodeStreamCache storm breaker); then (2) accumulate via the OWNER's authoritative
    /// <c>GetMeshNodeStream(path).Update</c> on the now-existing node, which reads the live current
    /// value and adds this round's tokens (exact across rounds, unlike a lagged CQRS query read).</para>
    /// </summary>
    public static IObservable<System.Reactive.Unit> RecordUsage(
        IMessageHub hub, string threadPath, string? userId,
        string? modelId, int? inputTokens, int? outputTokens, ILogger? logger = null,
        int? cacheReadTokens = null, int? cacheWriteTokens = null)
    {
        long inTok = inputTokens ?? 0;
        long outTok = outputTokens ?? 0;
        long cacheReadTok = cacheReadTokens ?? 0;
        long cacheWriteTok = cacheWriteTokens ?? 0;
        if (inTok == 0 && outTok == 0 && cacheReadTok == 0 && cacheWriteTok == 0)
            return Observable.Return(System.Reactive.Unit.Default); // no-token round → no-op

        var model = string.IsNullOrWhiteSpace(modelId) ? "(unknown)" : modelId!;
        var key = new string(model.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var ns = $"{threadPath}/{SatelliteSegment}";
        var usagePath = $"{ns}/{key}";

        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(System.Reactive.Unit.Default);

        // 🚨 Two NON-poisoning phases. The OLD code read the (first-round-ABSENT) satellite via a point
        // GetMeshNodeStream(usagePath) and created it with an UNTARGETED CreateOrUpdateNodeRequest — both
        // bugs (since 616b4e27f):
        //   • the point-read of an absent node opens a SubscribeRequest to a non-existent owner → NotFound
        //     → trips the MeshNodeStreamCache STORM BREAKER (2s+ backoff), which then fast-fails EVERY
        //     reader of usagePath (the GUI ThreadTokenChip AND the token tests' WaitForUsage) for the
        //     whole window — "No node found at …/_Usage/…". (MeshNodeStreamCache.cs storm breaker /
        //     project_aisettings_create_storm_fix / feedback_optional_node_query_not_access.)
        //   • the untargeted CreateOrUpdateNodeRequest never reaches HandleCreateOrUpdateNodeRequest (it
        //     lives on the MESH hub — IMeshService.CreateNode targets hub.GetMeshHub().Address), so from
        //     this per-node thread hub the satellite was never created at all.
        // Phase 1: EnsureExists via meshService.CreateNode of a ZERO-token satellite — CREATE-ONLY, so an
        //   existing satellite (round 2+) is left untouched (CreateNode throws NodeAlreadyExists → caught
        //   → continue). meshService.CreateNode posts a CreateNodeRequest TARGETED at the mesh hub and is
        //   NOT a point-read, so it neither mis-routes nor poisons. It guarantees the node + its owning
        //   per-node hub exist before the accumulate.
        // Phase 2: accumulate via the OWNER's authoritative stream.Update — the node now exists, so the
        //   read-modify-write reads the LIVE current value and adds this round's tokens. Race-free (rounds
        //   are serial per thread) and EXACT across rounds (the cumulative invariant), unlike a lagged
        //   CQRS query read which could miss a prior round's write.
        var freshNode = new MeshNode(key, ns)
        {
            Name = model,
            NodeType = NodeType,
            State = MeshNodeState.Active,
            MainNode = threadPath,
            Content = new TokenUsage { UserId = userId, ThreadId = threadPath, Model = model },
        };

        return meshService.CreateNode(freshNode)
            .Select(_ => true)
            // Already exists (every round after the first for this model) → keep going to the accumulate.
            // A DIFFERENT failure (e.g. RLS) must NOT fall through to Phase 2: .Update on a node that was
            // never created would re-open the absent-node point-access this fix exists to avoid. Rethrow
            // so the terminal Catch fails the usage write open without touching the stream.
            .Catch((Exception ex) =>
                ex is InvalidOperationException
                && ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                    ? Observable.Return(false)
                    : Observable.Throw<bool>(ex))
            .SelectMany(_ => hub.GetWorkspace().GetMeshNodeStream(usagePath)
                .Update(node =>
                {
                    var cur = node.ContentAs<TokenUsage>(hub.JsonSerializerOptions, logger)
                              ?? new TokenUsage { UserId = userId, ThreadId = threadPath, Model = model };
                    return node with { Content = cur.Add(inTok, outTok, cacheReadTok, cacheWriteTok) };
                }))
            .Select(_ => System.Reactive.Unit.Default)
            // Subscribed as an INDEPENDENT side effect (NOT chained before the terminal status write),
            // so it can never block the round. Still cap + fail open as basic hygiene: a wedged create
            // or accumulate resolves to a no-op rather than leaking a live subscription.
            .Timeout(TimeSpan.FromSeconds(15), Observable.Return(System.Reactive.Unit.Default))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "[TokenUsage] RecordUsage failed for {Path}", usagePath);
                return Observable.Return(System.Reactive.Unit.Default);
            });
    }
}
