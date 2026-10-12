using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>One recycle WAVE reads the index once per question, not once per leg</b> (#6520).
///
/// <para>Every NodeType recycle derives its dependency network from two index reads: the mesh's
/// NodeType definitions (<c>nodeType:NodeType partitions:all</c>) and, per type in the network, its
/// instances. A wave — a roll, a build, a recycle job touching many types — runs one cascade per
/// type at once, and each asked the SAME mesh-wide enumeration for itself. Measured on a portal pod:
/// one wave issued that enumeration for every leg inside one second, the Postgres provider emitted no
/// Initial within the fan-in's bound for any of them, and every leg was reported INCOMPLETE — the
/// cascade's own load, not a shared stall: a read of the same window for stalls on any OTHER query
/// found none, against a positive control that found the enumeration's.</para>
///
/// <para><b>What this does.</b> A read that is already IN FLIGHT is joined, not repeated: concurrent
/// legs asking the same question share one query and one answer. Once that read settles — value,
/// fault or completion — it is released, so the next recycle asks the index afresh; nothing is cached
/// past the wave, and a fault is never replayed to a later caller (the <see cref="PromiseCache{TKey,TValue}"/>
/// contract). Each caller still handles the answer — or the fault — itself, so an unreadable leg is
/// still reported INCOMPLETE by every cascade that needed it.</para>
///
/// <para>A mesh-scoped instance singleton, never static — its lifetime is the mesh's.</para>
///
/// <para>🚨 <b>One owner registry per owning hub, registered ONCE.</b> The hub overload of
/// <c>AutoConnectOwnedBy</c> mints a fresh <see cref="CompositeDisposable"/> and adds it to the hub's
/// append-only <c>RegisterForDisposal</c> on EVERY call. A settled connection removes its handle from
/// that composite, never the composite from the hub — so calling it per read left one empty
/// registrant on the long-lived read-issuing hub for every read of every wave, for the life of the
/// process. Each owner therefore gets one composite, registered with it the first time it is used,
/// and every connection is added to that composite on the mesh's <see cref="ReleaseLane"/>.</para>
/// </summary>
internal sealed class RecycleCascadeReads : IDisposable
{
    // One owner registry per owning hub (in production, the one read-issuing hub of the mesh),
    // registered with that hub once. Keyed by reference: a hub's identity is the instance.
    private ImmutableDictionary<IMessageHub, CompositeDisposable> owned =
        ImmutableDictionary.Create<IMessageHub, CompositeDisposable>(ReferenceEqualityComparer.Instance);

    private readonly PromiseCache<string, ImmutableDictionary<string, NodeTypeDefinition?>> definitions =
        new(StringComparer.Ordinal);

    private readonly PromiseCache<string, ImmutableList<string>> instances =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The single key of the NodeType-definition enumeration.</summary>
    private const string DefinitionsKey = "nodeType:NodeType";

    /// <summary>The mesh's NodeType definitions — joining a read already in flight.</summary>
    /// <param name="owner">The hub that owns the shared connection (the mesh's read-issuing hub).</param>
    /// <param name="read">The one read, run only when none is in flight.</param>
    public IObservable<ImmutableDictionary<string, NodeTypeDefinition?>> Definitions(
        IMessageHub owner, Func<IObservable<ImmutableDictionary<string, NodeTypeDefinition?>>> read) =>
        Shared(definitions, DefinitionsKey, owner, read);

    /// <summary>The instances of <paramref name="type"/> — joining a read already in flight.</summary>
    /// <param name="type">The NodeType whose instances are asked for.</param>
    /// <param name="owner">The hub that owns the shared connection (the mesh's read-issuing hub).</param>
    /// <param name="read">The one read, run only when none is in flight for this type.</param>
    public IObservable<ImmutableList<string>> Instances(
        string type, IMessageHub owner, Func<IObservable<ImmutableList<string>>> read) =>
        Shared(instances, type, owner, read);

    private IObservable<T> Shared<T>(
        PromiseCache<string, T> cache, string key, IMessageHub owner, Func<IObservable<T>> read) =>
        Observable.Defer(() =>
        {
            var promise = cache.GetOrAdd(key, _ => read()
                .Take(1)
                .Replay(1)
                .AutoConnectOwnedBy(
                    OwnerRegistry(owner),
                    owner.ServiceProvider.GetRequiredService<ReleaseLane>(),
                    nameof(RecycleCascadeReads)));
            // Released on ANY terminal, pair-exact: the read is shared while in flight and never
            // outlives it, so the next wave reads the index afresh and a replacement another caller
            // already started is never dropped.
            return promise.Finally(() => cache.Release(key, promise));
        });

    /// <summary>
    /// The one owner registry for <paramref name="owner"/>: created and registered with the hub the
    /// first time the hub is used, reused for every later read. Two racing first calls may both build
    /// one; only the one that wins the dictionary is registered, the loser is never referenced.
    /// </summary>
    private CompositeDisposable OwnerRegistry(IMessageHub owner)
    {
        if (owned.TryGetValue(owner, out var existing))
            return existing;
        var candidate = new CompositeDisposable();
        var winner = ImmutableInterlocked.GetOrAdd(ref owned, owner, candidate);
        if (ReferenceEquals(winner, candidate))
            owner.RegisterForDisposal(candidate);
        return winner;
    }

    /// <summary>Releases every connection still owned, should the container dispose this before its hubs.</summary>
    public void Dispose()
    {
        foreach (var registry in owned.Values)
            registry.Dispose();
    }
}
