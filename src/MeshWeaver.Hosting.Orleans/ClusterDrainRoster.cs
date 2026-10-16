using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// This silo's view of which OTHER silos have begun to drain (#6865) — the cluster-wide drain signal
/// that Orleans membership cannot give, because a pod in its preStop drain stays <c>Active</c> there.
///
/// <para>Filled by <see cref="SiloDrainNoticeGrain"/> on this silo, which a draining silo calls through
/// <c>ClusterDrainAnnouncer</c>; read SYNCHRONOUSLY by <c>MessageHubGrain</c> when it picks the silo to
/// hand an instance singleton to, so a draining silo is never chosen. A mesh-owned silo singleton
/// (never <c>static</c>): it dies with the silo.</para>
///
/// <para>Entries are silo addresses, which carry the silo's generation, so an address is never
/// reused by a later process; <see cref="Retain"/> drops the ones membership no longer lists or
/// reports dead, so the set stays as small as the silos currently draining.</para>
/// </summary>
/// <param name="logger">Logger for the drain notices this silo records.</param>
internal sealed class ClusterDrainRoster(ILogger<ClusterDrainRoster> logger) : IDisposable
{
    private ImmutableHashSet<SiloAddress> draining = ImmutableHashSet<SiloAddress>.Empty;
    private readonly ISubject<Unit> changed = Subject.Synchronize(new Subject<Unit>());

    /// <summary>The silos currently known to be draining.</summary>
    public ImmutableHashSet<SiloAddress> Current => Volatile.Read(ref draining);

    /// <summary>True when <paramref name="silo"/> has announced that it is draining.</summary>
    /// <param name="silo">The silo to ask about.</param>
    public bool IsDraining(SiloAddress silo) => Current.Contains(silo);

    /// <summary>
    /// The roster now and on every change — the current set is emitted on Subscribe, after the
    /// change subscription is in place, so no change between the two can be missed.
    /// </summary>
    public IObservable<ImmutableHashSet<SiloAddress>> Changes =>
        Observable.Create<ImmutableHashSet<SiloAddress>>(observer =>
        {
            var subscription = changed.Subscribe(_ => observer.OnNext(Current));
            observer.OnNext(Current);
            return subscription;
        });

    /// <summary>Records that <paramref name="silo"/> has begun to drain.</summary>
    /// <param name="silo">The draining silo.</param>
    /// <returns><c>true</c> when this is news to this silo.</returns>
    public bool MarkDraining(SiloAddress silo)
    {
        if (!ImmutableInterlocked.Update(ref draining, set => set.Add(silo)))
            return false;
        logger.LogInformation(
            "[DRAIN] silo {Silo} has begun to drain — no instance singleton will be handed off to it",
            silo);
        changed.OnNext(Unit.Default);
        return true;
    }

    /// <summary>Drops every entry <paramref name="stillListed"/> rejects — a silo that has left.</summary>
    /// <param name="stillListed">True for a silo membership still lists as not dead.</param>
    public void Retain(Func<SiloAddress, bool> stillListed)
    {
        if (ImmutableInterlocked.Update(ref draining, set => set.Where(stillListed).ToImmutableHashSet()))
            changed.OnNext(Unit.Default);
    }

    /// <inheritdoc />
    public void Dispose() => changed.OnCompleted();
}

/// <summary>
/// The hand-off target of a draining instance singleton (#6865): RENDEZVOUS hashing over the grain key
/// and each candidate silo's identity, with a FIXED hash.
///
/// <para>🚨 <b>Fixed, never <c>string.GetHashCode</c>.</b> .NET randomises string hashing per PROCESS,
/// so the previous spread (<c>StringComparer.Ordinal.GetHashCode(key) % candidates</c>) gave every pod
/// its own answer for the same address. Two pods draining at once could therefore keep choosing EACH
/// OTHER. Here every process ranks the candidates for one address identically, so independent
/// draining pods agree on where it goes, and removing one candidate moves only the addresses that
/// had ranked it first.</para>
/// </summary>
internal static class DrainHandOffTarget
{
    /// <summary>
    /// The candidate with the highest <see cref="Score"/> for <paramref name="key"/>, or <c>null</c>
    /// when there is none. Ties (practically impossible) break by ordinal identity, so the answer never
    /// depends on the candidates' order.
    /// </summary>
    /// <param name="key">The grain key being handed off.</param>
    /// <param name="candidates">The silos it may go to.</param>
    /// <returns>The target silo, or <c>null</c>.</returns>
    public static SiloAddress? Choose(string key, IEnumerable<SiloAddress> candidates) =>
        candidates
            .Select(silo => (Silo: silo, Identity: silo.ToParsableString()))
            .OrderByDescending(c => Score(key, c.Identity))
            .ThenBy(c => c.Identity, StringComparer.Ordinal)
            .Select(c => c.Silo)
            .FirstOrDefault();

    /// <summary>
    /// FNV-1a (64-bit) over the key's and the silo identity's UTF-16 code units, separated by a NUL,
    /// finished with the MurmurHash3 64-bit mixer so that identities differing only in their last
    /// characters (consecutive ports, generations) still spread. Identical in every process.
    /// </summary>
    /// <param name="key">The grain key.</param>
    /// <param name="siloIdentity">The candidate silo's parsable address.</param>
    /// <returns>The candidate's rank for this key — higher wins.</returns>
    internal static ulong Score(string key, string siloIdentity)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var c in key)
            hash = (hash ^ c) * prime;
        hash = (hash ^ 0) * prime;
        foreach (var c in siloIdentity)
            hash = (hash ^ c) * prime;
        hash ^= hash >> 33;
        hash *= 0xff51afd7ed558ccdUL;
        hash ^= hash >> 33;
        hash *= 0xc4ceb9fe1a85ec53UL;
        hash ^= hash >> 33;
        return hash;
    }
}
