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
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<TokenUsage>())
    };

    /// <summary>The sentinel stored when no model identifier reached the recorder.</summary>
    public const string UnknownModel = "(unknown)";

    /// <summary>
    /// The identifier a usage satellite is keyed and reported by. Pure.
    ///
    /// <para>🚨 <b>Why this exists.</b> <see cref="RecordUsage"/>'s callers pass
    /// <c>actualModel ?? effectiveModel ?? request.ModelName</c>, and <c>ThreadComposer.ModelName</c>
    /// is BY DESIGN a node PATH (its <c>[MeshNode]</c> picker persists the catalogue node's path).
    /// Stored verbatim, a path became the key dimension beside the catalogue id that denotes the very
    /// same model — measured in production 2026-08-26, where one DeepSeek model was recorded under
    /// four spellings, one of them <c>_Provider/Anthropic/DeepSeek-V3-0324</c> (a path, under the
    /// wrong provider). Usage split across rows and no catalogue lookup could price it.</para>
    ///
    /// <para>So a registry path is reduced to the catalogue id it denotes: the id is everything after
    /// <c>{Registry}/{Provider}/</c>, which is exactly how the catalogue node is addressed
    /// (<c>Provider/OpenRouter/anthropic/claude-opus-5</c> → <c>anthropic/claude-opus-5</c>). The
    /// legacy underscore registry (<c>_Provider/…</c>) reduces the same way.</para>
    ///
    /// <para><b>What this deliberately does NOT do:</b> reconcile a provider's DISPLAY NAME
    /// (<c>DeepSeek-V4-Pro</c>) with the catalogue id (<c>deepseek/deepseek-v4-pro</c>). That needs a
    /// catalogue alias lookup, not string surgery — guessing would merge genuinely distinct models.
    /// Everything that is not a registry path is passed through untouched.</para>
    /// </summary>
    /// <param name="modelId">The identifier as handed to the recorder — a catalogue id, a provider's
    /// reported model, or a catalogue node path.</param>
    /// <returns>The normalized identifier, or <see cref="UnknownModel"/> when none was supplied.</returns>
    public static string NormalizeModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return UnknownModel;
        var trimmed = modelId.Trim().Trim('/');
        if (trimmed.Length == 0)
            return UnknownModel;

        // A registry path is {Registry}/{Provider}/{catalogue id}; the id itself may contain a
        // slash (vendor/model), so only the first TWO segments are the address, never more.
        var segments = trimmed.Split('/');
        if (segments.Length > 2
            && (segments[0].Equals("Provider", StringComparison.OrdinalIgnoreCase)
                || segments[0].Equals("_Provider", StringComparison.OrdinalIgnoreCase)))
            return string.Join('/', segments.Skip(2));

        return trimmed;
    }

    /// <summary>
    /// The satellite's node id for a model identifier: <see cref="NormalizeModelId"/> with every
    /// non-alphanumeric mapped to <c>_</c>, so the key is a path-safe slug and a path keys identically
    /// to the catalogue id it denotes. Pure.
    /// </summary>
    /// <param name="modelId">The identifier as handed to the recorder.</param>
    /// <returns>The satellite node id (the <c>{modelKey}</c> in <c>{thread}/_Usage/{modelKey}</c>).</returns>
    public static string SatelliteKey(string? modelId) =>
        new(NormalizeModelId(modelId).Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

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
    /// race-free): (1) create-only via <see cref="IMeshService.CreateNode"/> (a mesh-targeted
    /// CreateNodeRequest — never a point GetMeshNodeStream read of an absent node, which would trip the
    /// MeshNodeStreamCache storm breaker) of a satellite ALREADY CARRYING this round's counts; then
    /// (2) — <b>only when that create reported the node already existed</b> — accumulate via the
    /// OWNER's authoritative <c>GetMeshNodeStream(path).Update</c>, which reads the live current value
    /// and adds this round's tokens (exact across rounds, unlike a lagged CQRS query read).</para>
    ///
    /// <para>🚨 <b>Phase 1 must never write a ZERO-token satellite</b> (#1812). It used to create the
    /// node with all counters at zero and let phase 2 add the round's tokens on top, which published a
    /// durable, readable, all-zero satellite in the window between the two writes — measured at ~60 ms
    /// on an idle box, unbounded under load. Every reader saw it: the GUI's <c>ThreadTokenChip</c>
    /// rendered <c>↑0 ↓0 · $0</c> for that window, and the token tests observed it as their FIRST
    /// emission and then had to out-wait it (CI run 32070434174 timed out on exactly that snapshot,
    /// <c>InputTokens = 0</c>, with the correct value landing after the assertion's budget). Worse, the
    /// 15 s cap below fails OPEN, so a phase 2 that never lands leaves those zeros as the PERMANENT
    /// record of a round that did consume tokens. Seeding the create with the round's counts removes
    /// the intermediate outright: the first round is one atomic write that is correct the instant it is
    /// visible, and only rounds 2+ pay for a read-modify-write. It costs nothing to do it this way —
    /// the create already had to carry a content instance.</para>
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
        {
            // #595: make the silent no-op diagnosable. A terminal (Cancelled/Error) round whose
            // provider never emitted usage lands here with all-zero counts and vanishes from
            // accounting — "provider returned no counts" was indistinguishable from "no work done".
            // Debug, not Information: this fires on every zero-token round, so it must stay off the
            // Loki hot path (AGENTS.md log-cost rule).
            logger?.LogDebug("[TokenUsage] RecordUsage no-op for {ThreadPath} (model {ModelId}): "
                + "provider reported no tokens", threadPath, modelId ?? "(unknown)");
            return Observable.Return(System.Reactive.Unit.Default); // no-token round → no-op
        }

        var model = NormalizeModelId(modelId);
        var key = SatelliteKey(modelId);
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
        // Phase 1: CREATE-ONLY via meshService.CreateNode, of a satellite already carrying THIS ROUND'S
        //   counts — so an existing satellite (round 2+) is left untouched (CreateNode throws
        //   NodeAlreadyExists → caught → phase 2). meshService.CreateNode posts a CreateNodeRequest
        //   TARGETED at the mesh hub and is NOT a point-read, so it neither mis-routes nor poisons.
        //   🚨 The counts go in HERE, not in a follow-up write (#1812): a create seeded with zeros
        //   publishes a durable all-zero satellite until phase 2 lands, and that intermediate is
        //   readable by everyone (the GUI chip renders it; the token tests saw it as their first
        //   emission and timed out out-waiting it). On the first round this branch is now the WHOLE
        //   write — atomic, correct the instant it is visible, and immune to the fail-open cap below.
        // Phase 2: reached ONLY when the create said the node already exists. Accumulate via the OWNER's
        //   authoritative stream.Update — the node demonstrably exists, so the read-modify-write reads
        //   the LIVE current value and adds this round's tokens. Race-free (rounds are serial per
        //   thread) and EXACT across rounds (the cumulative invariant), unlike a lagged CQRS query read
        //   which could miss a prior round's write. Running it on the create's SUCCESS path too would
        //   double-count, which is precisely why the zero seed existed.
        var freshNode = new MeshNode(key, ns)
        {
            Name = model,
            NodeType = NodeType,
            State = MeshNodeState.Active,
            MainNode = threadPath,
            Content = new TokenUsage
            {
                UserId = userId,
                ThreadId = threadPath,
                Model = model,
                InputTokens = inTok,
                OutputTokens = outTok,
                CacheReadTokens = cacheReadTok,
                CacheWriteTokens = cacheWriteTok,
            },
        };

        return meshService.CreateNode(freshNode)
            // TRUE = we created it, so this round's counts are already durable and phase 2 must NOT run.
            .Select(_ => true)
            // Already exists (every round after the first for this model) → FALSE, go accumulate.
            // A DIFFERENT failure (e.g. RLS) must NOT fall through to Phase 2: .Update on a node that was
            // never created would re-open the absent-node point-access this fix exists to avoid. Rethrow
            // so the terminal Catch fails the usage write open without touching the stream.
            .Catch((Exception ex) =>
                ex is InvalidOperationException
                && ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                    ? Observable.Return(false)
                    : Observable.Throw<bool>(ex))
            .SelectMany(alreadyDurable => alreadyDurable
                ? Observable.Return(System.Reactive.Unit.Default)
                : hub.GetWorkspace().GetMeshNodeStream(usagePath)
                    .Update(node =>
                    {
                        var cur = node.ContentAs<TokenUsage>(hub.JsonSerializerOptions, logger)
                                  ?? new TokenUsage { UserId = userId, ThreadId = threadPath, Model = model };
                        return node with { Content = cur.Add(inTok, outTok, cacheReadTok, cacheWriteTok) };
                    })
                    .Select(_ => System.Reactive.Unit.Default))
            // Subscribed as an INDEPENDENT side effect (NOT chained before the terminal status write),
            // so it can never block the round. Still cap + fail open as basic hygiene: a wedged create
            // or accumulate resolves to a no-op rather than leaking a live subscription.
            //
            // 🚨 Fail open LOUDLY. This used to swap in a bare Observable.Return, so a cap that fired
            // completed the chain SUCCESSFULLY and logged nothing at all — the round's tokens were gone
            // from accounting with no trace, and "phase 2 landed late" was indistinguishable from
            // "phase 2 never landed" for anyone reading the satellite afterwards. Defer so the warning
            // fires only when the cap actually trips, not when the chain is built.
            .Timeout(TimeSpan.FromSeconds(15), Observable.Defer(() =>
            {
                logger?.LogWarning(
                    "[TokenUsage] RecordUsage TIMED OUT after 15s for {Path} (model {ModelId}) — "
                    + "in={InputTokens} out={OutputTokens} cacheRead={CacheReadTokens} "
                    + "cacheWrite={CacheWriteTokens} were NOT recorded",
                    usagePath, model, inTok, outTok, cacheReadTok, cacheWriteTok);
                return Observable.Return(System.Reactive.Unit.Default);
            }))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "[TokenUsage] RecordUsage failed for {Path}", usagePath);
                return Observable.Return(System.Reactive.Unit.Default);
            });
    }
}
