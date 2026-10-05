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

    /// <summary>The <see cref="ModuleBootCategory"/> the module declares, or null.</summary>
    public string? DeclaredRestartCategory { get; init; }

    /// <summary>The simple name of the attribute type a host maps module endpoints from. Matched by
    /// NAME through the base-type chain, because it lives in <c>MeshWeaver.Hosting.AspNetCore</c>,
    /// which this assembly does not reference.</summary>
    public const string EndpointProviderAttributeName = "MeshEndpointProviderAttribute";

    /// <summary>Materialises every contribution of <paramref name="assembly"/>. Throws what a
    /// contribution getter throws — the caller treats that as the generation failing.</summary>
    public static ModuleContributions Of(Assembly assembly) => Of(assembly, null);

    /// <summary>As <see cref="Of(Assembly)"/>, decomposing the module's builder hook against
    /// <paramref name="configuration"/> — what the real builder would expose to it.</summary>
    public static ModuleContributions Of(Assembly assembly, Microsoft.Extensions.Configuration.IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var attributes = assembly.GetCustomAttributes<MeshNodeProviderAttribute>().ToArray();
        var hooks = attributes.SelectMany(a => a.BuilderConfigurations).ToArray();
        BuilderHookCapture capture;
        try
        {
            capture = MeshBuilder.CaptureBuilderHooks(hooks, configuration);
        }
        catch (Exception exception)
        {
            capture = BuilderHookCapture.Empty with
            {
                Blockers = [$"its builder hook could not be decomposed ({exception.GetType().Name}: {exception.Message})"],
            };
        }
        return new ModuleContributions(
            attributes.SelectMany(a => a.Nodes).ToArray(),
            attributes.SelectMany(a => a.AddressTypes).ToArray(),
            attributes.SelectMany(a => a.HubConfigurations).ToArray(),
            attributes.SelectMany(a => a.DefaultNodeHubConfigurations).ToArray(),
            attributes.SelectMany(a => a.BuilderConfigurations).ToArray(),
            CarriesEndpointProvider(assembly))
        {
            DeclaredRestartReason = assembly.GetCustomAttribute<ModuleRestartRequiredAttribute>()?.Reason,
            DeclaredRestartCategory = assembly.GetCustomAttribute<ModuleRestartRequiredAttribute>()?.Category,
            ModuleContext = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly),
            ModuleName = assembly.GetName().Name ?? "",
            // A hook that could not be fully decomposed keeps ONLY its blockers: it is applied to the
            // real builder as before (and the module stays restart-required), so none of its parts
            // may ALSO flow through the re-appliable seams — they would be applied twice.
            BuilderHooks = capture.Blockers.IsEmpty ? capture : BuilderHookCapture.Empty with { Blockers = capture.Blockers },
        };
    }

    /// <summary>The builder hook, decomposed (<see cref="MeshBuilder.CaptureBuilderHooks"/>).</summary>
    public BuilderHookCapture BuilderHooks { get; init; } = BuilderHookCapture.Empty;

    /// <summary>Every node the module contributes — its attributes' and its builder hook's.</summary>
    public IReadOnlyList<MeshNode> AllNodes => [.. Nodes, .. BuilderHooks.Nodes];

    /// <summary>Every configuration of every per-node hub — its attributes' and its builder hook's.</summary>
    public IReadOnlyList<Func<MessageHubConfiguration, MessageHubConfiguration>> AllDefaultNodeHubConfigurations =>
        [.. DefaultNodeHubConfigurations, .. BuilderHooks.DefaultNodeHubConfigurations];

    /// <summary>Every configuration of the MESH hub — its attributes', its address types, its builder
    /// hook's. Re-appliable to the running mesh hub only when each one MUTATES the configuration it is
    /// given (its type registry) and returns it — see <see cref="MeasuredLiveUpdateBlockers"/>.</summary>
    public IReadOnlyList<Func<MessageHubConfiguration, MessageHubConfiguration>> AllMeshHubConfigurations =>
    [
        .. (AddressTypes.Count > 0
            ? new Func<MessageHubConfiguration, MessageHubConfiguration>[] { RegisterAddressTypes }
            : []),
        .. HubConfigurations,
        .. BuilderHooks.MeshHubConfigurations,
    ];

    private MessageHubConfiguration RegisterAddressTypes(MessageHubConfiguration config)
    {
        config.TypeRegistry.WithTypes(AddressTypes);
        return config;
    }

    /// <summary>The load context the contributions' assembly lives in — what "the module's own type"
    /// means when its root services are routed (<see cref="ModuleServices"/>).</summary>
    public System.Runtime.Loader.AssemblyLoadContext? ModuleContext { get; init; }

    /// <summary>The module's entry-assembly name.</summary>
    public string ModuleName { get; init; } = "";

    /// <summary>Every root-service delegate the module's nodes carry, in node order.</summary>
    public IReadOnlyList<Func<Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.DependencyInjection.IServiceCollection>> ServiceConfigurations =>
        [.. AllNodes.SelectMany(n => n.GlobalServiceConfigurations), .. BuilderHooks.Services];

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
    /// mutation).</para>
    /// </summary>
    public ImmutableList<string> LiveUpdateBlockers() =>
        DeclaredRestartReason is { } declared
            ? MeasuredLiveUpdateBlockers().Insert(0, $"declares [ModuleRestartRequired] ({DeclaredRestartCategory}): {declared}")
            : MeasuredLiveUpdateBlockers();

    /// <summary>The blockers the platform MEASURES from the contributions alone — what
    /// <see cref="ModuleLiveUpdateGuard"/> demands a declaration for. Pure.</summary>
    public ImmutableList<string> MeasuredLiveUpdateBlockers()
    {
        var blockers = ImmutableList.CreateBuilder<string>();
        // Root services are served from a scope of the module's own and re-bound on a swap
        // (ModuleServices) — unless what they register cannot be, which the probe names.
        if (ServiceConfigurations.Count > 0 && ModuleContext is { } context)
        {
            ModuleServices? probed = null;
            string? fault = null;
            try
            {
                probed = ModuleServices.Probe(ModuleName, context, ServiceConfigurations, []);
            }
            catch (Exception exception)
            {
                fault = $"{exception.GetType().Name}: {exception.Message}";
            }
            if (fault is not null)
                blockers.Add($"its root-service registration could not be measured ({fault})");
            foreach (var blocker in probed?.Blockers ?? [])
                blockers.Add($"root services: {blocker}");
        }
        // The mesh hub lives as long as the process; what a module contributes to it is re-applied to
        // the RUNNING mesh hub on a swap — which is sound only for a delegate that mutates the
        // configuration it is handed (its type registry) and returns that same object. A dry run on a
        // throwaway configuration measures it.
        foreach (var configure in HubConfigurations.Concat(BuilderHooks.MeshHubConfigurations))
            if (MeshHubBlocker(configure) is { } meshHub)
            {
                blockers.Add(meshHub);
                break;
            }
        blockers.AddRange(BuilderHooks.Blockers.Select(b => $"builder hook: {b}"));
        if (MapsEndpoints)
            blockers.Add("maps HTTP endpoints (MeshEndpointProviderAttribute) — the endpoint map is built once");
        return blockers.ToImmutable();
    }

    /// <summary>Why <paramref name="configure"/> cannot be re-applied to the running mesh hub, or null:
    /// measured on a throwaway configuration — it must return the SAME configuration it was given.</summary>
    private static string? MeshHubBlocker(Func<MessageHubConfiguration, MessageHubConfiguration> configure)
    {
        try
        {
            var probe = new MessageHubConfiguration(null, new Address("module-probe", Guid.NewGuid().ToString("N")));
            return ReferenceEquals(configure(probe), probe)
                ? null
                : "configures the mesh hub beyond its type registry (it returns a new configuration) — the mesh hub is built once per process";
        }
        catch (Exception exception)
        {
            return $"configures the mesh hub, and the configuration could not be measured ({exception.GetType().Name}: {exception.Message})";
        }
    }
}
