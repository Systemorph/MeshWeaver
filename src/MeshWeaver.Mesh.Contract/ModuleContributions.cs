using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// What ONE module generation contributes, materialised from its
/// <see cref="MeshNodeProviderAttribute"/>s — the unit a live swap re-applies.
/// </summary>
/// <param name="Nodes">Mesh nodes (node types, seeds).</param>
/// <param name="AddressTypes">Address types for the mesh hub's type registry.</param>
/// <param name="HubConfigurations">Configuration of the MESH hub.</param>
/// <param name="DefaultNodeHubConfigurations">Configuration of EVERY per-node hub.</param>
/// <param name="BuilderConfigurations">The full builder hook.</param>
/// <param name="MapsEndpoints">Whether the assembly carries an endpoint provider attribute
/// (<c>MeshEndpointProviderAttribute</c>, applied by the host at endpoint-mapping time).</param>
public sealed record ModuleContributions(
    IReadOnlyCollection<MeshNode> Nodes,
    IReadOnlyCollection<KeyValuePair<string, Type>> AddressTypes,
    IReadOnlyCollection<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations,
    IReadOnlyCollection<Func<MessageHubConfiguration, MessageHubConfiguration>> DefaultNodeHubConfigurations,
    IReadOnlyCollection<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations,
    bool MapsEndpoints)
{
    /// <summary>The reason the module DECLARES it needs a restart to update
    /// (<see cref="ModuleRestartRequiredAttribute"/>), or null — live, the default.</summary>
    public string? DeclaredRestartReason { get; init; }

    /// <summary>The simple name of the attribute type a host maps module endpoints from. Matched by
    /// NAME through the base-type chain, because it lives in <c>MeshWeaver.Hosting.AspNetCore</c>,
    /// which this assembly does not reference.</summary>
    public const string EndpointProviderAttributeName = "MeshEndpointProviderAttribute";

    /// <summary>Materialises every contribution of <paramref name="assembly"/>. Throws what a
    /// contribution getter throws — the caller treats that as the generation failing.</summary>
    public static ModuleContributions Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var attributes = assembly.GetCustomAttributes<MeshNodeProviderAttribute>().ToArray();
        return new ModuleContributions(
            attributes.SelectMany(a => a.Nodes).ToArray(),
            attributes.SelectMany(a => a.AddressTypes).ToArray(),
            attributes.SelectMany(a => a.HubConfigurations).ToArray(),
            attributes.SelectMany(a => a.DefaultNodeHubConfigurations).ToArray(),
            attributes.SelectMany(a => a.BuilderConfigurations).ToArray(),
            CarriesEndpointProvider(assembly))
        {
            DeclaredRestartReason = assembly.GetCustomAttribute<ModuleRestartRequiredAttribute>()?.Reason,
        };
    }

    private static bool CarriesEndpointProvider(Assembly assembly)
    {
        foreach (var data in assembly.GetCustomAttributesData())
            for (var type = data.AttributeType; type is not null; type = type.BaseType)
                if (string.Equals(type.Name, EndpointProviderAttributeName, StringComparison.Ordinal))
                    return true;
        return false;
    }

    /// <summary>
    /// Why these contributions cannot be swapped inside the running process — empty when they can
    /// (policy <c>module-live-update-default</c>). Each reason names the contribution, because the
    /// remedy is per contribution: move it to a re-appliable surface, or declare
    /// <c>restartRequired</c> with this reason. Pure.
    ///
    /// <para>What IS re-appliable: mesh nodes that register nothing in the root container — served
    /// from the module's CURRENT generation and re-bound when the hubs they configure are recycled —
    /// and every-per-node-hub configuration, read from the current generation when a hub is built
    /// (a swap then recycles every per-node hub of the process). What is not, as the platform
    /// stands: root services (the container is built once), the mesh hub's configuration and address
    /// types (the mesh hub lives as long as the process), the builder hook (arbitrary boot-time
    /// mutation), and HTTP endpoints (the endpoint map is built once).</para>
    /// </summary>
    public ImmutableList<string> LiveUpdateBlockers() =>
        DeclaredRestartReason is { } declared
            ? MeasuredLiveUpdateBlockers().Insert(0, $"declares [ModuleRestartRequired]: {declared}")
            : MeasuredLiveUpdateBlockers();

    /// <summary>The blockers the platform MEASURES from the contributions alone — what
    /// <see cref="ModuleLiveUpdateGuard"/> demands a declaration for. Pure.</summary>
    public ImmutableList<string> MeasuredLiveUpdateBlockers()
    {
        var blockers = ImmutableList.CreateBuilder<string>();
        foreach (var node in Nodes.Where(n => !n.GlobalServiceConfigurations.IsEmpty))
            blockers.Add($"registers root services through node '{node.Path}' (WithGlobalServiceRegistry) — the root container is built once");
        if (HubConfigurations.Count > 0)
            blockers.Add("configures the mesh hub (HubConfigurations) — the mesh hub lives as long as the process");
        if (AddressTypes.Count > 0)
            blockers.Add("registers address types on the mesh hub (AddressTypes)");
        if (BuilderConfigurations.Count > 0)
            blockers.Add("uses the builder hook (BuilderConfigurations) — arbitrary boot-time configuration");
        if (MapsEndpoints)
            blockers.Add("maps HTTP endpoints (MeshEndpointProviderAttribute) — the endpoint map is built once");
        return blockers.ToImmutable();
    }
}
