using System.Collections.Immutable;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// What a module's BUILDER HOOK (<see cref="MeshNodeProviderAttribute.BuilderConfigurations"/>) contributes, decomposed into the
/// surfaces a live swap can re-apply (policy <c>module-live-update-default</c>,
/// <c>Doc/Architecture/LiveModuleUpdate</c> → "The builder hook").
/// </summary>
/// <param name="Nodes">Mesh nodes it added.</param>
/// <param name="Services">Root-service delegates it registered.</param>
/// <param name="MeshHubConfigurations">Configuration of the MESH hub.</param>
/// <param name="DefaultNodeHubConfigurations">Configuration of every per-node hub.</param>
/// <param name="MeshTypes">Types it registered on the mesh's shared type registry.</param>
/// <param name="AutocompleteExcludedTypes">Node types it excluded from autocomplete.</param>
/// <param name="Blockers">What it did that a live swap cannot re-apply — each named.</param>
public sealed record BuilderHookCapture(
    ImmutableList<MeshNode> Nodes,
    ImmutableList<Func<IServiceCollection, IServiceCollection>> Services,
    ImmutableList<Func<MessageHubConfiguration, MessageHubConfiguration>> MeshHubConfigurations,
    ImmutableList<Func<MessageHubConfiguration, MessageHubConfiguration>> DefaultNodeHubConfigurations,
    ImmutableList<(Type Type, string Name)> MeshTypes,
    ImmutableHashSet<string> AutocompleteExcludedTypes,
    ImmutableList<string> Blockers)
{
    /// <summary>A hook that contributed nothing.</summary>
    public static BuilderHookCapture Empty { get; } = new([], [], [], [], [], [], []);

    /// <summary>What has to stay the same across generations for the boot-time application of these
    /// contributions to keep meaning the same thing.</summary>
    public ImmutableList<string> Shape =>
        AutocompleteExcludedTypes.Order(StringComparer.Ordinal).Select(t => $"autocomplete-excluded:{t}").ToImmutableList();
}

public partial record MeshBuilder
{
    /// <summary>
    /// Runs <paramref name="hooks"/> against a CAPTURE builder and decomposes what they did into
    /// <see cref="BuilderHookCapture"/> — the conversion that makes a module contributing through the
    /// builder hook live-updatable: its nodes, root services, per-node-hub and mesh-hub configuration and
    /// mesh types are each served by a seam a swap can re-bind; anything else the hook touched is a
    /// named blocker. Throws what a hook throws.
    /// </summary>
    /// <param name="hooks">The module's builder hooks.</param>
    /// <param name="configuration">The deployment configuration the real builder would expose.</param>
    public static BuilderHookCapture CaptureBuilderHooks(
        IReadOnlyCollection<Func<MeshBuilder, MeshBuilder>> hooks, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        if (hooks.Count == 0)
            return BuilderHookCapture.Empty;
        var services = new List<Func<IServiceCollection, IServiceCollection>>();
        var capture = new MeshBuilder(services.Add, new Address("mesh", "module-capture"));
        if (configuration is not null)
            capture.WithConfiguration(configuration);
        var before = new
        {
            Services = services.Count,
            Nodes = capture.MeshNodes.Count,
            Hub = capture.HubConfiguration.Count,
            NodeHub = capture.DefaultNodeHubConfiguration.Count,
            MeshTypes = capture.MeshTypeRegistrations.Count,
            Mesh = capture.MeshConfiguration.Count,
            Excluded = capture.AutocompleteExcludedTypes.ToImmutableHashSet(StringComparer.Ordinal),
            Gates = capture.NodeTypeAccessConfig.BuildGates().Count,
            Routing = capture.QueryRoutingRules.Count,
            Streamed = capture.StreamRoutedAddressTypes.Count,
            ClientHosted = capture.ClientHostedAddressTypes.Count,
        };

        // The capture's module registry is disposed on EVERY path (#6128 review): a hook that installs
        // modules and then throws would otherwise leave those collectible contexts to unload only at
        // GC, unobserved by CollectibleContextUnloads and outside the retire path.
        MeshBuilder result;
        bool installedModules;
        try
        {
            result = hooks.Aggregate(capture, (builder, hook) => hook(builder));
            installedModules = capture.ModuleContexts.Generations.Count > 0;
        }
        finally
        {
            capture.ModuleContexts.Dispose();
        }

        var blockers = ImmutableList.CreateBuilder<string>();
        if (!ReferenceEquals(result, capture))
            blockers.Add("the builder hook returned a different builder than it was given");
        if (capture.MeshConfiguration.Count != before.Mesh)
            blockers.Add("it configures the mesh (ConfigureMesh)");
        if (capture.NodeTypeAccessConfig.BuildGates().Count != before.Gates)
            blockers.Add("it declares node-type access gates (ConfigureNodeTypeAccess)");
        if (capture.QueryRoutingRules.Count != before.Routing)
            blockers.Add("it adds query routing rules (AddQueryRoutingRule)");
        if (capture.StreamRoutedAddressTypes.Count != before.Streamed)
            blockers.Add("it adds stream-routed address types (AddStreamRoutedAddressType)");
        if (capture.ClientHostedAddressTypes.Count != before.ClientHosted)
            blockers.Add("it adds client-hosted address types (AddClientHostedAddressType)");
        if (installedModules)
            blockers.Add("it installs modules itself (InstallAssemblies)");

        return new BuilderHookCapture(
            [.. capture.MeshNodes.Skip(before.Nodes)],
            [.. services.Skip(before.Services)],
            [.. capture.HubConfiguration.Skip(before.Hub)],
            [.. capture.DefaultNodeHubConfiguration.Skip(before.NodeHub)],
            [.. capture.MeshTypeRegistrations.Skip(before.MeshTypes)],
            capture.AutocompleteExcludedTypes.Except(before.Excluded).ToImmutableHashSet(StringComparer.Ordinal),
            blockers.ToImmutable());
    }
}
