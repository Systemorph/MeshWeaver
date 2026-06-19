using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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
}
