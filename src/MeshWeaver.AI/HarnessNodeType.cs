using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI;

/// <summary>
/// The "Harness" node type. Harnesses are first-class catalog nodes (one per
/// registered <see cref="IHarness"/>) describing the available execution harnesses.
/// The MeshWeaver harness is registered here; Claude Code / GitHub Copilot register
/// their own <see cref="IHarness"/> from their respective assemblies, and
/// <see cref="BuiltInHarnessProvider"/> projects every registered harness into a node.
/// </summary>
public static class HarnessNodeType
{
    /// <summary>NodeType discriminator for harness nodes.</summary>
    public const string NodeType = "Harness";

    /// <summary>Namespace (partition) the built-in harness catalog lives under.</summary>
    public const string RootNamespace = "Harness";

    /// <summary>
    /// Registers the Harness type node, the built-in MeshWeaver harness, and the
    /// catalog provider that turns every registered <see cref="IHarness"/> into a
    /// read-only node under the <c>Harness</c> partition.
    /// </summary>
    public static TBuilder AddHarnessType<TBuilder>(this TBuilder builder,
        IReadOnlySet<string>? serveFromPartition = null) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        // When the "Harness" partition is DB-synced (static-repo import), DO NOT register the
        // read-only in-memory static surfaces — on the distributed/PG path queries never consult
        // the in-memory adapter, so the harness catalog would be invisible (empty picker / the
        // combobox spins). The import materializes harnesses into the partition; PG serves them.
        // Mirrors AddAgentType — see HarnessStaticRepoSource + Doc/Architecture/StaticRepoImport.md.
        var dbSynced = serveFromPartition?.Contains(RootNamespace) == true;
        builder.ConfigureServices(services =>
        {
            // The native MeshWeaver harness ships from this assembly. CLI harnesses
            // add their own IHarness from their DLLs (TryAddEnumerable composes them).
            // Registered regardless of dbSynced — the import SOURCE (HarnessStaticRepoSource)
            // wraps BuiltInHarnessProvider, which reads this IHarness collection.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarness, MeshWeaverHarness>());
            services.TryAddSingleton<BuiltInHarnessProvider>();
            if (!dbSynced)
            {
                services.AddSingleton<IStaticNodeProvider>(sp => sp.GetRequiredService<BuiltInHarnessProvider>());
                services.AddSingleton<IPartitionStorageProvider>(sp =>
                    new StaticNodePartitionStorageProvider(
                        RootNamespace,
                        sp.GetRequiredService<BuiltInHarnessProvider>(),
                        description: "Built-in harness definitions (read-only)."));
            }
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Resolves the registered <see cref="IHarness"/> for <paramref name="harnessId"/>
    /// (the value stored in <see cref="ThreadComposer.Harness"/> — a bare id or a picked
    /// node PATH like <c>Harness/MeshWeaver</c>, normalized via <see cref="SelectionId.IdOf"/>),
    /// or null when none matches — in which case execution uses the default MeshWeaver
    /// agent/model path.
    /// </summary>
    public static IHarness? ResolveHarness(IServiceProvider services, string? harnessId)
    {
        var id = SelectionId.IdOf(harnessId);
        return string.IsNullOrEmpty(id)
            ? null
            : services.GetServices<IHarness>()
                .FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The type-definition node for nodeType="Harness".</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Harness",
        Icon = "/static/NodeTypeIcons/bot.svg",
        IsSatelliteType = false,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Harness>())
    };
}
