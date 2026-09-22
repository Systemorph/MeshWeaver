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
/// One enumeration leg's answer: the instances of <paramref name="Type"/> as the index served them,
/// or — when <paramref name="Failure"/> is set — the reason the leg could not be read at all.
///
/// <para>🚨 <b>These are two different answers and this type exists to keep them apart.</b> A
/// timeout on the index used to be caught into an EMPTY instance list, so "this type has no live
/// instances" and "nobody could find out" reached the cascade as the same value — and the recycle
/// then reported success over hubs that went on serving the assembly they were born with. A
/// partial failure that reads like a clean pass is worse than a loud one: it is the one outcome
/// nobody re-checks.</para>
/// </summary>
/// <param name="Type">The NodeType whose instances were asked for.</param>
/// <param name="Instances">What the index served — empty when <paramref name="Failure"/> is set.</param>
/// <param name="Failure">Why the leg could not be enumerated, or <c>null</c> when it was.</param>
public sealed record EnumerationLeg(string Type, ImmutableList<string> Instances, string? Failure = null);

/// <summary>
/// What a cascade derived — and, separately, what it could NOT derive.
/// </summary>
/// <param name="Addresses">The addresses to fan out to, from the legs that ANSWERED. Recycling
/// these is still worth doing when another leg failed: every address here is genuinely stale.</param>
/// <param name="Incomplete">One sentence per leg that FAILED, naming it and why. 🚨 An empty list
/// is the ONLY value that means "<see cref="Addresses"/> is the whole network"; a non-empty one
/// means an unknown number of live hubs were never reached and must be recycled by hand.</param>
public sealed record DependencyNetworkResult(
    ImmutableList<string> Addresses,
    ImmutableList<string> Incomplete)
{
    /// <summary>True only when every enumeration leg answered — see <see cref="Incomplete"/>.</summary>
    public bool IsComplete => Incomplete.IsEmpty;
}

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
///
/// <para>🚨 <b>…and the READ comes from a survivor too, which is the half that was missed (#5099).</b>
/// The seam runs on the recycle's turn, strictly before <c>Dispose()</c> — so "derive it while the
/// hub is still whole" holds for the synchronous prologue and for NOTHING after it. Each enumeration
/// leg is a cross-hub query; by the time the second one subscribes the definition hub has finished
/// disposing and <c>HostedHubsCollection.CloseScopeWhenDisposed</c> has closed its DI lifetime
/// scope, so every service the leg resolves from that hub throws
/// <see cref="ObjectDisposedException"/>. Measured in production: <c>Hosting/TriageItem</c> recycled
/// <b>0</b> addresses with one leg reported INCOMPLETE, and its live instance hubs kept the assembly
/// they were born with — the exact stale-state problem this cascade exists to remove, wearing the
/// cascade's own success. <see cref="DependencyNetwork"/> therefore enumerates through the mesh's
/// read-issuing hub, which the definition hub's teardown cannot reach.</para>
///
/// <para>🚨 <b>And a leg that could not be read is never silence.</b> Every enumeration answers an
/// <see cref="EnumerationLeg"/>, and a failed one is carried to the end as
/// <see cref="DependencyNetworkResult.Incomplete"/> rather than collapsing into an empty list. The
/// cascade still fans out to what it DID find — those activations really are stale — but it says, at
/// <c>Error</c>, that the recycle did not reach everything and names which leg it lost.</para>
/// </summary>
public static class NodeTypeRecycleCascade
{
    /// <summary>How long the type enumeration and each instance enumeration may take before the cascade gives up on that leg and reports it.</summary>
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
        var reverse = ImmutableDictionary.CreateBuilder<string, ImmutableList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, deps) in dependencies)
            foreach (var dependency in deps)
                reverse[dependency] = reverse.TryGetValue(dependency, out var list)
                    ? list.Add(type)
                    : ImmutableList.Create(type);

        var visited = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        visited.Add(nodeTypePath);
        var pending = ImmutableQueue.Create(nodeTypePath);
        while (!pending.IsEmpty)
        {
            pending = pending.Dequeue(out var current);
            if (!reverse.TryGetValue(current, out var next))
                continue;
            foreach (var type in next)
                if (visited.Add(type))
                    pending = pending.Enqueue(type);
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
        var seen = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        seen.Add(nodeTypePath);
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
    /// Folds the enumeration legs into the network to fan out to AND the list of legs that could not
    /// be read. Pure over its inputs — this is where "an error is not an empty answer" is decided,
    /// so it is decided somewhere a test can reach without a mesh.
    ///
    /// <para>A failed leg contributes NO addresses (there are none to contribute) and one sentence
    /// to <see cref="DependencyNetworkResult.Incomplete"/>. A leg that answered an EMPTY list
    /// contributes nothing to either: a type with no live instances is a complete answer.</para>
    /// </summary>
    /// <param name="nodeTypePath">The type being recycled.</param>
    /// <param name="dependents">From <see cref="DependentsOf"/> — empty when <paramref name="dependentsFailure"/> is set.</param>
    /// <param name="dependentsFailure">Why the NodeType enumeration failed, or <c>null</c> when it answered.</param>
    /// <param name="legs">One per type asked for — the type itself and each dependent.</param>
    public static DependencyNetworkResult Compose(
        string nodeTypePath,
        IReadOnlyList<string> dependents,
        string? dependentsFailure,
        IReadOnlyList<EnumerationLeg> legs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeTypePath);
        ArgumentNullException.ThrowIfNull(dependents);
        ArgumentNullException.ThrowIfNull(legs);

        var answered = legs
            .Where(l => l.Failure is null)
            .ToImmutableDictionary(
                l => l.Type,
                l => (IReadOnlyList<string>)l.Instances,
                StringComparer.OrdinalIgnoreCase);

        var incomplete = ImmutableList.CreateBuilder<string>();
        if (dependentsFailure is not null)
            incomplete.Add(
                $"the NodeType definitions of the mesh could not be enumerated ({dependentsFailure}), so the "
                + $"DEPENDENTS of '{nodeTypePath}' are unknown — neither they nor their instances are in this network");
        foreach (var leg in legs)
            if (leg.Failure is not null)
                incomplete.Add(
                    $"the instances of '{leg.Type}' could not be enumerated ({leg.Failure}), so its live "
                    + "instance hubs are NOT recycled");

        return new DependencyNetworkResult(
            NetworkAddresses(nodeTypePath, dependents, answered),
            incomplete.ToImmutable());
    }

    /// <summary>
    /// Enumerates the dependency network of <paramref name="nodeTypePath"/> from the index, as system:
    /// every NodeType definition (for the dependents), then the instances of the type and of each
    /// dependent. Emits ONE <see cref="DependencyNetworkResult"/> and completes.
    ///
    /// <para>🚨 A leg that times out or errors is RECORDED, never folded into an empty answer — see
    /// <see cref="Compose"/>. The cascade must be able to tell "no live instances" from "nobody could
    /// find out", because only the second one leaves stale hubs behind.</para>
    /// </summary>
    /// <param name="hub">Any hub of the mesh — in production the definition hub. Nothing is read
    /// THROUGH it; it only names the mesh whose read-issuing hub enumerates.</param>
    /// <param name="nodeTypePath">The type being recycled.</param>
    public static IObservable<DependencyNetworkResult> DependencyNetwork(IMessageHub hub, string nodeTypePath)
    {
        ArgumentNullException.ThrowIfNull(hub);
        // 🚨 A SURVIVOR enumerates — see the remarks on this class for why, and #5099 for what it
        // cost. `hub` is the hub being torn down: HandleDispose runs the cascade seam and then
        // Dispose()es, and HostedHubsCollection.CloseScopeWhenDisposed closes that hub's DI
        // lifetime scope the moment its DisposalCompleted fires. Only this method's SYNCHRONOUS
        // prologue runs while the hub is whole — every leg below is a cross-hub query, so the legs
        // subscribe AFTER the scope is gone, and each service they reach for then
        // (AccessService via MeshService.StampViewer, the IoPoolRegistry behind MeshQuery's
        // subscribe pool, JsonSerializerOptions) throws ObjectDisposedException out of
        // AutofacServiceProvider.GetService. The leg is reported INCOMPLETE, the cascade recycles
        // nothing, and the instance hubs it exists to reach go on serving the assembly they were
        // born with — silently, which is the one outcome nobody re-checks.
        //
        // The mesh's READ-issuing hub is the seam for a bounded read (MeshExtensions.ReadIssuingHub):
        // it is hosted by the mesh hub, so the definition hub's teardown cannot touch it; it is off
        // the router, so the router is never an end of a delivery; and it is not the node-CRUD
        // execution hub, so these reads are not queued behind every write in flight (#2901).
        var reader = hub.GetMeshHub().ReadIssuingHub();
        var logger = reader.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(NodeTypeRecycleCascade));
        var meshService = reader.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = reader.ServiceProvider.GetService<AccessService>();

        IObservable<EnumerationLeg> Instances(string type) =>
            accessService.RunAsSystem(() => meshService
                    .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.Declare($"nodeType:{type}"))
                        with { Limit = MeshQueryRequest.NoLimit })
                    .Take(1)
                    .Timeout(EnumerationBudget))
                .Select(change => new EnumerationLeg(type, change.Items
                    .Where(n => !string.IsNullOrEmpty(n.Path) && n.State == MeshNodeState.Active)
                    .Select(n => n.Path!)
                    .ToImmutableList()))
                .Catch<EnumerationLeg, Exception>(ex =>
                {
                    logger?.LogError(ex,
                        "[RecycleCascade] Enumerating the instances of {Type} failed — its live instance "
                        + "hubs keep their activations until recycled by hand. This leg is reported as "
                        + "INCOMPLETE rather than as zero instances", type);
                    return Observable.Return(new EnumerationLeg(type, ImmutableList<string>.Empty, Describe(ex)));
                });

        return accessService.RunAsSystem(() => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(MeshNode.NodeTypePath)))
                .Take(1)
                .Timeout(EnumerationBudget))
            .Select(change => change.Items
                .Where(n => !string.IsNullOrEmpty(n.Path) && n.State == MeshNodeState.Active)
                .GroupBy(n => n.Path!, StringComparer.OrdinalIgnoreCase)
                .ToImmutableDictionary(
                    g => g.Key,
                    g => g.First().ContentAs<NodeTypeDefinition>(reader.JsonSerializerOptions, logger),
                    StringComparer.OrdinalIgnoreCase))
            .Select(types => new DependentsLeg(DependentsOf(types, nodeTypePath), null))
            .Catch<DependentsLeg, Exception>(ex =>
            {
                logger?.LogError(ex,
                    "[RecycleCascade] Enumerating the NodeTypes failed — the dependents of {Type} are "
                    + "not recycled; its own instances still are. This leg is reported as INCOMPLETE "
                    + "rather than as 'no dependents'", nodeTypePath);
                return Observable.Return(new DependentsLeg(ImmutableList<string>.Empty, Describe(ex)));
            })
            .SelectMany(head => head.Dependents.Prepend(nodeTypePath)
                .Select(Instances)
                .Concat()
                .ToList()
                .Select(legs => Compose(nodeTypePath, head.Dependents, head.Failure, legs.ToImmutableList())));
    }

    /// <summary>The NodeType enumeration's answer: the dependents, or why they could not be read.</summary>
    private sealed record DependentsLeg(ImmutableList<string> Dependents, string? Failure);

    /// <summary>
    /// What an incomplete leg is reported AS. The exception type is kept: a
    /// <c>TimeoutException</c> at the enumeration budget and a refusal from the store need
    /// different remedies, and a bare message cannot tell a reader which one happened.
    /// </summary>
    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

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
                result =>
                {
                    // Fan out to what WAS derived first: those activations are stale whatever
                    // happened on another leg, and a partial recycle beats none.
                    foreach (var path in result.Addresses)
                        issuing.Post(
                            new DisposeRequest { Reason = reason, CascadedFrom = nodeTypePath },
                            o => o.WithTarget(new Address(path)));

                    if (result.IsComplete)
                    {
                        logger?.LogInformation(
                            "[RecycleCascade] {Type}: {Count} address(es) in its dependency network — each "
                            + "live activation among them is recycled; the rest are no-ops at the router "
                            + "and instantiate nothing", nodeTypePath, result.Addresses.Count);
                        return;
                    }

                    // 🚨 Error, not Information with a smaller number. An incomplete cascade reads
                    // EXACTLY like a complete one from the outside — the operator's recycle returns,
                    // the definition hub goes down, and some instance hubs quietly go on serving the
                    // assembly they were born with. The only thing that distinguishes the two is
                    // this line, so it names every leg that was lost.
                    logger?.LogError(
                        "[RecycleCascade] {Type}: the recycle is INCOMPLETE. {Count} address(es) were "
                        + "recycled, but {FailedCount} enumeration leg(s) could not be read, so an "
                        + "unknown number of live hubs keep serving the assembly they were born with "
                        + "and must be recycled by hand: {Legs}",
                        nodeTypePath, result.Addresses.Count, result.Incomplete.Count,
                        string.Join(" | ", result.Incomplete));
                },
                ex => logger?.LogError(ex,
                    "[RecycleCascade] {Type}: deriving the dependency network faulted — NOTHING in its "
                    + "dependency network was recycled, and its live activations keep serving until each "
                    + "is recycled by hand", nodeTypePath));
    }
}
