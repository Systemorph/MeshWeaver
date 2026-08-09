using MeshWeaver.Graph;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI;

/// <summary>
/// Mesh-node type for model TIERS — the usage rungs (<c>utility</c>, <c>chat</c>, <c>reasoning</c>,
/// <c>coding</c>) an agent asks for instead of naming a model id. Companion to
/// <see cref="LanguageModelNodeType"/> and <see cref="ModelProviderNodeType"/>, and listed beside
/// them in the AI menu, because it is the third thing you configure when you set a deployment up:
/// which models exist, whose key pays for them, and what each one is FOR.
///
/// <para><b>Why a node type rather than an enum.</b> A tier set that only the framework can change is
/// a tier set nobody tunes. As nodes they are visible, searchable, editable in the standard node
/// editor, and extensible — rename <c>reasoning</c>, add a <c>vision</c> rung, re-rank the lot — with
/// no code change, no config key, and no redeploy.</para>
///
/// <para><b>Where they live.</b> Under <see cref="RootNamespace"/> (<c>Provider/Tier</c>), inside the
/// existing <c>Provider</c> catalog partition. Deliberately not a partition of its own: <c>Provider</c>
/// is already served, already carries the PublicRead <c>_Policy</c> the pickers need, and is already
/// part of the static-repo import set — so tiers arrive with zero new deployment surface.</para>
///
/// <para><b>Seeded create-if-absent.</b> <see cref="BuiltInLanguageModelProvider"/> emits the four
/// shipped tiers with <see cref="SyncBehavior.ExcludeThisAndChildren"/>, so the platform creates them
/// once and never writes over them again — an operator's rename, re-rank or purpose text survives
/// every redeploy, exactly like an admin's provider key does.</para>
/// </summary>
public static class ModelTierNodeType
{
    /// <summary>NodeType discriminator value.</summary>
    public const string NodeType = "ModelTier";

    /// <summary>
    /// Namespace the tier registry lives under — <c>Provider/Tier</c>, a sub-namespace of the
    /// existing model-catalog partition. Tier nodes are <c>Provider/Tier/{tierId}</c>.
    /// </summary>
    public const string RootNamespace = $"{ModelProviderNodeType.RootNamespace}/Tier";

    /// <summary>The node path of a tier by id: <c>Provider/Tier/{tierId}</c>.</summary>
    /// <param name="tierId">The tier id (e.g. <c>coding</c>).</param>
    /// <returns>The tier's node path.</returns>
    public static string PathFor(string tierId) => $"{RootNamespace}/{tierId}";

    /// <summary>
    /// Registers the <c>ModelTier</c> MeshNode definition + content type. Called from
    /// <see cref="LanguageModelNodeType.AddLanguageModelType{TBuilder}"/> so a deployment that wires
    /// language models always gets the tiers that classify them.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="serveFromPartition">Partitions served from the database; when the model catalog
    /// is DB-synced the in-memory type-def is registered DEFINITION-ONLY (see
    /// <see cref="ModelProviderNodeType.AddModelProviderType{TBuilder}"/> for why).</param>
    /// <returns>The builder, for chaining.</returns>
    public static TBuilder AddModelTierType<TBuilder>(this TBuilder builder,
        IReadOnlySet<string>? serveFromPartition = null)
        where TBuilder : MeshBuilder
    {
        var dbSynced = serveFromPartition is not null
            && (serveFromPartition.Contains("Model")
                || serveFromPartition.Contains(ModelProviderNodeType.RootNamespace));

        var typeDefinition = CreateMeshNode();
        if (dbSynced)
            typeDefinition = typeDefinition with { IsDefinitionOnly = true };
        builder.AddMeshNodes(typeDefinition);
        builder.ConfigureHub(config => config
            .WithType<ModelTierDefinition>(nameof(ModelTierDefinition)));
        return builder;
    }

    /// <summary>MeshNode definition for <c>nodeType:ModelTier</c>.</summary>
    /// <returns>The type-definition node.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Model Tier",
        Icon = "/static/NodeTypeIcons/task-list.svg",
        IsSatelliteType = false,
        // Creatable and editable through the standard node editor — that is the point of making a
        // tier a node. Kept out of free-text search results (like ModelProvider) so the registry
        // does not clutter every query; the AI menu's Tiers catalog is where you browse them.
        ExcludeFromContext = new HashSet<string> { "search" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ModelTierDefinition>())
    };
}
