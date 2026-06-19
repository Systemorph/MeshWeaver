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

    /// <summary>Cumulative input (prompt) tokens for this model in this thread.</summary>
    public long InputTokens { get; init; }

    /// <summary>Cumulative output (completion) tokens for this model in this thread.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Returns a copy with the given round's counts added.</summary>
    public TokenUsage Add(long inputTokens, long outputTokens)
        => this with { InputTokens = InputTokens + inputTokens, OutputTokens = OutputTokens + outputTokens };
}

/// <summary>
/// The <see cref="TokenUsage"/> satellite NodeType. Like Activity / Comment, it is a
/// system-generated satellite — excluded from search and create contexts, with access delegated to
/// the MainNode (the thread) via <see cref="SatelliteAccessRule"/> (Read needs Read on the thread;
/// Create/Update need Update on the thread).
/// </summary>
public static class TokenUsageNodeType
{
    public const string NodeType = "TokenUsage";

    /// <summary>The satellite sub-namespace under a thread — usage lives at <c>{threadPath}/_Usage/{modelKey}</c>.</summary>
    public const string SatelliteSegment = "_Usage";

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
    /// (keyed by model, per your "key by model"). Fire-and-forget: a no-token round is a no-op.
    ///
    /// <para>Read-modify-write, but SAFE and race-free: rounds run serially per thread (one terminal
    /// path at a time), so there is no concurrent writer to the same model node within a thread. The
    /// current value is read AUTHORITATIVELY off the live node stream (<c>GetMeshNodeStream().Take(1)</c>),
    /// bounded by <c>Timeout</c> + <c>Catch</c> so an absent node (first round for this model) resolves
    /// to null rather than parking on a never-acked subscribe — then the create-or-update lands it via
    /// <see cref="CreateOrUpdateNodeRequest"/> (NOT a point-read <c>.Update</c>, which NotFound-storms).</para>
    /// </summary>
    public static void RecordUsage(
        IMessageHub hub, string threadPath, string? userId,
        string? modelId, int? inputTokens, int? outputTokens, ILogger? logger = null)
    {
        long inTok = inputTokens ?? 0;
        long outTok = outputTokens ?? 0;
        if (inTok == 0 && outTok == 0)
            return; // parity with the old per-model accumulator: empty rounds never accrue a node

        var model = string.IsNullOrWhiteSpace(modelId) ? "(unknown)" : modelId!;
        var key = new string(model.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var ns = $"{threadPath}/{SatelliteSegment}";
        var usagePath = $"{ns}/{key}";

        hub.GetWorkspace().GetMeshNodeStream(usagePath)
            .Select(n => n?.ContentAs<TokenUsage>(hub.JsonSerializerOptions, logger))
            .Take(1)
            // Absent node (first round for this model): the read hangs (no node) → Timeout switches to
            // null; or it faults (not-found) → Catch switches to null. Either way we create fresh. An
            // existing node emits its current value so the accumulate adds onto it.
            .Timeout(TimeSpan.FromSeconds(5), Observable.Return<TokenUsage?>(null))
            .Catch((Exception _) => Observable.Return<TokenUsage?>(null))
            .SelectMany(current =>
            {
                var content = (current ?? new TokenUsage { UserId = userId, ThreadId = threadPath, Model = model })
                    .Add(inTok, outTok);
                var node = new MeshNode(key, ns)
                {
                    Name = model,
                    NodeType = NodeType,
                    State = MeshNodeState.Active,
                    MainNode = threadPath,
                    Content = content,
                };
                return hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node));
            })
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "[TokenUsage] RecordUsage failed for {Path}", usagePath));
    }
}
