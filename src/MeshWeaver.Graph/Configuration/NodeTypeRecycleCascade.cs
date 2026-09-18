using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The one real <see cref="RecycleCascade"/>: a routed <see cref="DisposeRequest"/> on a NodeType
/// DEFINITION's hub tears down the definition's whole dependency network, so an operator recycles
/// the main bit and nothing else.
///
/// <para><b>The network.</b> Every NodeType that depends on this one — the types whose sources
/// reach into this type's tree (<c>shared=@Type/Source/…</c>), transitively, as
/// <see cref="NodeTypeDependencyGraph"/> already computes them for the compile order — plus EVERY
/// instance of this type and of each dependent. A dependent's assembly embeds this type's shared
/// sources and every instance hub binds its type's assembly ONCE at activation
/// (<c>Doc/Architecture/HubDisposalModel</c>); after a rebuild each of those activations is stale
/// until it is torn down, and until this cascade existed each was recycled by hand — or not, which is
/// how "the grain keeps serving old state" turned into image pins.</para>
///
/// <para><b>Only what was instantiated.</b> The set is computed from the INDEX (every instance the
/// mesh knows), not from what is live: which activations exist is a fact of the routing layer, and
/// that is where it is answered — a cascaded request reaching an address with no live hub is a no-op
/// (<c>MonolithRoutingService.RouteImpl</c>, <c>MessageHubGrain.DeliverMessage</c>) and instantiates
/// nothing. So a type with ten thousand instances and three open pages recycles three hubs.</para>
///
/// <para><b>Once, from a survivor.</b> The set is derived once, at the main node, and every fanned-out
/// request carries <see cref="DisposeRequest.CascadedFrom"/>, which <c>MessageHub.HandleDispose</c>
/// never cascades again — a cycle among NodeTypes cannot turn one recycle into a storm. The requests
/// are posted from the mesh's node-operation issuing hub, never from the definition hub itself: a
/// dying hub cannot deliver its own last frame.</para>
/// </summary>
public static class NodeTypeRecycleCascade
{
    /// <summary>How long the type enumeration and each instance enumeration may take before the cascade gives up on that leg and logs it.</summary>
    public static readonly TimeSpan EnumerationBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Installs the cascade on a NodeType definition's own hub — the <c>WithInitialization</c>
    /// delegate <see cref="NodeTypeNodeType"/> registers. A static method group, so the delegate-identity
    /// idempotency collapses repeat registrations from composed configurators.
    /// </summary>
    /// <param name="definitionHub">The definition node's own hub.</param>
    /// <returns>An observable that completes once the seam is set.</returns>
    public static IObservable<Unit> InstallRecycleCascade(IMessageHub definitionHub) =>
        Observable.Defer(() =>
        {
            definitionHub.Set(new RecycleCascade(request => Cascade(definitionHub, request)));
            return Observable.Return(Unit.Default);
        });

