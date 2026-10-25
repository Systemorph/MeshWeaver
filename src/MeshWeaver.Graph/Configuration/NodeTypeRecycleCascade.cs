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
/// The NodeTypes a recycle of one type reaches: its dependents — or why they could not be read.
/// </summary>
/// <param name="Dependents">From <see cref="NodeTypeRecycleCascade.DependentsOf"/> — empty when
/// <paramref name="Failure"/> is set.</param>
/// <param name="Failure">Why the NodeType definitions could not be enumerated, or <c>null</c> when
/// they were. 🚨 An empty <paramref name="Dependents"/> list means "a leaf type" ONLY when this is
/// <c>null</c>.</param>
public sealed record NetworkTypes(ImmutableList<string> Dependents, string? Failure = null)
{
    /// <summary>True when the definitions were read, so <see cref="Dependents"/> is the whole set.</summary>
    public bool IsComplete => Failure is null;
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
/// <para>🚨 <b>Only what is LIVE, found where it lives — one <see cref="ActivationRecycle"/> request
/// (#6520).</b> The cascade asks NO index for instances. It used to: one <c>nodeType:{type}</c>
/// query across every partition per type of the network, then one <see cref="DisposeRequest"/> per
/// stored instance, most of them to cold addresses. On a portal pod one wave of that issued ~200
/// concurrent cross-partition reads and logged 289 "recycle is INCOMPLETE" lines in 52 s, stalling
/// the PostgreSQL query provider for every other reader. Which activations exist is a fact of each
/// PROCESS, so the cascade now writes ONE <see cref="ActivationRecycleRequest"/> naming the type and
/// its dependents; every process hears it on its invalidation feed and disposes only the hubs IT
/// hosts whose bound NodeType is in the request — paced, off the router, with an audit row per
/// process. A type with ten thousand stored instances and three open pages recycles three hubs, and
/// a cold address is never touched, let alone activated.</para>
///
/// <para><b>Once, from a survivor.</b> The network is derived once, at the main node. The dependent
/// definitions' own hubs are disposed with <see cref="DisposeRequest.CascadedFrom"/> set, and the
/// instance disposals <see cref="ActivationRecycle"/> posts carry it too, so
/// <c>MessageHub.HandleDispose</c> never cascades any of them again — a cycle among NodeTypes cannot
/// turn one recycle into a storm. Everything is issued from the mesh hub and its node-operation
/// issuing hub, never from the definition hub itself: a dying hub cannot deliver its own last
/// frame.</para>
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
/// <para>🚨 <b>And a read that failed is never silence.</b> The one read the cascade still makes — the
/// NodeType definitions, for the dependents — answers <see cref="DependentTypes"/> with its
/// <see cref="NetworkTypes.Failure"/> kept. A failed read still recycles the type's own activations
/// (that half needs no read), and the cascade says, at <c>Error</c>, that the dependents were NOT
/// reached. <see cref="DependencyNetwork"/> — the ADDRESS enumeration a control-lane plan binds its
/// blast radius to — keeps the same rule per leg (<see cref="EnumerationLeg"/>); the cascade itself no
/// longer runs it.</para>
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
        // the hub's service provider's GetService. The leg is reported INCOMPLETE, the cascade recycles
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

        // 🚨 A wave of recycles shares each read while it is in flight (#6520): every cascade of
        // the wave asks the SAME mesh-wide questions, and asking them once per leg is the load that
        // stalled the provider. The budget stays per caller, outside the shared read.
        var reads = reader.ServiceProvider.GetRequiredService<RecycleCascadeReads>();

