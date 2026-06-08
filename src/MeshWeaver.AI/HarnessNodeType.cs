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
    public static TBuilder AddHarnessType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        builder.ConfigureServices(services =>
        {
            // The native MeshWeaver harness ships from this assembly. CLI harnesses
            // add their own IHarness from their DLLs (TryAddEnumerable composes them).
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarness, MeshWeaverHarness>());
            services.TryAddSingleton<BuiltInHarnessProvider>();
            services.AddSingleton<IStaticNodeProvider>(sp => sp.GetRequiredService<BuiltInHarnessProvider>());
            services.AddSingleton<IPartitionStorageProvider>(sp =>
                new StaticNodePartitionStorageProvider(
                    RootNamespace,
                    sp.GetRequiredService<BuiltInHarnessProvider>(),
                    description: "Built-in harness definitions (read-only)."));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Resolves the registered <see cref="IHarness"/> for <paramref name="harnessId"/>
    /// (the value stored in <see cref="Thread.SelectedHarness"/>), or null when none
    /// matches — in which case execution uses the default MeshWeaver agent/model path.
    /// </summary>
    public static IHarness? ResolveHarness(IServiceProvider services, string? harnessId) =>
        string.IsNullOrEmpty(harnessId)
            ? null
            : services.GetServices<IHarness>()
                .FirstOrDefault(h => string.Equals(h.Id, harnessId, StringComparison.OrdinalIgnoreCase));

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