    /// <summary>
    /// The NodeTypes that depend on <paramref name="nodeTypePath"/>, transitively — the reverse of
    /// <see cref="NodeTypeDependencyGraph.Build"/>'s edges — excluding the type itself, in a
    /// deterministic (ordinal, case-insensitive) order. Pure.
    /// </summary>
    /// <param name="types">Every NodeType definition of the mesh, by path.</param>
    /// <param name="nodeTypePath">The type whose dependents are asked for.</param>
    public static ImmutableList<string> DependentsOf(
        IReadOnlyDictionary<string, NodeTypeDefinition?> types, string nodeTypePath)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeTypePath);
        var dependencies = NodeTypeDependencyGraph.Build(types);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, deps) in dependencies)
            foreach (var dependency in deps)
            {
                if (!dependents.TryGetValue(dependency, out var list))
                    dependents[dependency] = list = [];
                list.Add(type);
            }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { nodeTypePath };
        var queue = new Queue<string>();
        queue.Enqueue(nodeTypePath);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!dependents.TryGetValue(current, out var next))
                continue;
            foreach (var type in next)
                if (visited.Add(type))
                    queue.Enqueue(type);
        }
        visited.Remove(nodeTypePath);
        return visited.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToImmutableList();
    }

    /// <summary>
    /// The addresses a recycle of <paramref name="nodeTypePath"/> fans out to: each dependent type's
    /// own hub, then every instance of the type and of each dependent. Pure over the two inputs.
    /// </summary>
    /// <param name="nodeTypePath">The type being recycled (its own hub is the request that started this, so it is NOT in the result).</param>
    /// <param name="dependents">From <see cref="DependentsOf"/>.</param>
    /// <param name="instancesByType">Instance paths per type, for the type and every dependent.</param>
    public static ImmutableList<string> NetworkAddresses(
        string nodeTypePath,
        IReadOnlyList<string> dependents,
        IReadOnlyDictionary<string, IReadOnlyList<string>> instancesByType)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { nodeTypePath };
        var result = ImmutableList.CreateBuilder<string>();
        foreach (var type in dependents)
            if (seen.Add(type))
                result.Add(type);
        foreach (var type in dependents.Prepend(nodeTypePath))
            if (instancesByType.TryGetValue(type, out var instances))
                foreach (var instance in instances)
                    if (!string.IsNullOrEmpty(instance) && seen.Add(instance))
                        result.Add(instance);
        return result.ToImmutable();
    }

    /// <summary>
    /// Enumerates the dependency network of <paramref name="nodeTypePath"/> from the index, as system:
    /// every NodeType definition (for the dependents), then the instances of the type and of each
    /// dependent. Emits ONE list of addresses and completes; a failed leg is logged and contributes
    /// nothing rather than faulting the cascade.
    /// </summary>
    public static IObservable<IReadOnlyList<string>> DependencyNetwork(IMessageHub hub, string nodeTypePath)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(NodeTypeRecycleCascade));
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        IObservable<IReadOnlyList<string>> Instances(string type) =>
            accessService.RunAsSystem(() => meshService
                    .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.Declare($"nodeType:{type}"))
                        with { Limit = MeshQueryRequest.NoLimit })
                    .Take(1)
                    .Timeout(EnumerationBudget))
                .Select(change => (IReadOnlyList<string>)change.Items
                    .Where(n => !string.IsNullOrEmpty(n.Path) && n.State == MeshNodeState.Active)
                    .Select(n => n.Path!)
                    .ToImmutableList())
                .Catch<IReadOnlyList<string>, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[RecycleCascade] Enumerating the instances of {Type} failed — its live instance "
                        + "hubs keep their activations until recycled by hand", type);
                    return Observable.Return<IReadOnlyList<string>>(ImmutableList<string>.Empty);
                });

        return accessService.RunAsSystem(() => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(MeshNode.NodeTypePath)))
                .Take(1)
                .Timeout(EnumerationBudget))
            .Select(change => change.Items
                .Where(n => !string.IsNullOrEmpty(n.Path) && n.State == MeshNodeState.Active)
                .GroupBy(n => n.Path!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions, logger),
                    StringComparer.OrdinalIgnoreCase))
            .Select(types => DependentsOf(types, nodeTypePath))
            .Catch<ImmutableList<string>, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[RecycleCascade] Enumerating the NodeTypes failed — the dependents of {Type} are "
                    + "not recycled; its own instances still are", nodeTypePath);
                return Observable.Return(ImmutableList<string>.Empty);
            })
            .SelectMany(dependents => dependents.Prepend(nodeTypePath)
                .Select(type => Instances(type).Select(instances => (Type: type, Instances: instances)))
                .Concat()
                .ToList()
                .Select(legs => NetworkAddresses(
                    nodeTypePath,
                    dependents,
                    legs.ToDictionary(l => l.Type, l => l.Instances, StringComparer.OrdinalIgnoreCase))))
            .Select(x => (IReadOnlyList<string>)x);
    }

    /// <summary>
    /// What the seam runs on the recycle's turn: derive the network off-turn and post one cascaded
    /// <see cref="DisposeRequest"/> per address from the mesh's node-operation issuing hub — a
    /// survivor — with the reason the main request carried.
    /// </summary>
    internal static void Cascade(IMessageHub definitionHub, DisposeRequest request)
    {
        var nodeTypePath = definitionHub.Address.ToString();
        var logger = definitionHub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(NodeTypeRecycleCascade));
        // 🚨 The SURVIVOR issues the requests. The definition hub is on its way out; the mesh hub's
        // node-operation hub outlives it and is the one seam every documented recycle posts from
        // (HubRecycleExtensions, MeshOperations.Recycle).
        var mesh = definitionHub.GetMeshHub();
        var issuing = mesh.NodeOperationIssuingHub();
        var reason = $"RecycleCascade from '{nodeTypePath}': {request.Reason ?? DisposeRequest.ReasonNotStated}";

        DependencyNetwork(definitionHub, nodeTypePath)
            .Subscribe(
                network =>
                {
                    logger?.LogInformation(
                        "[RecycleCascade] {Type}: {Count} address(es) in its dependency network — each "
                        + "live activation among them is recycled; the rest are no-ops at the router "
                        + "and instantiate nothing", nodeTypePath, network.Count);
                    foreach (var path in network)
                        issuing.Post(
                            new DisposeRequest { Reason = reason, CascadedFrom = nodeTypePath },
                            o => o.WithTarget(new Address(path)));
                },
                ex => logger?.LogWarning(ex,
                    "[RecycleCascade] {Type}: deriving the dependency network faulted — its live "
                    + "activations keep serving until each is recycled by hand", nodeTypePath));
    }
}