        IObservable<EnumerationLeg> Instances(string type) =>
            reads.Instances(type, reader, () => accessService.RunAsSystem(() => meshService
                        .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.Declare($"nodeType:{type}"))
                            with { Limit = MeshQueryRequest.NoLimit }))
                    .Select(change => change.Items
                        .Where(n => !string.IsNullOrEmpty(n.Path) && n.State == MeshNodeState.Active)
                        .Select(n => n.Path!)
                        .ToImmutableList()))
                .Timeout(EnumerationBudget)
                .Select(paths => new EnumerationLeg(type, paths))
                .Catch<EnumerationLeg, Exception>(ex =>
                {
                    logger?.LogError(ex,
                        "[RecycleCascade] Enumerating the instances of {Type} failed — its live instance "
                        + "hubs keep their activations until recycled by hand. This leg is reported as "
                        + "INCOMPLETE rather than as zero instances", type);
                    return Observable.Return(new EnumerationLeg(type, ImmutableList<string>.Empty, Describe(ex)));
                });

        return DependentTypes(hub, nodeTypePath)
            .SelectMany(head => head.Dependents.Prepend(nodeTypePath)
                .Select(Instances)
                .Concat()
                .ToList()
                .Select(legs => Compose(nodeTypePath, head.Dependents, head.Failure, legs.ToImmutableList())));
    }

    /// <summary>
    /// The dependents of <paramref name="nodeTypePath"/> — the NodeTypes sharing its sources,
    /// transitively — from ONE read of the mesh's NodeType definitions, as system, through the
    /// mesh's read-issuing hub (a survivor, #5099), joining a read already in flight
    /// (<see cref="RecycleCascadeReads"/>). Emits ONE <see cref="NetworkTypes"/> and completes; never
    /// errors — a failed read is answered as <see cref="NetworkTypes.Failure"/>, never as "no
    /// dependents". Asks the index for NO instances.
    /// </summary>
    /// <param name="hub">Any hub of the mesh — in production the definition hub being recycled.
    /// Nothing is read THROUGH it; it only names the mesh.</param>
    /// <param name="nodeTypePath">The type being recycled.</param>
    public static IObservable<NetworkTypes> DependentTypes(IMessageHub hub, string nodeTypePath)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeTypePath);
        // A SURVIVOR reads — see DependencyNetwork and the remarks on this class (#5099).
        var reader = hub.GetMeshHub().ReadIssuingHub();
        var logger = reader.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(NodeTypeRecycleCascade));
        var meshService = reader.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = reader.ServiceProvider.GetService<AccessService>();
        var reads = reader.ServiceProvider.GetRequiredService<RecycleCascadeReads>();

        return reads.Definitions(reader, () => accessService.RunAsSystem(() => meshService
                    .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(MeshNode.NodeTypePath))))
                .Select(change => change.Items
                    .Where(n => !string.IsNullOrEmpty(n.Path) && n.State == MeshNodeState.Active)
                    .GroupBy(n => n.Path!, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableDictionary(
                        g => g.Key,
                        g => g.First().ContentAs<NodeTypeDefinition>(reader.JsonSerializerOptions, logger),
                        StringComparer.OrdinalIgnoreCase)))
            .Timeout(EnumerationBudget)
            .Select(types => new NetworkTypes(DependentsOf(types, nodeTypePath)))
            .Catch<NetworkTypes, Exception>(ex =>
            {
                logger?.LogError(ex,
                    "[RecycleCascade] Enumerating the NodeTypes failed — the dependents of {Type} are "
                    + "not recycled; its own live activations still are. This is reported as INCOMPLETE "
                    + "rather than as 'no dependents'", nodeTypePath);
                return Observable.Return(new NetworkTypes(ImmutableList<string>.Empty, Describe(ex)));
            });
    }

    /// <summary>
    /// The ONE <see cref="ActivationRecycleRequest"/> a recycle of <paramref name="nodeTypePath"/>
    /// writes: the type and each dependent, once each, in order, minus the types
    /// <see cref="ActivationRecycle.RefusedNodeTypes"/> refuses (a recycle of the definition type
    /// itself would name <c>NodeType</c>, i.e. every definition hub of the mesh). Pure.
    /// </summary>
    /// <param name="nodeTypePath">The type being recycled.</param>
    /// <param name="dependents">From <see cref="DependentTypes"/>.</param>
    /// <param name="reason">The reason the recycle that started this carried — passed through.</param>
    public static ActivationRecycleRequest RecycleRequest(
        string nodeTypePath, IReadOnlyList<string> dependents, string reason) =>
        new()
        {
            NodeTypes = dependents.Prepend(nodeTypePath)
                .Where(t => !string.IsNullOrWhiteSpace(t) && !ActivationRecycle.RefusedNodeTypes.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableList(),
            Reason = string.IsNullOrWhiteSpace(reason) ? DisposeRequest.ReasonNotStated : reason,
            RequestedBy = $"RecycleCascade from '{nodeTypePath}'",
        };

    /// <summary>
    /// What an incomplete leg is reported AS. The exception type is kept: a
    /// <c>TimeoutException</c> at the enumeration budget and a refusal from the store need
    /// different remedies, and a bare message cannot tell a reader which one happened.
    /// </summary>
    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// What the seam runs on the recycle's turn: read the dependents off-turn, dispose the dependent
    /// definitions' own hubs (each with <see cref="DisposeRequest.CascadedFrom"/>, so none cascades
    /// again), and write ONE <see cref="ActivationRecycleRequest"/> for the type and its dependents —
    /// every process then disposes the live activations IT hosts. Everything is issued from the mesh
    /// hub (a survivor), with the reason the main request carried. Asks the index for NO instances.
    /// </summary>
    internal static void Cascade(IMessageHub definitionHub, DisposeRequest request)
    {
        var nodeTypePath = definitionHub.Address.ToString();
        // 🚨 The SURVIVOR issues everything. The definition hub is on its way out; the mesh hub and
        // its node-operation hub outlive it, and the latter is the one seam every documented
        // recycle posts from (HubRecycleExtensions, MeshOperations.Recycle). Resolved HERE, on the
        // recycle's turn, while the definition hub is still whole (#5099).
        var mesh = definitionHub.GetMeshHub();
        var issuing = mesh.NodeOperationIssuingHub();
        var logger = mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(NodeTypeRecycleCascade));
        var origin = string.IsNullOrWhiteSpace(request.Reason) ? DisposeRequest.ReasonNotStated : request.Reason;
        var reason = $"RecycleCascade from '{nodeTypePath}': {origin}";

        DependentTypes(definitionHub, nodeTypePath)
            .SelectMany(network =>
            {
                // A dependent's DEFINITION hub is bound from `NodeType`, which an activation recycle
                // refuses to name (it would reach every definition hub of the mesh) — so the few
                // dependent definitions are disposed by address. A cold one is a no-op at the router
                // and instantiates nothing.
                foreach (var dependent in network.Dependents)
                    issuing.Post(
                        new DisposeRequest { Reason = reason, CascadedFrom = nodeTypePath },
                        o => o.WithTarget(new Address(dependent)));

                var recycle = RecycleRequest(nodeTypePath, network.Dependents, origin);
                return recycle.NodeTypes.IsEmpty
                    ? Observable.Return((Network: network, Request: recycle, Ticket: (ActivationRecycleTicket?)null))
                    : ActivationRecycle.Request(mesh, recycle)
                        .Select(ticket => (Network: network, Request: recycle, Ticket: (ActivationRecycleTicket?)ticket));
            })
            .Subscribe(
                outcome =>
                {
                    var (network, recycle, ticket) = outcome;
                    if (ticket is { Accepted: false })
                    {
                        // 🚨 Error: nothing below the definition was recycled, and from the outside
                        // this reads exactly like a recycle that worked.
                        logger?.LogError(
                            "[RecycleCascade] {Type}: the activation recycle for {Types} was REFUSED ({Refusal}) — "
                            + "no live activation of the type or its dependents was recycled; each keeps the "
                            + "build it was born with until recycled by hand",
                            nodeTypePath, string.Join(", ", recycle.NodeTypes), ticket.Refusal);
                        return;
                    }

                    if (network.IsComplete)
                    {
                        logger?.LogInformation(
                            "[RecycleCascade] {Type}: {Dependents} dependent NodeType(s); one activation recycle "
                            + "{Request} for {Types} — every process disposes the live activations it hosts, "
                            + "cold addresses are not touched",
                            nodeTypePath, network.Dependents.Count, ticket?.Path ?? "(none: no recyclable type)",
                            string.Join(", ", recycle.NodeTypes));
                        return;
                    }

                    // 🚨 Error, not Information with a smaller number: the type's own live
                    // activations were recycled, but its DEPENDENTS are unknown, and their live hubs
                    // keep serving the assembly they were born with. Only this line says so.
                    logger?.LogError(
                        "[RecycleCascade] {Type}: the recycle is INCOMPLETE. Its own live activations are "
                        + "recycled ({Request}), but the NodeType definitions could not be read ({Failure}), so "
                        + "its DEPENDENTS — and their live activations — are NOT recycled and must be recycled "
                        + "by hand",
                        nodeTypePath, ticket?.Path ?? "(none)", network.Failure);
                },
                ex => logger?.LogError(ex,
                    "[RecycleCascade] {Type}: the recycle faulted — NOTHING in its dependency network was "
                    + "recycled, and its live activations keep serving until each is recycled by hand",
                    nodeTypePath));
    }
}
