using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
        // When the "Harness" partition is DB-synced (static-repo import), DO NOT register the
        // read-only in-memory static surfaces — on the distributed/PG path queries never consult
        // the in-memory adapter, so the harness catalog would be invisible (empty picker / the
        // combobox spins). The import materializes harnesses into the partition; PG serves them.
        // Mirrors AddAgentType — see HarnessStaticRepoSource + Doc/Architecture/StaticRepoImport.md.
        var dbSynced = serveFromPartition?.Contains(RootNamespace) == true;

        // The in-memory "Harness" NodeType definition. On the DB-synced path it is registered
        // DEFINITION-ONLY: it still supplies the HubConfiguration delegate BY NAME (the catalog's
        // instances enrich through it) and proves the type exists, but it is NOT served as the
        // runtime node at @Harness — Postgres owns the nodeType:NodeType partition root the import
        // materializes (HarnessStaticRepoSource.PartitionRoot). Without IsDefinitionOnly the
        // in-memory type-def and the DB root BOTH claim @Harness → the bare-address GetDataRequest
        // bounces → routing-loop guard fails it → ds/Harness faults → the harness picker vanishes.
        // See Doc/Architecture/NodeTypeCatalogs.md.
        var typeDefinition = CreateMeshNode();
        if (dbSynced)
            typeDefinition = typeDefinition with { IsDefinitionOnly = true };
        builder.AddMeshNodes(typeDefinition);
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

    /// <summary>How long the install probe waits for the picked harness node before treating the
    /// harness as not installed (graceful fallback to the default agent path — never a wedge).</summary>
    public static readonly TimeSpan InstallProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Resolves the harness for a round AND enforces the per-user install contract: a
    /// <see cref="Harness.RequiresInstall"/> harness (the CLI ones) runs only when the picked node
    /// path — <c>{user}/Harness/{id}</c> or <c>{space}/Harness/{id}</c>, localized there by its
    /// Store plugin — still resolves to an Active node. A missing/deleted node (never installed,
    /// uninstalled, or a stale global <c>Harness/{id}</c> path from before the catalog gate) emits
    /// <c>null</c>, which execution treats exactly like an unresolvable harness: log + fall back to
    /// the default MeshWeaver agent path. The probe is one read off the shared
    /// <c>IMeshNodeStreamCache</c> handle, bounded by <see cref="InstallProbeTimeout"/> with a
    /// graceful <c>null</c> sink — a stalled probe degrades, it never wedges the round.
    /// </summary>
    public static IObservable<IHarness?> ResolveInstalledHarness(
        IMessageHub hub, string? harnessPath, ILogger? logger = null)
    {
        var harness = ResolveHarness(hub.ServiceProvider, harnessPath);
        if (harness is null || !harness.Definition.RequiresInstall)
            return Observable.Return(harness);

        return Observable.Defer(() => hub.GetWorkspace()
                .GetMeshNodeStream(harnessPath!)
                .Where(node => node is not null)
                .Take(1)
                .Timeout(InstallProbeTimeout)
                .Select(node =>
                {
                    if (node is { State: MeshNodeState.Active })
                        return harness;
                    logger?.LogWarning(
                        "[Harness] '{Harness}' requires install but the picked node is not Active — "
                        + "not installed (or uninstalled); falling back to the default agent path",
                        harnessPath);
                    return (IHarness?)null;
                }))
            // The graceful degrade sink — a Timeout here just means "no such node" (never
            // installed, or a stale pre-gate global path); anything else (denied read, a
            // stalled stream) is logged WITH the exception so production diagnosis isn't
            // reduced to a misleading "not installed".
            .Catch<IHarness?, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[Harness] install probe for '{Harness}' failed or timed out — treating as "
                    + "not installed; falling back to the default agent path",
                    harnessPath);
                return Observable.Return<IHarness?>(null);
            });
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
